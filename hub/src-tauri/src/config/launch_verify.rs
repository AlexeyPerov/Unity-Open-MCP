//! M4 Plan 5 — wizard Step 5 launch + HTTP `/ping` verification.
//!
//! The Step 5 surface owns **two** Tauri commands:
//!
//! - [`launch_for_verify`] — spawns Unity for the project with
//!   the bridge port pinned via the `-UNITY_AGENT_BRIDGE_PORT`
//!   command-line argument **and** the `UNITY_AGENT_BRIDGE_PORT`
//!   env var (the bridge package reads the env var; the CLI
//!   form is here for parity with `packages/bridge.md` §HTTP
//!   API). This is the same launch flow as the regular
//!   `launch_project` command but with the bridge port layered
//!   in so the in-Editor connector listens on the port the
//!   wizard Step 5 is about to poll.
//! - [`poll_bridge_ping`] — single GET on `127.0.0.1:{port}/ping`
//!   with a configurable timeout. Returns the parsed body
//!   (per `packages/bridge.md` §HTTP API) or a structured
//!   `BridgePingError` with a `kind` the UI can branch on
//!   (`timeout`, `connectionRefused`, `unreachable`, `httpError`,
//!   `malformedBody`).
//!
//! Step 5 **never** spawns a separate `unity-agent-mcp`
//! subprocess to call `unity_agent_ping` (per `questions-4.md`
//! Q8 = B) — the wizard goes straight to the bridge HTTP
//! endpoint.

use std::io::{Read, Write};
use std::net::{Ipv4Addr, Shutdown, SocketAddr, TcpStream, ToSocketAddrs};
use std::path::Path;
use std::time::Duration;

use serde::{Deserialize, Serialize};
use tauri::State;

use crate::config::commands::AppState;
use crate::config::env_vars;
use crate::config::launch::{read_project_version, resolve_install_for_version};
use crate::config::launch_log::{self, LaunchOutcome};
use crate::config::persistence;

/// Default bridge HTTP port. Mirrors the `DEFAULT_BRIDGE_PORT`
/// exported from `ai_toolkit.ts` and the value documented in
/// `packages/bridge.md` §HTTP API. The wizard Step 5 polls
/// `127.0.0.1:{DEFAULT_PORT}/ping` whenever the user does not
/// override the port in Step 4.
pub const DEFAULT_BRIDGE_PORT: u16 = 19120;

/// Default `/ping` request timeout. The spec gives the wizard
/// a 120 s window for the bridge to come up; we keep the
/// per-request timeout smaller (10 s) so a hung connection
/// surfaces as a `timeout` error well before the overall
/// wizard step times out. The wizard UI is the only caller
/// of this command and orchestrates the 120 s budget itself
/// across multiple polls.
pub const DEFAULT_PING_TIMEOUT_MS: u64 = 10_000;

/// Hard cap on the per-request timeout the wizard can request.
/// 30 s is well below the 120 s Step 5 budget and gives a
/// hung TCP connection a chance to bail quickly.
pub const MAX_PING_TIMEOUT_MS: u64 = 30_000;

