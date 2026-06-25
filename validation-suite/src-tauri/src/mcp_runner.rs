//! MCP CLI subprocess runner (phase-2 task 5).
//!
//! Invokes the engine CLI (`unity-open-mcp`) as a subprocess to run MCP
//! tools and health checks. This is the loose-coupling boundary: the
//! suite never embeds the MCP server; it shells out + parses JSON, so
//! engine upgrades don't require a suite rebuild (idea.md →
//! Relationship to MCP server).
//!
//! CLI invocations (unity.md → MCP CLI integration):
//!   unity-open-mcp run-tool <name> --project <path> --json [--args '<json>']
//!   unity-open-mcp status    --project <path> --json
//!   unity-open-mcp ping      --project <path> --json
//!
//! The runner sets `UNITY_PROJECT_PATH` (and an optional port override)
//! for parity with MCP-client invocations, but also passes `--project`
//! explicitly so a flag always wins (unity.md env notes).

use std::path::PathBuf;
use std::process::Command;
use std::time::Duration;

// `wait-timeout` adds a bounded `wait_timeout` to std::process::Child so
// the runner can kill an MCP CLI that hangs (phase-2: timeout capture).
use wait_timeout::ChildExt;

use crate::schemas::{ActionResult, ActionLogLine, ActionLogLevel, McpResult};
use crate::schemas::EngineProfile;

/// Default per-call timeout for an MCP CLI invocation (ms).
pub const DEFAULT_TIMEOUT_MS: u64 = 30_000;

/// Resolved CLI binary location. v1 resolves from `PATH` (the same
/// discovery the operator's MCP client uses). Future work can resolve
/// from a toolkit-root setting.
fn resolve_binary(profile: &EngineProfile) -> Result<PathBuf, String> {
    // We rely on the OS PATH lookup (Command::new does this), so we just
    // return the binary name. If we ever add a toolkit-root setting, this
    // is where it would join.
    Ok(PathBuf::from(&profile.mcp_cli_binary))
}

/// Build the base Command for an invocation, with env + project set.
fn base_command(binary: &PathBuf, project_root: &str, port: Option<u16>) -> Command {
    let mut cmd = Command::new(binary);
    cmd.env("UNITY_PROJECT_PATH", project_root);
    if let Some(p) = port {
        cmd.env("UNITY_OPEN_MCP_BRIDGE_PORT", p.to_string());
    }
    cmd.arg("--project").arg(project_root);
    cmd
}

/// Capture stdout+stderr + exit code within a timeout. Returns the joined
/// output lines and the parsed JSON body (the CLI emits one JSON document).
struct ProcOutput {
    exit_code: Option<i32>,
    stdout: String,
    stderr: String,
    timed_out: bool,
}

fn run_with_timeout(mut cmd: Command, timeout_ms: u64) -> Result<ProcOutput, String> {
    let mut child = cmd
        .stdout(std::process::Stdio::piped())
        .stderr(std::process::Stdio::piped())
        .spawn()
        .map_err(|e| format!("Failed to spawn MCP CLI: {e}"))?;
    let timeout = Duration::from_millis(timeout_ms);
    match child.wait_timeout(timeout) {
        Ok(Some(status)) => {
            let stdout = child.stdout.take().map(read_to_string).unwrap_or(Ok(String::new()))?;
            let stderr = child.stderr.take().map(read_to_string).unwrap_or(Ok(String::new()))?;
            Ok(ProcOutput {
                exit_code: status.code(),
                stdout,
                stderr,
                timed_out: false,
            })
        }
        Ok(None) => {
            // Timed out — kill the child best-effort.
            let _ = child.kill();
            let _ = child.wait();
            Ok(ProcOutput {
                exit_code: None,
                stdout: String::new(),
                stderr: String::new(),
                timed_out: true,
            })
        }
        Err(e) => Err(format!("MCP CLI wait failed: {e}")),
    }
}

fn read_to_string<R: std::io::Read>(mut r: R) -> Result<String, String> {
    let mut buf = String::new();
    r.read_to_string(&mut buf).map_err(|e| e.to_string())?;
    Ok(buf)
}

