//! Long-running command runner for Open-MCP repositories.
//!
//! Ports the core of vibe-launcher's `lib.rs` process pattern: spawn a
//! child process with piped stdout/stderr, stream each line to the
//! frontend via a Tauri event, and track the PID so it can be killed
//! (process-group kill on Unix, taskkill on Windows). Used by the
//! Open-MCP settings popup for `npm run build`, `npm test`, and a
//! free-form custom command.
//!
//! State is keyed by `(project_id, panel)` so multiple projects can
//! each run independent commands without colliding; one panel per
//! project at a time (build / test / custom).

use std::collections::HashMap;
use std::io::{BufRead, BufReader};
use std::path::PathBuf;
use std::process::{Command, Stdio};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::Duration;

use serde::{Deserialize, Serialize};
use tauri::{AppHandle, Emitter, State};

use crate::config::schemas::ProjectKind;

/// Per-(project, panel) tracked process. Holds the child PID (when
/// running) so the stop command can kill the whole process tree.
#[derive(Default, Clone)]
pub struct TrackedProc {
    pub pid: Option<u32>,
    pub running: bool,
}

/// All tracked processes for the command runner. Managed as a single
/// Tauri state value; keyed by `"{project_id}|{panel}"`. The procs
/// map is wrapped in an `Arc<Mutex>` so the exit-wait thread can hold
/// a clone and flip the running flag without going through the Tauri
/// state handle (which is not `Send`-able into a thread).
#[derive(Default)]
pub struct CommandRunnerState {
    pub procs: Arc<Mutex<HashMap<String, TrackedProc>>>,
}

fn key(project_id: &str, panel: &str) -> String {
    format!("{}|{}", project_id, panel)
}

/// Cross-platform npm wrapper: `npm` on Unix, `cmd /C npm` on Windows
/// (Windows needs the shell to resolve `npm.cmd`).
fn npm_command() -> Command {
    if cfg!(target_os = "windows") {
        let mut cmd = Command::new("cmd");
        cmd.arg("/C").arg("npm");
        cmd
    } else {
        Command::new("npm")
    }
}

/// Resolve the npm working directory for a tracked project.
///
/// For Open-MCP repositories the publishable package lives in
/// `mcp-server/` — the npm scripts (`build`, `test`, `publish`, `version`)
/// are defined there, not at the repo root. Package projects keep using
/// the project root. This centralizes the rule so every maintainer-panel
/// command shares one resolution path (the frontend passes the repo root
/// and the kind; Rust derives the right cwd).
pub fn resolve_npm_cwd(project_path: &str, kind: ProjectKind) -> PathBuf {
    let base = PathBuf::from(project_path);
    match kind {
        ProjectKind::OpenMcp => base.join("mcp-server"),
        _ => base,
    }
}

/// Read `name` + `version` from the `package.json` at the resolved npm
/// cwd. Used by the maintainer panel's read-only info header so the user
/// sees the package identity without a separate `npm` invocation.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct McpPackageInfo {
    pub name: String,
    pub version: String,
    /// Absolute path of the `package.json` the values came from.
    pub manifest_path: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum McpPackageInfoError {
    #[serde(rename_all = "camelCase")]
    NotFound { path: String },
    #[serde(rename_all = "camelCase")]
    ParseFailed { path: String, message: String },
}

fn read_package_json_at(cwd: &PathBuf) -> Result<McpPackageInfo, McpPackageInfoError> {
    let manifest_path = cwd.join("package.json");
    let body = std::fs::read_to_string(&manifest_path).map_err(|_| McpPackageInfoError::NotFound {
        path: manifest_path.to_string_lossy().into_owned(),
    })?;
    let parsed: serde_json::Value =
        serde_json::from_str(&body).map_err(|e| McpPackageInfoError::ParseFailed {
            path: manifest_path.to_string_lossy().into_owned(),
            message: e.to_string(),
        })?;
    let name = parsed
        .get("name")
        .and_then(|v| v.as_str())
        .unwrap_or("")
        .to_string();
    let version = parsed
        .get("version")
        .and_then(|v| v.as_str())
        .unwrap_or("")
        .to_string();
    Ok(McpPackageInfo {
        name,
        version,
        manifest_path: manifest_path.to_string_lossy().into_owned(),
    })
}

