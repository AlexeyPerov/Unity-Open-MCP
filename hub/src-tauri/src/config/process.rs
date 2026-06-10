use std::process::Command;

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum KillUnityStatus {
    /// The PID was running and a terminate signal was delivered.
    Killed,
    /// The PID was not running (no such process / stale entry).
    NotFound,
    /// The PID exists but the current user is not allowed to terminate it.
    AccessDenied,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct KillUnityResult {
    pub pid: u32,
    pub status: KillUnityStatus,
    pub message: String,
}

#[cfg(unix)]
fn check_unix(pid: u32) -> Result<bool, String> {
    // `kill -0 <pid>` exits 0 when the process exists and is signalable, 1
    // otherwise. We deliberately do not use libc::kill directly so the binary
    // stays free of unsafe code in this module.
    let output = Command::new("kill")
        .arg("-0")
        .arg(pid.to_string())
        .output()
        .map_err(|e| format!("failed to probe pid {}: {}", pid, e))?;
    Ok(output.status.success())
}

#[cfg(unix)]
fn kill_unix(pid: u32, signal: &str) -> Result<(), String> {
    let output = Command::new("kill")
        .arg(format!("-{}", signal))
        .arg(pid.to_string())
        .output()
        .map_err(|e| format!("failed to send {} to pid {}: {}", signal, pid, e))?;
    if output.status.success() {
        Ok(())
    } else {
        let stderr = String::from_utf8_lossy(&output.stderr);
        Err(format!(
            "kill -{} {} failed: {}",
            signal,
            pid,
            stderr.trim()
        ))
    }
}

#[cfg(unix)]
pub fn terminate_process(pid: u32) -> KillUnityResult {
    let alive = match check_unix(pid) {
        Ok(true) => true,
        Ok(false) => {
            return KillUnityResult {
                pid,
                status: KillUnityStatus::NotFound,
                message: format!("pid {} is not running (stale launch record)", pid),
            };
        }
        Err(e) => {
            return KillUnityResult {
                pid,
                status: KillUnityStatus::NotFound,
                message: e,
            };
        }
    };

    if !alive {
        return KillUnityResult {
            pid,
            status: KillUnityStatus::NotFound,
            message: format!("pid {} is not running (stale launch record)", pid),
        };
    }

    match kill_unix(pid, "TERM") {
        Ok(()) => KillUnityResult {
            pid,
            status: KillUnityStatus::Killed,
            message: format!("sent SIGTERM to pid {}", pid),
        },
        Err(e) => {
            // EPERM (not our process) surfaces as a non-zero exit from `kill`.
            // Distinguish "not allowed" from "vanished" by re-probing.
            if !check_unix(pid).unwrap_or(false) {
                return KillUnityResult {
                    pid,
                    status: KillUnityStatus::NotFound,
                    message: format!("pid {} exited before terminate completed", pid),
                };
            }
            // Try harder with SIGKILL; if that also fails, treat as denied.
            match kill_unix(pid, "KILL") {
                Ok(()) => KillUnityResult {
                    pid,
                    status: KillUnityStatus::Killed,
                    message: format!("sent SIGKILL to pid {}", pid),
                },
                Err(e2) => KillUnityResult {
                    pid,
                    status: KillUnityStatus::AccessDenied,
                    message: format!("{}; SIGKILL fallback: {}", e, e2),
                },
            }
        }
    }
}

#[cfg(windows)]
fn check_windows(pid: u32) -> Result<bool, String> {
    // `tasklist /FI "PID eq <pid>" /FO CSV /NH` prints a header line followed
    // by a CSV row when the process exists, or "INFO: No tasks are running..."
    // when it does not. We look for the PID as a CSV field to be safe.
    let output = Command::new("tasklist")
        .arg("/FI")
        .arg(format!("PID eq {}", pid))
        .arg("/FO")
        .arg("CSV")
        .arg("/NH")
        .output()
        .map_err(|e| format!("failed to probe pid {}: {}", pid, e))?;
    if !output.status.success() {
        // tasklist exit code 0 even when filter matches nothing; non-zero
        // is a real error.
        let stderr = String::from_utf8_lossy(&output.stderr);
        return Err(format!("tasklist probe failed: {}", stderr.trim()));
    }
    let stdout = String::from_utf8_lossy(&output.stdout);
    let needle = format!(",\"{}\",", pid);
    Ok(stdout.contains(&needle))
}

#[cfg(windows)]
fn kill_windows(pid: u32) -> Result<(), String> {
    let output = Command::new("taskkill")
        .arg("/F")
        .arg("/PID")
        .arg(pid.to_string())
        .output()
        .map_err(|e| format!("failed to invoke taskkill for pid {}: {}", pid, e))?;
    if output.status.success() {
        return Ok(());
    }
    let stderr = String::from_utf8_lossy(&output.stderr);
    let stdout = String::from_utf8_lossy(&output.stdout);
    let combined = format!("{} {}", stderr.trim(), stdout.trim());
    Err(format!("taskkill failed: {}", combined.trim()))
}

#[cfg(windows)]
pub fn terminate_process(pid: u32) -> KillUnityResult {
    let alive = match check_windows(pid) {
        Ok(true) => true,
        Ok(false) => {
            return KillUnityResult {
                pid,
                status: KillUnityStatus::NotFound,
                message: format!("pid {} is not running (stale launch record)", pid),
            };
        }
        Err(e) => {
            return KillUnityResult {
                pid,
                status: KillUnityStatus::NotFound,
                message: e,
            };
        }
    };

    if !alive {
        return KillUnityResult {
            pid,
            status: KillUnityStatus::NotFound,
            message: format!("pid {} is not running (stale launch record)", pid),
        };
    }

    match kill_windows(pid) {
        Ok(()) => KillUnityResult {
            pid,
            status: KillUnityStatus::Killed,
            message: format!("taskkill /F /PID {} succeeded", pid),
        },
        Err(e) => {
            // taskkill exit code 128 on access denied; re-probe to confirm
            // the process is still around (it might have raced out).
            if !check_windows(pid).unwrap_or(false) {
                return KillUnityResult {
                    pid,
                    status: KillUnityStatus::NotFound,
                    message: format!("pid {} exited before terminate completed", pid),
                };
            }
            KillUnityResult {
                pid,
                status: KillUnityStatus::AccessDenied,
                message: e,
            }
        }
    }
}

#[tauri::command]
pub fn kill_unity(pid: u32) -> KillUnityResult {
    if pid == 0 {
        // PID 0 is a special sentinel: on Unix it means "every process in
        // the calling process group", and on Windows it is unused. We must
        // never treat it as a valid launch PID and must refuse to act.
        return KillUnityResult {
            pid,
            status: KillUnityStatus::NotFound,
            message: "pid 0 is not a valid Unity launch record".to_string(),
        };
    }
    terminate_process(pid)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::process::Command;

    fn spawn_dummy_child() -> std::process::Child {
        if cfg!(windows) {
            // ping blocks until told to stop; -n 99999 yields a long sleep
            // without external dependencies.
            Command::new("ping")
                .arg("-n")
                .arg("99999")
                .arg("127.0.0.1")
                .stdout(std::process::Stdio::null())
                .stderr(std::process::Stdio::null())
                .spawn()
                .expect("spawn ping")
        } else {
            // sleep is part of POSIX and present on macOS and Linux.
            Command::new("sleep")
                .arg("99999")
                .stdout(std::process::Stdio::null())
                .stderr(std::process::Stdio::null())
                .spawn()
                .expect("spawn sleep")
        }
    }

    /// Spawn a long-running dummy process, return its PID **and** the live
    /// `Child` handle so the test can `wait()` for the OS to actually reap
    /// it after a kill. Reaping synchronously avoids macOS zombie-PID races
    /// where `kill -0` still succeeds for a few ms after the process exits.
    fn spawn_dummy_child_with_handle() -> (std::process::Child, u32) {
        let child = spawn_dummy_child();
        let pid = child.id();
        (child, pid)
    }

    #[test]
    fn not_found_for_unused_pid() {
        // Pick a high PID unlikely to be in use. u32::MAX is reserved-ish on
        // most systems and `kill -0` will report ESRCH.
        let result = terminate_process(u32::MAX);
        assert_eq!(result.status, KillUnityStatus::NotFound);
    }

    #[test]
    fn killed_for_running_process() {
        let (mut child, pid) = spawn_dummy_child_with_handle();
        let result = terminate_process(pid);
        // wait() will succeed only once the OS has reaped the terminated
        // process; this avoids the test leaving zombies behind.
        let _ = child.wait();
        assert_eq!(result.status, KillUnityStatus::Killed);
    }

    #[test]
    fn second_kill_reports_not_found() {
        let (mut child, pid) = spawn_dummy_child_with_handle();
        let first = terminate_process(pid);
        assert_eq!(first.status, KillUnityStatus::Killed);
        // Drain the killed process so the OS reclaims the PID before we
        // probe it again. wait() blocks until reaping is complete.
        let _ = child.wait();
        let second = terminate_process(pid);
        assert_eq!(second.status, KillUnityStatus::NotFound);
    }

    #[test]
    fn result_serializes_camel_case() {
        let result = KillUnityResult {
            pid: 12345,
            status: KillUnityStatus::Killed,
            message: "ok".to_string(),
        };
        let json = serde_json::to_string(&result).unwrap();
        assert!(json.contains("\"pid\":12345"));
        assert!(json.contains("\"status\":\"killed\""));
        assert!(json.contains("\"message\":\"ok\""));
    }

    #[test]
    fn status_serializes_camel_case() {
        assert_eq!(
            serde_json::to_string(&KillUnityStatus::Killed).unwrap(),
            "\"killed\""
        );
        assert_eq!(
            serde_json::to_string(&KillUnityStatus::NotFound).unwrap(),
            "\"notFound\""
        );
        assert_eq!(
            serde_json::to_string(&KillUnityStatus::AccessDenied).unwrap(),
            "\"accessDenied\""
        );
    }

    #[test]
    fn zero_pid_reports_not_found_safely() {
        // PID 0 is special: on Unix it means "process group", and on
        // Windows it is unused. The wrapper must refuse to act and never
        // touch a real process.
        let result = kill_unity(0);
        assert_eq!(result.status, KillUnityStatus::NotFound);
    }
}
