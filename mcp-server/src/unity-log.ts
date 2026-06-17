// Unity Editor.log path resolution + tail, for the read_compile_errors tool.
//
// When the bridge assembly itself fails to compile, every in-bridge channel
// (read_console, editor_status, an in-bridge CompilationPipeline listener) is
// dead with it, and batch compile_check can't run either (the batch entry
// point lives in the same broken assembly, and Unity's per-project lock blocks
// a second instance). The ONE channel that survives is the live Editor's
// platform Editor.log — Unity writes CSxxxx diagnostics there regardless of
// bridge health. This module resolves that path per-OS (porting the logic from
// hub/src-tauri/src/config/logs.rs) and reads a bounded tail.
//
// No runtime deps beyond node built-ins (mcp-server/AGENTS.md).

import { existsSync, openSync, readSync, fstatSync, closeSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

export type UnityLogPlatform = "win32" | "darwin" | "linux";

/** Resolve the live Editor.log directory for the current platform. Mirrors
 *  hub/src-tauri/src/config/logs.rs (editor_logs_dir_*).
 *  - macOS: ~/Library/Logs/Unity
 *  - Windows: %LOCALAPPDATA%\Unity\Editor
 *  - Linux: $XDG_CONFIG_HOME/unity3d or ~/.config/unity3d
 */
export function editorLogsDir(
  platform: UnityLogPlatform = process.platform as UnityLogPlatform,
): string {
  switch (platform) {
    case "darwin":
      return join(homedir(), "Library", "Logs", "Unity");
    case "win32": {
      const local = process.env.LOCALAPPDATA;
      if (local) return join(local, "Unity", "Editor");
      // LOCALAPPDATA is effectively always set on Windows; fall back to the
      // Public user profile if it is somehow missing.
      return join(
        "C:\\Users\\Public\\AppData\\Local",
        "Unity",
        "Editor",
      );
    }
    case "linux": {
      const xdg = process.env.XDG_CONFIG_HOME;
      if (xdg) return join(xdg, "unity3d");
      return join(homedir(), ".config", "unity3d");
    }
    default:
      return join(homedir(), "Library", "Logs", "Unity");
  }
}

/** Resolve the live Editor.log file path for the current platform. */
export function editorLogPath(
  platform: UnityLogPlatform = process.platform as UnityLogPlatform,
): string {
  return join(editorLogsDir(platform), "Editor.log");
}

/** Default tail size. Bounded so a multi-MB log can't blow up the tool
 *  response; 256KB is ample for a compile-error burst (Unity writes the
 *  diagnostics in a contiguous block near the end of the log). */
export const DEFAULT_LOG_TAIL_BYTES = 256 * 1024;

export interface ReadLogTailResult {
  /** Absolute path that was read. */
  path: string;
  /** Whether the file existed and was read. */
  exists: boolean;
  /** The tail content. Empty when the file is missing or unreadable. */
  content: string;
  /** Bytes read (content.length in UTF-8 bytes). 0 when missing. */
  bytes: number;
  /** Error message when the file existed but could not be read. */
  error?: string;
}

/**
 * Read up to `maxBytes` from the END of a file, as a UTF-8 string. Returns
 * { exists: false } when the file is absent. Never throws — read failures
 * (permissions, vanished mid-read) surface as { exists, error }.
 *
 * The tail is read by seeking to (size - maxBytes) and reading forward, so a
 * multi-MB log is not loaded in full.
 */
export function readLogTail(
  path: string,
  maxBytes: number = DEFAULT_LOG_TAIL_BYTES,
): ReadLogTailResult {
  if (!existsSync(path)) {
    return { path, exists: false, content: "", bytes: 0 };
  }
  let fd;
  try {
    fd = openSync(path, "r");
    const stat = fstatSync(fd);
    const size = stat.size;
    const readLen = Math.min(size, Math.max(0, maxBytes));
    const start = size - readLen;
    const buf = Buffer.alloc(readLen);
    // readSync may return fewer bytes than requested if the file is being
    // written concurrently; loop until the buffer is filled or we hit EOF.
    let read = 0;
    while (read < readLen) {
      // openSync's positional read overload (offset) is used so we don't rely
      // on the file pointer's current position.
      const n = readSync(fd, buf, read, readLen - read, start + read);
      if (n === 0) break;
      read += n;
    }
    return {
      path,
      exists: true,
      content: buf.subarray(0, read).toString("utf8"),
      bytes: read,
    };
  } catch (err) {
    return {
      path,
      exists: true,
      content: "",
      bytes: 0,
      error: err instanceof Error ? err.message : String(err),
    };
  } finally {
    if (fd !== undefined) {
      try {
        closeSync(fd);
      } catch {
        // best-effort
      }
    }
  }
}