/// Tauri command: read `name` + `version` from the package.json the
/// maintainer panel would publish. For Open-MCP projects this resolves
/// to `{project.path}/mcp-server/package.json`; for Package projects it
/// is the root `package.json`.
#[tauri::command]
pub fn read_mcp_package_info(
    project_path: String,
    kind: ProjectKind,
) -> Result<McpPackageInfo, McpPackageInfoError> {
    let cwd = resolve_npm_cwd(&project_path, kind);
    read_package_json_at(&cwd)
}

/// Strips ANSI CSI color sequences so the log pane renders cleanly.
/// Ports vibe-launcher's `strip_ansi_escapes`. Only handles the `m`
/// (SGR) final byte; cursor/erase sequences are rare in npm output.
fn strip_ansi(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    let bytes = s.as_bytes();
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == 0x1b && i + 1 < bytes.len() && bytes[i + 1] == b'[' {
            // Skip until the final byte (0x40..=0x7e).
            i += 2;
            while i < bytes.len() && !(0x40..=0x7e).contains(&bytes[i]) {
                i += 1;
            }
            i += 1; // skip the final byte itself
        } else {
            out.push(bytes[i] as char);
            i += 1;
        }
    }
    out
}

/// Emits a single log line to the frontend. The payload matches the
/// shape the Open-MCP settings popup listens for.
fn emit_log(app: &AppHandle, project_id: &str, panel: &str, line: &str) {
    let _ = app.emit(
        "cmd-log",
        serde_json::json!({
            "projectId": project_id,
            "panel": panel,
            "line": line,
        }),
    );
}

/// Emits a process-exit event so the frontend can flip its running
/// badge and refresh any derived status.
fn emit_exit(app: &AppHandle, project_id: &str, panel: &str, code: Option<i32>) {
    let _ = app.emit(
        "cmd-exit",
        serde_json::json!({
            "projectId": project_id,
            "panel": panel,
            "code": code,
        }),
    );
}

/// Reads `stream` line-by-line and emits each line. Returns when the
/// stream closes (child exited). Strips ANSI before emitting.
fn pipe_lines<R: std::io::Read + Send + 'static>(
    stream: R,
    app: AppHandle,
    project_id: String,
    panel: String,
) {
    let reader = BufReader::new(stream);
    for line in reader.lines().flatten() {
        let cleaned = strip_ansi(&line);
        emit_log(&app, &project_id, &panel, &cleaned);
    }
}