/// Run an MCP tool via `run-tool` and parse its JSON result. The result
/// body surfaces `isError` so the UI can distinguish tool errors from
/// transport errors (phase-2 deliverable: surface `isError` + tool body).
pub fn run_tool(
    profile: &EngineProfile,
    project_root: &str,
    tool: &str,
    args: Option<&serde_json::Value>,
    timeout_ms: Option<u64>,
    port: Option<u16>,
) -> Result<ActionResult, String> {
    let binary = resolve_binary(profile)?;
    let mut cmd = base_command(&binary, project_root, port);
    cmd.arg("run-tool").arg(tool).arg("--json");
    if let Some(args_val) = args {
        let args_str = serde_json::to_string(args_val)
            .map_err(|e| format!("serialize tool args: {e}"))?;
        cmd.arg("--args").arg(args_str);
    }
    let timeout = timeout_ms.unwrap_or(DEFAULT_TIMEOUT_MS);
    let out = run_with_timeout(cmd, timeout)?;
    Ok(build_tool_result(tool, &out, timeout))
}

/// Run a health check (`status` or `ping`). Returns the parsed JSON body
/// in an [`ActionResult`] so the UI can show it in the action log.
pub fn run_health(
    profile: &EngineProfile,
    project_root: &str,
    subcommand: &str,
    timeout_ms: Option<u64>,
    port: Option<u16>,
) -> Result<ActionResult, String> {
    debug_assert!(subcommand == "status" || subcommand == "ping");
    let binary = resolve_binary(profile)?;
    let mut cmd = base_command(&binary, project_root, port);
    cmd.arg(subcommand).arg("--json");
    let timeout = timeout_ms.unwrap_or(DEFAULT_TIMEOUT_MS);
    let out = run_with_timeout(cmd, timeout)?;
    Ok(build_health_result(subcommand, &out, timeout))
}

/// Turn a run-tool proc output into an ActionResult. A tool-level error
/// (`isError: true`) is still `ok: true` at the action level — the suite
/// records it and surfaces `mcp.isError` so the operator can inspect it.
/// Transport/timeout/parse failures are `ok: false`.
fn build_tool_result(tool: &str, out: &ProcOutput, timeout_ms: u64) -> ActionResult {
    if out.timed_out {
        return ActionResult::err(
            format!("mcp_tool {tool} timed out after {timeout_ms} ms"),
            format!("MCP CLI timed out after {timeout_ms} ms."),
        );
    }
    let trimmed = out.stdout.trim();
    if trimmed.is_empty() {
        return ActionResult::err(
            format!("mcp_tool {tool} produced no output"),
            format!(
                "MCP CLI produced no output.{}",
                stderr_snippet(&out.stderr)
            ),
        )
        .with_stderr(&out.stderr);
    }
    let parsed: serde_json::Value = match serde_json::from_str(trimmed) {
        Ok(v) => v,
        Err(e) => {
            return ActionResult::err(
                format!("mcp_tool {tool} returned non-JSON"),
                format!("MCP CLI output was not JSON: {e}{}", stderr_snippet(&out.stderr)),
            )
            .with_stderr(&out.stderr);
        }
    };
    let is_error = parsed
        .get("isError")
        .and_then(|v| v.as_bool())
        .unwrap_or(false);
    let result_body = parsed.get("result").cloned().unwrap_or(parsed.clone());
    ActionResult {
        ok: true,
        summary: format!("mcp_tool {tool}: {}", if is_error { "tool error" } else { "ok" }),
        logs: vec![ActionLogLine {
            level: if is_error { ActionLogLevel::Warn } else { ActionLogLevel::Info },
            message: format!("run-tool {tool} (exit {:?})", out.exit_code),
            snippet: stderr_snippet(&out.stderr).is_empty().then(|| out.stdout.clone()),
        }],
        entries: Vec::new(),
        mcp: Some(McpResult { is_error, result: result_body }),
    }
}

/// Turn a status/ping proc output into an ActionResult.
fn build_health_result(subcommand: &str, out: &ProcOutput, timeout_ms: u64) -> ActionResult {
    if out.timed_out {
        return ActionResult::err(
            format!("{subcommand} timed out after {timeout_ms} ms"),
            format!("MCP CLI timed out after {timeout_ms} ms."),
        );
    }
    let trimmed = out.stdout.trim();
    if trimmed.is_empty() {
        return ActionResult::err(
            format!("{subcommand} produced no output"),
            format!("MCP CLI produced no output.{}", stderr_snippet(&out.stderr)),
        )
        .with_stderr(&out.stderr);
    }
    let parsed: serde_json::Value = match serde_json::from_str(trimmed) {
        Ok(v) => v,
        Err(e) => {
            return ActionResult::err(
                format!("{subcommand} returned non-JSON"),
                format!("MCP CLI output was not JSON: {e}{}", stderr_snippet(&out.stderr)),
            )
            .with_stderr(&out.stderr);
        }
    };
    // For ping/status, a non-zero exit signals an unhealthy bridge; the
    // JSON still carries useful detail, so we report ok=true with the body.
    ActionResult {
        ok: true,
        summary: format!("{subcommand} ok"),
        logs: vec![ActionLogLine {
            level: ActionLogLevel::Info,
            message: format!("{subcommand} (exit {:?})", out.exit_code),
            snippet: Some(out.stdout.clone()),
        }],
        entries: Vec::new(),
        mcp: Some(McpResult { is_error: false, result: parsed }),
    }
}