/// Per-bridge-poll result. Mirrors the body the bridge
/// `/ping` endpoint emits (see `packages/bridge.md` §HTTP API):
/// `connected`, `compiling`, `is_playing`, `projectPath`,
/// `unityVersion`. Any field the bridge omits from its body
/// surfaces as `None` / `false` here so the wizard chip copy
/// stays stable.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BridgePingResult {
    /// Resolved bridge port that was polled. Surfaced on Done
    /// so the user can confirm the port the wizard wrote into
    /// the MCP config in Step 4.
    pub port: u16,
    /// Wall-clock duration of the single poll request.
    pub duration_ms: u64,
    /// `true` when the TCP connection was established and the
    /// bridge returned HTTP 200 with a parseable JSON body.
    /// `false` for any failure (timeout, refused, malformed).
    pub ok: bool,
    /// `true` when the bridge `connected` field is `true`.
    /// Always `false` when `ok` is `false`.
    pub connected: bool,
    /// `true` when the bridge reports a compile is in flight.
    /// Cleared (set to `false`) when the request itself fails.
    pub compiling: bool,
    /// `true` when the Editor is in play mode.
    pub is_playing: bool,
    /// Project path the bridge is bound to (when reported).
    pub project_path: Option<String>,
    /// Bridge / Unity version string (when reported).
    pub unity_version: Option<String>,
    /// Optional raw response body — useful for the Step 5 log
    /// tail on failure.
    pub raw: Option<String>,
    /// One of `"timeout"`, `"connectionRefused"`,
    /// `"unreachable"`, `"httpError"`, `"malformedBody"`. Empty
    /// when `ok` is `true`.
    pub error_kind: String,
    /// Optional human-readable failure message. Empty when
    /// `ok` is `true`.
    pub error_message: String,
}

impl BridgePingResult {
    fn failure(port: u16, kind: &str, message: impl Into<String>, duration_ms: u64) -> Self {
        Self {
            port,
            duration_ms,
            ok: false,
            connected: false,
            compiling: false,
            is_playing: false,
            project_path: None,
            unity_version: None,
            raw: None,
            error_kind: kind.to_string(),
            error_message: message.into(),
        }
    }
}

/// Inputs to [`launch_for_verify`]. The wizard Step 5 always
/// knows the project id, the active theme, and the bridge port
/// the user picked in Step 4; we collect them in one struct so
/// the Rust side can layer the port into the launch command
/// without a second round-trip.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LaunchForVerifyParams {
    pub project_id: String,
    /// Bridge port to pass to Unity via
    /// `-UNITY_AGENT_BRIDGE_PORT` / `UNITY_AGENT_BRIDGE_PORT`.
    /// The wizard always uses the port the user picked in Step
    /// 4 — `DEFAULT_BRIDGE_PORT` is just a default the caller
    /// picks when the user did not override.
    #[serde(default = "default_port")]
    pub bridge_port: u16,
    /// M1.5-18: active Hub theme at the time of the launch.
    /// `None` ⇒ `"system"` (matches `launch_project`).
    pub theme: Option<String>,
}

fn default_port() -> u16 {
    DEFAULT_BRIDGE_PORT
}