/// Kills a process tree. On Unix, kills the whole process group
/// (`kill -SIGTERM -pgid`) then SIGKILL after a grace period — this
/// is how vibe-launcher reliably stops an `npm` process and all its
/// children. On Windows, `taskkill /T /F`.
fn kill_process_tree(pid: u32) {
    #[cfg(unix)]
    {
        unsafe {
            libc::kill(-(pid as i32), libc::SIGTERM);
            thread::sleep(Duration::from_millis(900));
            libc::kill(-(pid as i32), libc::SIGKILL);
        }
    }
    #[cfg(windows)]
    {
        let _ = Command::new("taskkill")
            .args(["/PID", &pid.to_string(), "/T", "/F"])
            .status();
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum CommandRunnerError {
    #[serde(rename_all = "camelCase")]
    SpawnFailed { message: String },
    #[serde(rename_all = "camelCase")]
    AlreadyRunning { project_id: String, panel: String },
}

/// Spawns a tracked command for `(project_id, panel)` in `cwd`. The
/// command runs detached; stdout/stderr are streamed line-by-line via
/// `cmd-log` events. Refuses to spawn when a process is already
/// running for that key (frontend should call `stop_project_command`
/// first).
fn spawn_tracked(
    app: &AppHandle,
    state: &State<CommandRunnerState>,
    project_id: &str,
    panel: &str,
    cwd: &str,
    mut cmd: Command,
) -> Result<(), CommandRunnerError> {
    let k = key(project_id, panel);
    {
        let procs = state.procs.lock().unwrap();
        if let Some(p) = procs.get(&k) {
            if p.running {
                return Err(CommandRunnerError::AlreadyRunning {
                    project_id: project_id.into(),
                    panel: panel.into(),
                });
            }
        }
    }

    cmd.current_dir(cwd);
    cmd.stdin(Stdio::null());
    cmd.stdout(Stdio::piped());
    cmd.stderr(Stdio::piped());
    // Put the child in its own process group so we can kill the whole
    // tree (npm spawns node, which may spawn more) with one signal.
    #[cfg(unix)]
    {
        use std::os::unix::process::CommandExt;
        cmd.process_group(0);
    }

    // Force stable, color-free output so the ANSI stripper is a safety
    // net rather than the only defense.
    cmd.env("NO_COLOR", "1");
    cmd.env("FORCE_COLOR", "0");

    let mut child = cmd.spawn().map_err(|e| CommandRunnerError::SpawnFailed {
        message: e.to_string(),
    })?;
    let pid = child.id();
    let stdout = child.stdout.take();
    let stderr = child.stderr.take();

    {
        let mut procs = state.procs.lock().unwrap();
        procs.insert(
            k.clone(),
            TrackedProc {
                pid: Some(pid),
                running: true,
            },
        );
    }

    let app_clone = app.clone();
    let project_id_owned = project_id.to_string();
    let panel_owned = panel.to_string();
    // Clone the Arc<Mutex> so the exit-wait thread can flip the
    // running flag without touching the Tauri state handle (which is
    // not safe to move across threads).
    let procs_arc = state.procs.clone();

    // Spawn reader threads for each stream.
    if let Some(out) = stdout {
        let app2 = app_clone.clone();
        let panel2 = panel_owned.clone();
        let project_id2 = project_id_owned.clone();
        thread::spawn(move || {
            pipe_lines(out, app2, project_id2, panel2);
        });
    }
    if let Some(err) = stderr {
        let app3 = app_clone.clone();
        let panel3 = panel_owned.clone();
        let project_id3 = project_id_owned.clone();
        thread::spawn(move || {
            pipe_lines(err, app3, project_id3, panel3);
        });
    }

    // Wait thread: blocks until the child exits, then emits cmd-exit
    // and flips the running flag.
    let app4 = app_clone.clone();
    let panel4 = panel_owned.clone();
    let project_id4 = project_id_owned.clone();
    thread::spawn(move || {
        let status = child.wait();
        let code = status.ok().and_then(|s| s.code());
        emit_exit(&app4, &project_id4, &panel4, code);
        let k = key(&project_id4, &panel4);
        let mut procs = procs_arc.lock().unwrap();
        if let Some(p) = procs.get_mut(&k) {
            p.running = false;
            p.pid = None;
        }
    });

    Ok(())
}

/// Runs `npm run build` in the npm-resolved cwd (`mcp-server/` for
/// Open-MCP projects, the project root otherwise). The frontend listens
/// for `cmd-log` lines tagged with `panel: "build"`.
#[tauri::command]
pub fn run_project_build(
    app: AppHandle,
    state: State<'_, CommandRunnerState>,
    project_id: String,
    project_path: String,
    kind: ProjectKind,
) -> Result<(), CommandRunnerError> {
    let cwd = resolve_npm_cwd(&project_path, kind);
    let cwd_str = cwd.to_string_lossy().into_owned();
    let mut cmd = npm_command();
    cmd.args(["run", "build"]);
    spawn_tracked(&app, &state, &project_id, "build", &cwd_str, cmd)
}

/// Runs `npm test` in the npm-resolved cwd.
#[tauri::command]
pub fn run_project_test(
    app: AppHandle,
    state: State<'_, CommandRunnerState>,
    project_id: String,
    project_path: String,
    kind: ProjectKind,
) -> Result<(), CommandRunnerError> {
    let cwd = resolve_npm_cwd(&project_path, kind);
    let cwd_str = cwd.to_string_lossy().into_owned();
    let mut cmd = npm_command();
    cmd.args(["run", "test"]);
    spawn_tracked(&app, &state, &project_id, "test", &cwd_str, cmd)
}

/// Runs a custom npm script (e.g. `lint`) or, when `args` is empty, a
/// bare `npm install`. Runs in the npm-resolved cwd.
#[tauri::command]
pub fn run_project_custom(
    app: AppHandle,
    state: State<'_, CommandRunnerState>,
    project_id: String,
    project_path: String,
    kind: ProjectKind,
    args: Vec<String>,
) -> Result<(), CommandRunnerError> {
    let cwd = resolve_npm_cwd(&project_path, kind);
    let cwd_str = cwd.to_string_lossy().into_owned();
    let mut cmd = npm_command();
    if args.is_empty() {
        cmd.arg("install");
    } else {
        cmd.args(&args);
    }
    spawn_tracked(&app, &state, &project_id, "custom", &cwd_str, cmd)
}

/// `npm version {level}` bump in the npm-resolved cwd.
/// `--no-git-tag-version` keeps the bump local to `package.json` —
/// the Hub never creates git tags; tagging stays in the release runbook
/// (CI publishes on a manually-pushed `v*` tag).
#[tauri::command]
pub fn run_project_npm_version(
    app: AppHandle,
    state: State<'_, CommandRunnerState>,
    project_id: String,
    project_path: String,
    kind: ProjectKind,
    level: String,
) -> Result<(), CommandRunnerError> {
    let cwd = resolve_npm_cwd(&project_path, kind);
    let cwd_str = cwd.to_string_lossy().into_owned();
    let level_trim = level.trim();
    let valid = matches!(level_trim, "patch" | "minor" | "major");
    if !valid {
        return Err(CommandRunnerError::SpawnFailed {
            message: format!(
                "Invalid version bump level '{}': expected patch, minor, or major.",
                level
            ),
        });
    }
    let mut cmd = npm_command();
    cmd.args(["version", level_trim, "--no-git-tag-version"]);
    spawn_tracked(&app, &state, &project_id, "version", &cwd_str, cmd)
}

/// `npm publish --dry-run --access public` in the npm-resolved cwd.
/// Preflight only — lists the files that would ship without touching the
/// registry. Safe to run without confirmation.
#[tauri::command]
pub fn run_project_npm_publish_dry_run(
    app: AppHandle,
    state: State<'_, CommandRunnerState>,
    project_id: String,
    project_path: String,
    kind: ProjectKind,
) -> Result<(), CommandRunnerError> {
    let cwd = resolve_npm_cwd(&project_path, kind);
    let cwd_str = cwd.to_string_lossy().into_owned();
    let mut cmd = npm_command();
    cmd.args(["publish", "--dry-run", "--access", "public"]);
    spawn_tracked(&app, &state, &project_id, "publishDryRun", &cwd_str, cmd)
}

/// `npm publish --access public` in the npm-resolved cwd. Real publish —
/// mutating. The frontend is expected to show a confirmation dialog
/// before invoking this command; the Hub never persists npm credentials
/// (the maintainer authenticates via `npm login` on the host machine).
#[tauri::command]
pub fn run_project_npm_publish(
    app: AppHandle,
    state: State<'_, CommandRunnerState>,
    project_id: String,
    project_path: String,
    kind: ProjectKind,
) -> Result<(), CommandRunnerError> {
    let cwd = resolve_npm_cwd(&project_path, kind);
    let cwd_str = cwd.to_string_lossy().into_owned();
    let mut cmd = npm_command();
    cmd.args(["publish", "--access", "public"]);
    spawn_tracked(&app, &state, &project_id, "publish", &cwd_str, cmd)
}

/// Structured result of a best-effort registry query. Both fields are
/// best-effort: a missing/empty value with an `error` message is a
/// normal outcome when the host is offline, the package is unpublished,
/// or the maintainer is not logged in. The panel surfaces the errors
/// inline rather than blocking the publish flow.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NpmRegistryInfo {
    /// Package version advertised on the public registry, parsed from
    /// `npm view <name> version` stdout. `null` when the query failed
    /// or returned nothing (unpublished name).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub published_version: Option<String>,
    /// `npm whoami` result — the logged-in account name. `null` when
    /// the maintainer is not authenticated.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub whoami: Option<String>,
    /// Error text for the `npm view` call when it failed. Empty on
    /// success.
    #[serde(default)]
    pub view_error: String,
    /// Error text for the `npm whoami` call when it failed.
    #[serde(default)]
    pub whoami_error: String,
}

/// Tauri command: query the public npm registry for the package's
/// published version and the current `npm whoami`. Both calls are
/// best-effort and run synchronously (each shells out once); the panel
/// treats any failure as a soft warning rather than a hard block.
#[tauri::command]
pub fn query_npm_registry(
    project_path: String,
    kind: ProjectKind,
) -> Result<NpmRegistryInfo, CommandRunnerError> {
    let cwd = resolve_npm_cwd(&project_path, kind);
    let cwd_str = cwd.to_string_lossy().into_owned();
    // Read the package name from the local manifest so the registry
    // query targets whatever this checkout actually publishes (and so a
    // fork with a different name still resolves correctly).
    let info = match read_package_json_at(&cwd) {
        Ok(i) => i,
        Err(e) => {
            return Err(CommandRunnerError::SpawnFailed {
                message: format!(
                    "cannot read package.json for registry query: {:?}",
                    e
                ),
            });
        }
    };
    if info.name.is_empty() {
        return Err(CommandRunnerError::SpawnFailed {
            message: "package.json has no `name` field; cannot query the registry.".to_string(),
        });
    }

    let mut view_cmd = npm_command();
    view_cmd.args(["view", &info.name, "version"]);
    view_cmd.current_dir(&cwd_str);
    view_cmd.stdin(Stdio::null());
    let (published_version, view_error) = match capture_npm(&mut view_cmd) {
        Ok(stdout) => {
            let trimmed = stdout.trim();
            if trimmed.is_empty() {
                (None, String::new())
            } else {
                (Some(trimmed.to_string()), String::new())
            }
        }
        Err(msg) => (None, msg),
    };

    let mut whoami_cmd = npm_command();
    whoami_cmd.arg("whoami");
    whoami_cmd.current_dir(&cwd_str);
    whoami_cmd.stdin(Stdio::null());
    let (whoami, whoami_error) = match capture_npm(&mut whoami_cmd) {
        Ok(stdout) => {
            let trimmed = stdout.trim();
            if trimmed.is_empty() {
                (None, String::new())
            } else {
                (Some(trimmed.to_string()), String::new())
            }
        }
        Err(msg) => (None, msg),
    };

    Ok(NpmRegistryInfo {
        published_version,
        whoami,
        view_error,
        whoami_error,
    })
}

/// Run an npm command to completion, capturing combined stdout/stderr.
/// Returns the stdout text on exit 0, or an error string on non-zero /
/// spawn failure. Used by the registry query (which is synchronous and
/// short-lived — the streaming `spawn_tracked` path is for the
/// long-running maintainer-panel commands).
fn capture_npm(cmd: &mut Command) -> Result<String, String> {
    let output = cmd
        .output()
        .map_err(|e| format!("failed to spawn npm: {}", e))?;
    if output.status.success() {
        Ok(String::from_utf8_lossy(&output.stdout).into_owned())
    } else {
        let stderr = String::from_utf8_lossy(&output.stderr);
        let stdout = String::from_utf8_lossy(&output.stdout);
        let mut msg = String::new();
        if !stderr.trim().is_empty() {
            msg.push_str(stderr.trim());
        }
        if !stdout.trim().is_empty() {
            if !msg.is_empty() {
                msg.push(' ');
            }
            msg.push_str(stdout.trim());
        }
        if msg.is_empty() {
            msg = format!("npm exited with status {}", output.status);
        }
        Err(msg)
    }
}

/// Stops the tracked process for `(project_id, panel)`, killing its
/// whole tree. No-op (returns Ok) when nothing is running.
#[tauri::command]
pub fn stop_project_command(
    state: State<'_, CommandRunnerState>,
    project_id: String,
    panel: String,
) -> Result<(), CommandRunnerError> {
    let k = key(&project_id, &panel);
    let pid = {
        let procs = state.procs.lock().unwrap();
        procs.get(&k).and_then(|p| p.pid)
    };
    if let Some(pid) = pid {
        kill_process_tree(pid);
    }
    let mut procs = state.procs.lock().unwrap();
    if let Some(p) = procs.get_mut(&k) {
        p.running = false;
        p.pid = None;
    }
    Ok(())
}

/// Returns true when a tracked process is running for the given key.
#[tauri::command]
pub fn project_command_running(
    state: State<'_, CommandRunnerState>,
    project_id: String,
    panel: String,
) -> bool {
    let k = key(&project_id, &panel);
    state
        .procs
        .lock()
        .unwrap()
        .get(&k)
        .map(|p| p.running)
        .unwrap_or(false)
}

// ---- Repo-wide version sync (scripts/sync-version.mjs) --------------------
//
// The maintainer panel above runs *npm* commands against the publishable
// `package.json`. This is a different concern: the repo-wide version sync
// script that rewrites every generated version target (trio: 5 files from
// `version.json`; hub: 3 files from `hub/version.json`) and powers the CI
// drift gate. It is the release/drift tool, not a pre-publish package bump.
//
// Unlike the npm commands, this runner must execute at the **repo root**
// (`scripts/` lives there, not under `mcp-server/`), so it deliberately
// bypasses `resolve_npm_cwd`.

/// Which version line to target. Mirrors the script's `--hub` flag.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum SyncVersionLine {
    /// Shared trio (npm server + bridge + verify). Default; no flag.
    Trio,
    /// Unity Hub Pro desktop app. Maps to `--hub`.
    Hub,
}