fn stderr_snippet(stderr: &str) -> String {
    let trimmed = stderr.trim();
    if trimmed.is_empty() {
        String::new()
    } else {
        // Keep the snippet bounded so the log stays readable.
        let snippet: String = trimmed.chars().take(500).collect();
        format!("\nstderr: {snippet}")
    }
}

/// Convenience trait to attach a stderr snippet to an error result's log.
trait WithStderr {
    fn with_stderr(self, stderr: &str) -> Self;
}

impl WithStderr for ActionResult {
    fn with_stderr(mut self, stderr: &str) -> Self {
        let trimmed = stderr.trim();
        if !trimmed.is_empty() {
            // Fold stderr into the existing error log line's snippet.
            if let Some(first) = self.logs.first_mut() {
                first.snippet = Some(format!("{trimmed}"));
            }
        }
        self
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn profile() -> EngineProfile {
        EngineProfile {
            id: "unity".to_string(),
            display_name: "Unity".to_string(),
            mcp_cli_binary: "unity-open-mcp".to_string(),
            paths: crate::schemas::ProfilePaths {
                fixture_root: "Assets/_ValidationSuite/<test-id>/".to_string(),
                state_root: "UserSettings/ValidationSuite/".to_string(),
                state_file: "UserSettings/ValidationSuite/.state.json".to_string(),
                actuals_dir: "UserSettings/ValidationSuite/actuals/".to_string(),
                exports_dir: "UserSettings/ValidationSuite/exports/".to_string(),
            },
            markers: crate::schemas::ProjectMarkers {
                dirs: vec![],
                files: vec![],
            },
            companions: vec![],
            placeholders: vec![],
            tool_name_prefix: "unity_open_mcp_".to_string(),
        }
    }

    #[test]
    fn build_tool_result_parses_is_error_and_body() {
        let out = ProcOutput {
            exit_code: Some(0),
            stdout: serde_json::to_string(&json!({
                "command": "run-tool",
                "tool": "unity_open_mcp_ping",
                "isError": false,
                "result": { "ok": true }
            }))
            .unwrap(),
            stderr: String::new(),
            timed_out: false,
        };
        let res = build_tool_result("unity_open_mcp_ping", &out, 1000);
        assert!(res.ok);
        let mcp = res.mcp.unwrap();
        assert!(!mcp.is_error);
        assert_eq!(mcp.result, json!({ "ok": true }));
    }

    #[test]
    fn build_tool_result_surfaces_tool_error_as_ok_with_is_error() {
        let out = ProcOutput {
            exit_code: Some(1),
            stdout: serde_json::to_string(&json!({
                "command": "run-tool",
                "tool": "x",
                "isError": true,
                "result": { "error": "boom" }
            }))
            .unwrap(),
            stderr: String::new(),
            timed_out: false,
        };
        let res = build_tool_result("x", &out, 1000);
        assert!(res.ok); // transport ok; tool error flagged below
        assert!(res.mcp.unwrap().is_error);
        assert_eq!(res.logs[0].level, ActionLogLevel::Warn);
    }

    #[test]
    fn build_tool_result_timeout_is_failure() {
        let out = ProcOutput {
            exit_code: None,
            stdout: String::new(),
            stderr: String::new(),
            timed_out: true,
        };
        let res = build_tool_result("x", &out, 1000);
        assert!(!res.ok);
        assert!(res.summary.contains("timed out"));
    }

    #[test]
    fn build_tool_result_non_json_is_failure() {
        let out = ProcOutput {
            exit_code: Some(0),
            stdout: "not json at all".to_string(),
            stderr: "some stderr".to_string(),
            timed_out: false,
        };
        let res = build_tool_result("x", &out, 1000);
        assert!(!res.ok);
        assert!(res.logs[0].snippet.as_deref().unwrap().contains("some stderr"));
    }

    #[test]
    fn build_tool_result_empty_output_is_failure() {
        let out = ProcOutput {
            exit_code: Some(127),
            stdout: String::new(),
            stderr: "command not found".to_string(),
            timed_out: false,
        };
        let res = build_tool_result("x", &out, 1000);
        assert!(!res.ok);
        assert!(res.logs[0].snippet.as_deref().unwrap().contains("command not found"));
    }
}