/// Tauri command: launch Unity for the Step 5 verify flow. The
/// command is a thin wrapper around the same launch pipeline
/// `launch_project` uses (install resolution, version refresh,
/// env-var layering, `last_launch_pid` bookkeeping) with one
/// addition: the bridge port is appended to the launch args
/// (`-UNITY_AGENT_BRIDGE_PORT=<port>`) and the same value is
/// set as the `UNITY_AGENT_BRIDGE_PORT` env var so the bridge
/// package reads it on Editor startup.
#[tauri::command]
pub fn launch_for_verify(
    state: State<AppState>,
    params: LaunchForVerifyParams,
) -> Result<LaunchForVerifyResult, LaunchForVerifyError> {
    let resolved_theme = params
        .theme
        .as_deref()
        .map(|t| if t == "dark" || t == "light" { t } else { "system" })
        .unwrap_or("system");
    let port = params.bridge_port;
    let project_id = params.project_id.clone();
    match launch_for_verify_inner(&state, &params) {
        Ok(result) => {
            let record = launch_log::build_record(
                &result.project_id,
                &result.project_name,
                &result.project_path,
                result.unity_version.as_deref(),
                Some(&result.executable_path),
                Some(result.pid),
                &result.launch_args,
                result.build_target.as_deref(),
                LaunchOutcome::Ok {
                    pid: result.pid,
                    unity_version: result.unity_version.clone(),
                    executable_path: result.executable_path.clone(),
                },
                Some(resolved_theme),
            );
            launch_log::append_record_async(record);
            Ok(LaunchForVerifyResult {
                project_id: result.project_id,
                pid: result.pid,
                unity_version: result.unity_version,
                executable_path: result.executable_path,
                bridge_port: port,
            })
        }
        Err(err) => {
            let record = launch_log::build_record(
                &project_id,
                &err.project_name,
                &err.project_path,
                err.unity_version.as_deref(),
                err.install_path.as_deref(),
                None,
                &err.launch_args,
                err.build_target.as_deref(),
                LaunchOutcome::Error {
                    code: err.code,
                    message: err.message,
                },
                Some(resolved_theme),
            );
            launch_log::append_record_async(record);
            Err(err.typed)
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LaunchForVerifyResult {
    pub project_id: String,
    pub pid: u32,
    pub unity_version: Option<String>,
    pub executable_path: String,
    /// Bridge port the wizard used in this launch.
    pub bridge_port: u16,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "camelCase")]
pub enum LaunchForVerifyError {
    #[serde(rename_all = "camelCase")]
    ProjectNotFound { project_id: String },
    #[serde(rename_all = "camelCase")]
    PathInvalid { project_id: String, path: String },
    #[serde(rename_all = "camelCase")]
    VersionMissing { project_id: String },
    #[serde(rename_all = "camelCase")]
    InstallNotFound { project_id: String, version: String },
    #[serde(rename_all = "camelCase")]
    LaunchFailed { project_id: String, message: String },
    #[serde(rename_all = "camelCase")]
    PortInvalid { port: u16 },
}

impl std::fmt::Display for LaunchForVerifyError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            LaunchForVerifyError::ProjectNotFound { project_id } => {
                write!(f, "project not found: {}", project_id)
            }
            LaunchForVerifyError::PathInvalid { path, .. } => write!(f, "path invalid: {}", path),
            LaunchForVerifyError::VersionMissing { .. } => write!(f, "unity version missing"),
            LaunchForVerifyError::InstallNotFound { version, .. } => {
                write!(f, "unity {} is not installed", version)
            }
            LaunchForVerifyError::LaunchFailed { message, .. } => {
                write!(f, "Failed to launch Unity: {}", message)
            }
            LaunchForVerifyError::PortInvalid { port } => {
                write!(f, "bridge port {} is not a valid TCP port", port)
            }
        }
    }
}

#[derive(Debug, Clone)]
struct InnerLaunchForVerify {
    project_id: String,
    project_name: String,
    project_path: String,
    pid: u32,
    unity_version: Option<String>,
    executable_path: String,
    launch_args: Vec<String>,
    build_target: Option<String>,
}

#[derive(Debug, Clone)]
struct InnerLaunchForVerifyError {
    typed: LaunchForVerifyError,
    project_name: String,
    project_path: String,
    unity_version: Option<String>,
    install_path: Option<String>,
    launch_args: Vec<String>,
    build_target: Option<String>,
    code: String,
    message: String,
}