/// Which sync-version action to run. Mirrors the script's subcommands.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum SyncVersionAction {
    /// Bare run: rewrite all generated targets from the source file.
    Sync,
    /// `--check`: read-only drift gate. Exits 1 when any target drifted.
    Check,
    /// `bump <level>`: increment the source, then sync.
    Bump,
    /// `set <X.Y.Z>`: jump to an exact version, then sync.
    Set,
}

/// Cross-platform `node` wrapper: `node` on Unix, `cmd /C node` on Windows
/// (mirrors `npm_command` — Windows needs the shell to resolve `node.exe`
/// off PATH the same way it resolves `npm.cmd`).
fn node_command() -> Command {
    if cfg!(target_os = "windows") {
        let mut cmd = Command::new("cmd");
        cmd.arg("/C").arg("node");
        cmd
    } else {
        Command::new("node")
    }
}

/// Validates the bump level / set-version operand using the same rules as
/// `scripts/sync-version.mjs` (the script is the source of truth; this
/// mirrors its regex so a bad value fails fast with a clear message
/// instead of letting the script exit 2 after spawning).
fn validate_sync_version_args(
    action: SyncVersionAction,
    bump_level: &Option<String>,
    set_version: &Option<String>,
) -> Result<(), CommandRunnerError> {
    match action {
        SyncVersionAction::Bump => {
            let level = bump_level
               .as_deref()
                .map(|s| s.trim())
                .unwrap_or("");
            if !matches!(level, "patch" | "minor" | "major") {
                return Err(CommandRunnerError::SpawnFailed {
                    message: format!(
                        "Invalid version bump level '{}': expected patch, minor, or major.",
                        level
                    ),
                });
            }
        }
        SyncVersionAction::Set => {
            let raw = set_version
                .as_deref()
                .map(|s| s.trim())
                .unwrap_or("");
            // Same tolerance as the script: optional leading `v`, plain
            // major.minor.patch, no pre-release/build metadata.
            if !is_loose_semver(raw) {
                return Err(CommandRunnerError::SpawnFailed {
                    message: format!(
                        "Invalid set version '{}': expected X.Y.Z (a leading 'v' is tolerated).",
                        raw
                    ),
                });
            }
        }
        SyncVersionAction::Sync | SyncVersionAction::Check => {}
    }
    Ok(())
}

