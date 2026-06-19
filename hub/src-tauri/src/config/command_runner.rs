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
use std::process::{Command, Stdio};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::Duration;

use serde::{Deserialize, Serialize};
use tauri::{AppHandle, Emitter, State};

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

/// Runs `npm run build` in the project root. The frontend listens for
/// `cmd-log` lines tagged with `panel: "build"`.
#[tauri::command]
pub fn run_project_build(
    app: AppHandle,
    state: State<'_, CommandRunnerState>,
    project_id: String,
    cwd: String,
) -> Result<(), CommandRunnerError> {
    let mut cmd = npm_command();
    cmd.args(["run", "build"]);
    spawn_tracked(&app, &state, &project_id, "build", &cwd, cmd)
}

/// Runs `npm test` in the project root.
#[tauri::command]
pub fn run_project_test(
    app: AppHandle,
    state: State<'_, CommandRunnerState>,
    project_id: String,
    cwd: String,
) -> Result<(), CommandRunnerError> {
    let mut cmd = npm_command();
    cmd.args(["run", "test"]);
    spawn_tracked(&app, &state, &project_id, "test", &cwd, cmd)
}

/// Runs a custom npm script (e.g. `lint`) or, when `args` is empty, a
/// bare `npm install`.
#[tauri::command]
pub fn run_project_custom(
    app: AppHandle,
    state: State<'_, CommandRunnerState>,
    project_id: String,
    cwd: String,
    args: Vec<String>,
) -> Result<(), CommandRunnerError> {
    let mut cmd = npm_command();
    if args.is_empty() {
        cmd.arg("install");
    } else {
        cmd.args(&args);
    }
    spawn_tracked(&app, &state, &project_id, "custom", &cwd, cmd)
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
}