fn launch_for_verify_inner(
    state: &State<AppState>,
    params: &LaunchForVerifyParams,
) -> Result<InnerLaunchForVerify, InnerLaunchForVerifyError> {
    if params.bridge_port == 0 {
        return Err(InnerLaunchForVerifyError {
            typed: LaunchForVerifyError::PortInvalid { port: 0 },
            project_name: String::new(),
            project_path: String::new(),
            unity_version: None,
            install_path: None,
            launch_args: Vec::new(),
            build_target: None,
            code: "portInvalid".to_string(),
            message: "bridge port 0 is not a valid TCP port".to_string(),
        });
    }
    let projects = {
        let guard = state.projects.lock().unwrap();
        guard.clone()
    };
    let project = match projects.projects.iter().find(|p| p.id == params.project_id) {
        Some(p) => p.clone(),
        None => {
            return Err(InnerLaunchForVerifyError {
                typed: LaunchForVerifyError::ProjectNotFound {
                    project_id: params.project_id.clone(),
                },
                project_name: String::new(),
                project_path: String::new(),
                unity_version: None,
                install_path: None,
                launch_args: Vec::new(),
                build_target: None,
                code: "projectNotFound".to_string(),
                message: format!("project not found: {}", params.project_id),
            });
        }
    };
    let project_path_str = project.path.clone();
    let project_name = project.name.clone();
    let project_path = Path::new(&project_path_str);
    if !project_path.exists() {
        return Err(InnerLaunchForVerifyError {
            typed: LaunchForVerifyError::PathInvalid {
                project_id: params.project_id.clone(),
                path: project.path.clone(),
            },
            project_name,
            project_path: project_path_str,
            unity_version: None,
            install_path: None,
            launch_args: Vec::new(),
            build_target: None,
            code: "pathInvalid".to_string(),
            message: format!("path invalid: {}", project.path),
        });
    }
    let refreshed_version = read_project_version(project_path);
    let unity_version = refreshed_version.clone().or(project.unity_version.clone());
    let version = match unity_version {
        Some(v) => v,
        None => {
            return Err(InnerLaunchForVerifyError {
                typed: LaunchForVerifyError::VersionMissing {
                    project_id: params.project_id.clone(),
                },
                project_name,
                project_path: project_path_str,
                unity_version: None,
                install_path: None,
                launch_args: Vec::new(),
                build_target: None,
                code: "versionMissing".to_string(),
                message: "unity version missing".to_string(),
            });
        }
    };
    let (executable, install_path) = match resolve_install_for_version(&state, &version) {
        Some(v) => v,
        None => {
            return Err(InnerLaunchForVerifyError {
                typed: LaunchForVerifyError::InstallNotFound {
                    project_id: params.project_id.clone(),
                    version: version.clone(),
                },
                project_name,
                project_path: project_path_str,
                unity_version: Some(version.clone()),
                install_path: None,
                launch_args: Vec::new(),
                build_target: None,
                code: "installNotFound".to_string(),
                message: format!("unity {} is not installed", version),
            });
        }
    };
    let mut args: Vec<String> = Vec::new();
    let mut build_target: Option<String> = None;
    // Always pass -projectPath so the Hub launcher flow opens the
    // right project even when the wizard is the one driving the
    // launch (the regular launch_project flow layers this same
    // argument).
    args.push("-projectPath".to_string());
    args.push(project.path.clone());
    if let Some(ref launch_args) = project.launch_args {
        if !launch_args.is_empty() {
            for arg in launch_args.split_whitespace() {
                args.push(arg.to_string());
            }
        }
    }
    if let Some(ref platform_intent) = project.platform_intent {
        if !platform_intent.is_empty() {
            args.push("-buildTarget".to_string());
            args.push(platform_intent.clone());
            build_target = Some(platform_intent.clone());
        }
    }
    // Pin the bridge port both as a launch arg (for parity with
    // `packages/bridge.md` §HTTP API) and as an env var (the
    // bridge package reads the env var first). The arg is
    // additive — we never strip user-provided args above.
    let port_arg = format!("-UNITY_AGENT_BRIDGE_PORT={}", params.bridge_port);
    args.push(port_arg.clone());
    let mut command = std::process::Command::new(&executable);
    command.args(&args);
    env_vars::apply_to_command(&mut command, &project.env_vars);
    command.env("UNITY_AGENT_BRIDGE_PORT", params.bridge_port.to_string());
    let child = match command.spawn() {
        Ok(c) => c,
        Err(e) => {
            return Err(InnerLaunchForVerifyError {
                typed: LaunchForVerifyError::LaunchFailed {
                    project_id: params.project_id.clone(),
                    message: format!("Failed to spawn Unity: {}", e),
                },
                project_name,
                project_path: project_path_str,
                unity_version: Some(version),
                install_path: Some(install_path),
                launch_args: args,
                build_target,
                code: "launchFailed".to_string(),
                message: format!("Failed to spawn Unity: {}", e),
            });
        }
    };
    let pid = child.id();
    let mut projects = projects;
    if let Some(p) = projects.projects.iter_mut().find(|p| p.id == params.project_id) {
        p.last_launch_pid = Some(pid);
        p.last_launch_at = Some(chrono::Utc::now().to_rfc3339());
        p.frecency = p.frecency.saturating_add(1);
        if refreshed_version.is_some() {
            p.unity_version = refreshed_version.clone();
        }
    }
    if let Err(e) = persistence::save_projects(&projects) {
        log::error!("Failed to persist launch data: {}", e);
    }
    {
        let mut guard = state.projects.lock().unwrap();
        *guard = projects;
    }
    Ok(InnerLaunchForVerify {
        project_id: params.project_id.clone(),
        project_name,
        project_path: project_path_str,
        pid,
        unity_version: Some(version),
        executable_path: executable.to_string_lossy().to_string(),
        launch_args: args,
        build_target,
    })
}