/// Hand-rolled equivalent of the script's `/^v?\d+\.\d+\.\d+$/` — kept
/// dependency-free (the rest of the runner avoids pulling in `regex`).
/// Accepts an optional leading `v`, three digit groups, no
/// pre-release/build metadata.
fn is_loose_semver(s: &str) -> bool {
    let s = s.strip_prefix('v').unwrap_or(s);
    let mut parts = s.split('.');
    match (parts.next(), parts.next(), parts.next(), parts.next()) {
        (Some(a), Some(b), Some(c), None) => {
            !a.is_empty() && a.bytes().all(|b| b.is_ascii_digit())
                && !b.is_empty() && b.bytes().all(|b| b.is_ascii_digit())
                && !c.is_empty() && c.bytes().all(|b| b.is_ascii_digit())
        }
        _ => false,
    }
}

/// Tauri command: run `node scripts/sync-version.mjs …` at the repo root
/// for the chosen version line and action, streaming output to the
/// `sync` panel exactly like the npm maintainer commands. The script's
/// own exit code is forwarded (0 = ok / in-sync; 1 = drift detected by
/// `--check`; 2 = usage error — though we pre-validate to avoid the
/// latter). The Hub never creates git tags; tagging stays in the release
/// runbook (CI publishes on a manually-pushed `v*` / `hub-v*` tag).
#[tauri::command]
pub fn run_project_sync_version(
    app: AppHandle,
    state: State<'_, CommandRunnerState>,
    project_id: String,
    project_path: String,
    kind: ProjectKind,
    line: SyncVersionLine,
    action: SyncVersionAction,
    bump_level: Option<String>,
    set_version: Option<String>,
) -> Result<(), CommandRunnerError> {
    // Only Open-MCP repos carry `scripts/sync-version.mjs`; for every
    // other kind the panel doesn't render, but defend in depth.
    if kind != ProjectKind::OpenMcp {
        return Err(CommandRunnerError::SpawnFailed {
            message: format!(
                "Version sync is only available for Open-MCP repositories (got kind = {:?}).",
                kind
            ),
        });
    }
    validate_sync_version_args(action, &bump_level, &set_version)?;

    // The script lives at the repo root, not under mcp-server/.
    let cwd_str = project_path.clone();
    let mut cmd = node_command();
    cmd.arg("scripts/sync-version.mjs");

    match action {
        SyncVersionAction::Check => {
            cmd.arg("--check");
        }
        SyncVersionAction::Sync => {
            // Bare sync: no subcommand, just optional --hub below.
        }
        SyncVersionAction::Bump => {
            cmd.arg("bump").arg(bump_level.as_deref().unwrap_or("").trim());
        }
        SyncVersionAction::Set => {
            // Strip the optional leading `v` the script also tolerates,
            // so the emitted log line is canonical either way.
            let raw = set_version.as_deref().unwrap_or("").trim();
            let clean = raw.strip_prefix('v').unwrap_or(raw);
            cmd.arg("set").arg(clean);
        }
    }
    if line == SyncVersionLine::Hub {
        cmd.arg("--hub");
    }

    spawn_tracked(&app, &state, &project_id, "sync", &cwd_str, cmd)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn strip_ansi_removes_color_codes() {
        let input = "\x1b[31mred text\x1b[0m";
        assert_eq!(strip_ansi(input), "red text");
    }

    #[test]
    fn strip_ansi_leaves_plain_text_untouched() {
        assert_eq!(strip_ansi("hello world"), "hello world");
    }

    #[test]
    fn key_format_is_project_pipe_panel() {
        assert_eq!(key("abc", "build"), "abc|build");
    }

    #[test]
    fn tracked_proc_default_is_not_running() {
        let p = TrackedProc::default();
        assert!(!p.running);
        assert!(p.pid.is_none());
    }

    #[test]
    fn resolve_npm_cwd_appends_mcp_server_for_open_mcp() {
        let cwd = resolve_npm_cwd("/repos/uai", ProjectKind::OpenMcp);
        assert!(cwd.ends_with("mcp-server"));
        assert!(cwd.starts_with("/repos/uai"));
    }

    #[test]
    fn resolve_npm_cwd_keeps_project_root_for_package() {
        // Package + Unity + Custom all stay at the project root — only
        // Open-MCP reroutes into mcp-server/.
        assert_eq!(
            resolve_npm_cwd("/repos/pkg", ProjectKind::Package),
            PathBuf::from("/repos/pkg")
        );
        assert_eq!(
            resolve_npm_cwd("/game", ProjectKind::Unity),
            PathBuf::from("/game")
        );
        assert_eq!(
            resolve_npm_cwd("/any", ProjectKind::Custom),
            PathBuf::from("/any")
        );
    }

    #[test]
    fn read_package_json_at_returns_name_and_version() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(
            dir.path().join("package.json"),
            r#"{"name": "unity-open-mcp", "version": "0.2.0"}"#,
        )
        .unwrap();
        let info = read_package_json_at(&dir.path().to_path_buf()).unwrap();
        assert_eq!(info.name, "unity-open-mcp");
        assert_eq!(info.version, "0.2.0");
        assert!(info.manifest_path.ends_with("package.json"));
    }

    #[test]
    fn read_package_json_at_errors_when_missing() {
        let dir = tempfile::tempdir().unwrap();
        let err = read_package_json_at(&dir.path().to_path_buf()).unwrap_err();
        assert!(matches!(err, McpPackageInfoError::NotFound { .. }));
    }

    #[test]
    fn read_package_json_at_errors_when_malformed() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(dir.path().join("package.json"), "{ not json").unwrap();
        let err = read_package_json_at(&dir.path().to_path_buf()).unwrap_err();
        assert!(matches!(err, McpPackageInfoError::ParseFailed { .. }));
    }

    // ---- sync-version validation ---------------------------------------

    #[test]
    fn is_loose_semver_accepts_plain_and_leading_v() {
        assert!(is_loose_semver("0.1.2"));
        assert!(is_loose_semver("v0.1.2"));
        assert!(is_loose_semver("1.23.456"));
    }

    #[test]
    fn is_loose_semver_rejects_prerelease_and_garbage() {
        assert!(!is_loose_semver("0.1.2-alpha"));
        assert!(!is_loose_semver("0.1")); // too few groups
        assert!(!is_loose_semver("0.1.2.3")); // too many groups
        assert!(!is_loose_semver(""));
        assert!(!is_loose_semver("x.y.z"));
        // The script's `\d+` is permissive about leading zeros; mirror it.
        assert!(is_loose_semver("01.2.3"));
    }

    #[test]
    fn validate_sync_version_accepts_valid_bump_and_set() {
        assert!(validate_sync_version_args(
            SyncVersionAction::Bump,
            &Some("patch".into()),
            &None
        )
        .is_ok());
        assert!(validate_sync_version_args(
            SyncVersionAction::Set,
            &None,
            &Some("0.2.0".into())
        )
        .is_ok());
        assert!(validate_sync_version_args(
            SyncVersionAction::Set,
            &None,
            &Some("v0.2.0".into())
        )
        .is_ok());
        // Sync/Check ignore their operands.
        assert!(validate_sync_version_args(
            SyncVersionAction::Sync,
            &None,
            &None
        )
        .is_ok());
        assert!(validate_sync_version_args(
            SyncVersionAction::Check,
            &None,
            &None
        )
        .is_ok());
    }

    #[test]
    fn validate_sync_version_rejects_bad_bump_level() {
        let err = validate_sync_version_args(
            SyncVersionAction::Bump,
            &Some("mega".into()),
            &None,
        )
        .unwrap_err();
        assert!(matches!(err, CommandRunnerError::SpawnFailed { .. }));
    }

    #[test]
    fn validate_sync_version_rejects_bad_set_version() {
        let err = validate_sync_version_args(
            SyncVersionAction::Set,
            &None,
            &Some("0.1".into()),
        )
        .unwrap_err();
        assert!(matches!(err, CommandRunnerError::SpawnFailed { .. }));
    }
}
