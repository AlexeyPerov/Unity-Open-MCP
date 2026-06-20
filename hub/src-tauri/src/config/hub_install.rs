//! Unity Hub install integration via the `unityhub://` deep link.
//!
//! Replaces the earlier headless `Unity Hub -- --headless install …`
//! child-process approach. That spawned a fresh Hub process per click,
//! showed a disconnected Hub window, and streamed raw `downloading N%`
//! stdout into the drawer with no real progress UI — the multi-GB Unity
//! 6 download looked stuck forever.
//!
//! The Hub registers a `unityhub://<version>/<changeset>` URL handler.
//! Opening it (the same deep link Unity's own releases archive fires
//! when you click "Install") brings the Hub to the foreground at its
//! native install dialog: single instance, real progress bar, module
//! selection. The install then happens inside the Hub, outside this app.
//!
//! There is no in-app completion detection (the Hub owns the process),
//! so the frontend asks the user to click Refresh on the Installed
//! panel once the Hub finishes.

use tauri::AppHandle;
use tauri_plugin_opener::OpenerExt;

/// Build the `unityhub://` deep link for a release. When a changeset is
/// available (the normal case — the feed exposes one for nearly every
/// release) the link is `unityhub://<version>/<changeset>`, which the
/// Hub resolves to the exact build. Without a changeset the link is
/// `unityhub://<version>`; older Hub versions may ignore a
/// changeset-less link, so the frontend falls back to pointing the user
/// at the release-notes page in that case.
fn build_deep_link(version: &str, changeset: Option<&str>) -> String {
    match changeset {
        Some(cs) if !cs.is_empty() => format!("unityhub://{}/{}", version, cs),
        _ => format!("unityhub://{}", version),
    }
}

/// Open Unity Hub at its install dialog for `<version>` by firing the
/// `unityhub://` deep link via the opener plugin. The Hub must be
/// installed and registered as the system handler for the scheme; if it
/// isn't, the OS reports the failure and we surface it to the frontend.
#[tauri::command]
pub fn open_unity_hub_install(
    app: AppHandle,
    version: String,
    changeset: Option<String>,
) -> Result<(), String> {
    if version.trim().is_empty() {
        return Err("No version specified.".to_string());
    }
    let link = build_deep_link(version.trim(), changeset.as_deref().and_then(|c| {
        let trimmed = c.trim();
        if trimmed.is_empty() {
            None
        } else {
            Some(trimmed)
        }
    }));
    app.opener()
        .open_url(link, None::<&str>)
        .map_err(|e| format!("could not open Unity Hub install dialog: {e}"))
}

// ── Tests ──────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn build_deep_link_includes_changeset_when_present() {
        assert_eq!(
            build_deep_link("6000.5.0f1", Some("88b47c5e7076")),
            "unityhub://6000.5.0f1/88b47c5e7076"
        );
    }

    #[test]
    fn build_deep_link_omits_changeset_when_none() {
        assert_eq!(build_deep_link("6000.5.0f1", None), "unityhub://6000.5.0f1");
    }

    #[test]
    fn build_deep_link_omits_empty_changeset() {
        assert_eq!(
            build_deep_link("6000.5.0f1", Some("")),
            "unityhub://6000.5.0f1"
        );
    }
}