/// Tauri command: GET `127.0.0.1:{port}/ping` with the given
/// timeout and return the parsed bridge body. The wizard Step
/// 5 calls this on a 2-3 s cadence until the bridge responds
/// 200 with a parseable body or until the 120 s overall budget
/// elapses.
#[tauri::command]
pub fn poll_bridge_ping(port: u16, timeout_ms: u64) -> BridgePingResult {
    let timeout = Duration::from_millis(timeout_ms.clamp(1, MAX_PING_TIMEOUT_MS));
    poll_bridge_ping_at(port, timeout)
}

fn poll_bridge_ping_at(port: u16, timeout: Duration) -> BridgePingResult {
    let started = std::time::Instant::now();
    let addr: SocketAddr = match (Ipv4Addr::LOCALHOST, port).to_socket_addrs() {
        Ok(mut iter) => match iter.next() {
            Some(a) => a,
            None => {
                return BridgePingResult::failure(
                    port,
                    "unreachable",
                    "could not resolve 127.0.0.1",
                    started.elapsed().as_millis() as u64,
                )
            }
        },
        Err(_e) => {
            return BridgePingResult::failure(
                port,
                "unreachable",
                format!("could not resolve 127.0.0.1:{}", port),
                started.elapsed().as_millis() as u64,
            );
        }
    };
    let raw = match http_get_with_timeout(addr, "/ping", timeout) {
        Ok(r) => r,
        Err(PingFailure::Timeout) => {
            return BridgePingResult::failure(
                port,
                "timeout",
                format!(
                    "ping did not respond within {} ms",
                    timeout.as_millis()
                ),
                started.elapsed().as_millis() as u64,
            );
        }
        Err(PingFailure::ConnectionRefused) => {
            return BridgePingResult::failure(
                port,
                "connectionRefused",
                format!("connection refused on 127.0.0.1:{}", port),
                started.elapsed().as_millis() as u64,
            );
        }
        Err(PingFailure::Unreachable(message)) => {
            return BridgePingResult::failure(
                port,
                "unreachable",
                message,
                started.elapsed().as_millis() as u64,
            );
        }
        Err(PingFailure::HttpStatus(code, body)) => {
            return BridgePingResult::failure(
                port,
                "httpError",
                format!("bridge returned HTTP {}", code),
                started.elapsed().as_millis() as u64,
            )
            .with_raw(body);
        }
    };
    let parsed: ParsedBridgePing = match serde_json::from_str(&raw) {
        Ok(p) => p,
        Err(e) => {
            return BridgePingResult::failure(
                port,
                "malformedBody",
                format!("bridge body is not JSON: {}", e),
                started.elapsed().as_millis() as u64,
            )
            .with_raw(raw);
        }
    };
    BridgePingResult {
        port,
        duration_ms: started.elapsed().as_millis() as u64,
        ok: true,
        connected: parsed.connected.unwrap_or(false),
        compiling: parsed.compiling.unwrap_or(false),
        is_playing: parsed.is_playing.unwrap_or(false),
        project_path: parsed.project_path,
        unity_version: parsed.unity_version,
        raw: Some(raw),
        error_kind: String::new(),
        error_message: String::new(),
    }
}

impl BridgePingResult {
    fn with_raw(mut self, raw: String) -> Self {
        self.raw = Some(raw);
        self
    }
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ParsedBridgePing {
    #[serde(default)]
    connected: Option<bool>,
    #[serde(default)]
    compiling: Option<bool>,
    #[serde(default)]
    is_playing: Option<bool>,
    #[serde(default)]
    project_path: Option<String>,
    #[serde(default)]
    unity_version: Option<String>,
}

#[derive(Debug)]
enum PingFailure {
    Timeout,
    ConnectionRefused,
    Unreachable(String),
    HttpStatus(u16, String),
}

/// Perform a minimal HTTP/1.1 GET against the bridge and return
/// the response body. The function uses a `TcpStream` with
/// read/write timeouts so a hung bridge surfaces as a `Timeout`
/// failure without a separate tokio runtime. The bridge only
/// ever answers on `127.0.0.1` so the plain-text request is
/// safe — no proxy / TLS concerns.
fn http_get_with_timeout(
    addr: SocketAddr,
    path: &str,
    timeout: Duration,
) -> Result<String, PingFailure> {
    let mut stream = match TcpStream::connect_timeout(&addr, timeout) {
        Ok(s) => s,
        Err(e) => {
            return Err(match e.kind() {
                std::io::ErrorKind::TimedOut => PingFailure::Timeout,
                std::io::ErrorKind::ConnectionRefused => PingFailure::ConnectionRefused,
                _ => PingFailure::Unreachable(format!(
                    "could not connect to 127.0.0.1:{} ({})",
                    addr.port(),
                    e
                )),
            });
        }
    };
    if let Err(e) = stream.set_read_timeout(Some(timeout)) {
        return Err(PingFailure::Unreachable(format!(
            "could not arm read timeout: {}",
            e
        )));
    }
    if let Err(e) = stream.set_write_timeout(Some(timeout)) {
        return Err(PingFailure::Unreachable(format!(
            "could not arm write timeout: {}",
            e
        )));
    }
    let request = format!(
        "GET {path} HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\nAccept: application/json\r\nUser-Agent: unity-ai-hub/0.1 (Step5-verify)\r\n\r\n",
        path = path
    );
    if let Err(e) = stream.write_all(request.as_bytes()) {
        return Err(match e.kind() {
            std::io::ErrorKind::TimedOut => PingFailure::Timeout,
            _ => PingFailure::Unreachable(format!("write failed: {}", e)),
        });
    }
    let mut response = String::new();
    if let Err(e) = stream.read_to_string(&mut response) {
        return Err(match e.kind() {
            std::io::ErrorKind::TimedOut => PingFailure::Timeout,
            _ => PingFailure::Unreachable(format!("read failed: {}", e)),
        });
    }
    let _ = stream.shutdown(Shutdown::Both);
    split_http_response(&response)
        .unwrap_or_else(|| Err(PingFailure::Unreachable("malformed HTTP response".to_string())))
}

/// Parse `HTTP/1.1 200 OK\r\n…\r\n\r\n{body}` into a
/// `(status, body)` pair. We only care about the status code
/// and the trailing body — bridge responses are short JSON.
/// `None` is returned when the input is not a recognisable HTTP
/// response at all (so the caller can treat it as a transport
/// failure); a non-2xx status surfaces as an `Err(HttpStatus)`.
fn split_http_response(raw: &str) -> Option<Result<String, PingFailure>> {
    let (head, body) = match raw.find("\r\n\r\n") {
        Some(idx) => (&raw[..idx], &raw[idx + 4..]),
        None => match raw.find("\n\n") {
            Some(idx) => (&raw[..idx], &raw[idx + 2..]),
            None => return None,
        },
    };
    let status_line = head.lines().next()?;
    let mut parts = status_line.split_whitespace();
    let _proto = parts.next()?;
    let code_str = parts.next()?;
    let code: u16 = code_str.parse().ok()?;
    if (200..300).contains(&code) {
        Some(Ok(body.to_string()))
    } else {
        Some(Err(PingFailure::HttpStatus(code, body.to_string())))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write as _;
    use std::net::TcpListener;
    use std::thread;
    use std::time::Duration as StdDuration;

    /// Spin up a one-shot TCP listener that replies with
    /// `body_bytes` (after the `HTTP/1.1 200 OK\r\n…\r\n\r\n`
    /// preamble) and returns the bound port. Tests use this to
    /// simulate the bridge without spawning Unity.
    fn spawn_http_server(status: u16, body: &'static str) -> u16 {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        thread::spawn(move || {
            if let Ok((mut stream, _)) = listener.accept() {
                let mut buf = [0u8; 1024];
                let _ = stream.read(&mut buf);
                let response = format!(
                    "HTTP/1.1 {status} OK\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: {len}\r\n\r\n{body}",
                    status = status,
                    len = body.len(),
                    body = body
                );
                let _ = stream.write_all(response.as_bytes());
                let _ = stream.flush();
            }
        });
        port
    }

    /// Like `spawn_http_server` but with an empty body — the
    /// server accepts the connection then immediately closes so
    /// the client sees `ConnectionRefused` semantics on the next
    /// request.
    fn spawn_silent_server() -> u16 {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        thread::spawn(move || {
            if let Ok((stream, _)) = listener.accept() {
                drop(stream);
            }
        });
        port
    }

    #[test]
    fn poll_bridge_ping_succeeds_on_200_with_connected() {
        let port = spawn_http_server(
            200,
            r#"{"connected":true,"compiling":false,"isPlaying":false,"projectPath":"/g/MyGame","unityVersion":"6000.0.1f1"}"#,
        );
        let res = poll_bridge_ping_at(port, StdDuration::from_secs(2));
        assert!(res.ok, "expected ok, got {:?}", res);
        assert_eq!(res.port, port);
        assert!(res.connected);
        assert!(!res.compiling);
        assert!(!res.is_playing);
        assert_eq!(res.project_path.as_deref(), Some("/g/MyGame"));
        assert_eq!(res.unity_version.as_deref(), Some("6000.0.1f1"));
        assert!(res.error_kind.is_empty());
    }

    #[test]
    fn poll_bridge_ping_succeeds_when_only_connected_present() {
        let port = spawn_http_server(200, r#"{"connected":true}"#);
        let res = poll_bridge_ping_at(port, StdDuration::from_secs(2));
        assert!(res.ok);
        assert!(res.connected);
        assert!(!res.compiling);
        assert!(!res.is_playing);
        assert!(res.project_path.is_none());
        assert!(res.unity_version.is_none());
    }

    #[test]
    fn poll_bridge_ping_returns_http_error_for_500() {
        let port = spawn_http_server(500, r#"{"error":"server"}"#);
        let res = poll_bridge_ping_at(port, StdDuration::from_secs(2));
        assert!(!res.ok);
        assert_eq!(res.error_kind, "httpError");
        assert!(res.raw.is_some());
    }

    #[test]
    fn poll_bridge_ping_marks_malformed_body() {
        let port = spawn_http_server(200, "not json at all");
        let res = poll_bridge_ping_at(port, StdDuration::from_secs(2));
        assert!(!res.ok);
        assert_eq!(res.error_kind, "malformedBody");
    }

    #[test]
    fn poll_bridge_ping_times_out_when_server_hangs() {
        // A listener that never sends a byte — the read deadline
        // is the only thing that bails the client out.
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        thread::spawn(move || {
            if let Ok((stream, _)) = listener.accept() {
                // Hold the socket open without writing.
                std::thread::sleep(StdDuration::from_millis(500));
                drop(stream);
            }
        });
        let res = poll_bridge_ping_at(port, StdDuration::from_millis(200));
        assert!(!res.ok);
        assert!(
            res.error_kind == "timeout" || res.error_kind == "unreachable",
            "got kind={}",
            res.error_kind
        );
    }

    #[test]
    fn poll_bridge_ping_reports_refused_on_no_listener() {
        // Pick a port and immediately drop the listener so the
        // port is unbound — the next connect should see
        // ConnectionRefused.
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        drop(listener);
        let res = poll_bridge_ping_at(port, StdDuration::from_millis(500));
        assert!(!res.ok);
        assert_eq!(res.error_kind, "connectionRefused");
    }

    #[test]
    fn poll_bridge_ping_handles_empty_body_after_close() {
        // `spawn_silent_server` accepts then immediately drops
        // the stream so the client sees EOF. The result should
        // surface as a non-`ok` failure of some kind (the
        // exact kind is OS-dependent; the contract we care
        // about is "the result is well-formed and the bridge
        // status stays `Failed`").
        let port = spawn_silent_server();
        let res = poll_bridge_ping_at(port, StdDuration::from_millis(500));
        assert!(!res.ok);
        assert!(!res.connected);
        assert!(!res.error_kind.is_empty());
    }

    #[test]
    fn split_http_response_handles_200_and_body() {
        let raw = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\n{}";
        let res = split_http_response(raw).expect("parseable");
        assert!(matches!(res, Ok(body) if body == "{}"));
    }

    #[test]
    fn split_http_response_returns_http_status_for_non_2xx() {
        let raw = "HTTP/1.1 503 Service Unavailable\r\n\r\n{\"error\":\"x\"}";
        let res = split_http_response(raw).expect("parseable");
        match res {
            Err(PingFailure::HttpStatus(503, body)) => assert!(body.contains("error")),
            other => panic!("expected HttpStatus(503), got {:?}", other),
        }
    }

    #[test]
    fn split_http_response_returns_none_for_no_separator() {
        assert!(split_http_response("not http at all").is_none());
    }

    #[test]
    fn default_port_is_19120() {
        assert_eq!(DEFAULT_BRIDGE_PORT, 19120);
    }

    #[test]
    fn bridge_ping_result_failure_helper_clears_structured_fields() {
        let res = BridgePingResult::failure(19120, "timeout", "slow", 0);
        assert!(!res.ok);
        assert!(!res.connected);
        assert!(!res.compiling);
        assert!(!res.is_playing);
        assert_eq!(res.error_kind, "timeout");
        assert_eq!(res.error_message, "slow");
    }

    #[test]
    fn launch_for_verify_error_serializes_camel_case() {
        let err = LaunchForVerifyError::ProjectNotFound {
            project_id: "p".to_string(),
        };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"projectNotFound\""));
        let err = LaunchForVerifyError::PortInvalid { port: 0 };
        let json = serde_json::to_string(&err).unwrap();
        assert!(json.contains("\"portInvalid\""));
    }

    #[test]
    fn launch_for_verify_error_displays_friendly_message() {
        let err = LaunchForVerifyError::InstallNotFound {
            project_id: "p".to_string(),
            version: "6000.0.1f1".to_string(),
        };
        let s = err.to_string();
        assert!(s.contains("6000.0.1f1"));
    }

    #[test]
    fn bridge_ping_result_serializes_camel_case() {
        let res = BridgePingResult {
            port: 19120,
            duration_ms: 42,
            ok: true,
            connected: true,
            compiling: false,
            is_playing: false,
            project_path: Some("/g/MyGame".to_string()),
            unity_version: Some("6000.0.1f1".to_string()),
            raw: Some("{}".to_string()),
            error_kind: String::new(),
            error_message: String::new(),
        };
        let json = serde_json::to_string(&res).unwrap();
        assert!(json.contains("\"port\":19120"));
        assert!(json.contains("\"durationMs\""));
        assert!(json.contains("\"projectPath\""));
        assert!(json.contains("\"unityVersion\""));
        assert!(json.contains("\"errorKind\""));
    }
}
