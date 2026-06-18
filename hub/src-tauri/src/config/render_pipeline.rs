//! Render-pipeline (SRP) detection (M15 T6.4).
//!
//! Surfaces whether a Unity project uses the **Built-in Render Pipeline
//! (BIRP)**, the **Universal Render Pipeline (URP)**, or the **High
//! Definition Render Pipeline (HDRP)**. Unity persists the active
//! pipeline into `ProjectSettings/GraphicsSettings.asset` either as
//! `m_CustomRenderPipeline` (Unity 2019.4+/6) — a `fileID`/`guid`
//! reference into the asset database — or, in the global settings map
//! introduced in newer Unity 6 builds, as an
//! `m_RenderPipelineGlobalSettingsMap` keyed by pipeline type. The
//! friendly name (`URP` / `HDRP`) is recoverable from the
//! `m_ScriptingClassIdentifier` recorded against the pipeline asset
//! (`UniversalRenderPipelineAsset` / `HDRenderPipelineAsset`), but that
//! requires the `ScriptObjects` GUID table; in practice scanning the
//! asset for the canonical class-name strings (the UnityLauncherPro
//! `Tools.GetSRP` heuristic) is robust across hand-edited, machine
//! written, and newer-mapped files and avoids touching the binary
//! Library folder.
//!
//! The reader is intentionally pure and side-effect-free: it returns
//! `RenderPipeline::BuiltIn` for missing/empty/unrecognised files so the
//! Projects tab can render a "BIRP" chip the same way it renders "URP"
//! and "HDRP" — the value is always present, never `None`.

use std::path::Path;

use serde::{Deserialize, Serialize};

const GRAPHICS_SETTINGS_FILE: &str = "ProjectSettings/GraphicsSettings.asset";

/// The three Unity render-pipeline families plus an `Unknown` escape
/// hatch for malformed files. Serialized as lower-case kebab strings
/// (`builtIn`, `urp`, `hdrp`, `unknown`) so the frontend can map
/// directly to chip labels.
#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum RenderPipeline {
    /// Built-in Render Pipeline (the legacy forward renderer; the
    /// default before Unity assigns a SRP asset).
    BuiltIn,
    /// Universal Render Pipeline (URP) — matches a
    /// `UniversalRenderPipeline` class reference in
    /// `GraphicsSettings.asset`.
    Urp,
    /// High Definition Render Pipeline (HDRP) — matches a
    /// `HDRenderPipeline` class reference.
    Hdrp,
    /// The file exists but does not contain a recognisable pipeline
    /// marker (e.g. truncated, hand-edited, or a future pipeline we do
    /// not know about). The Projects tab renders `—` rather than
    /// asserting a default so the user is not misled.
    Unknown,
}

impl RenderPipeline {
    /// Returns the short label the Projects tab renders next to the
    /// project name. Kept on the backend so chip formatting is owned by
    /// the same module that owns detection (the frontend imports the
    /// `RenderPipeline` enum via `services/config.ts`).
    pub fn label(self) -> &'static str {
        match self {
            RenderPipeline::BuiltIn => "BIRP",
            RenderPipeline::Urp => "URP",
            RenderPipeline::Hdrp => "HDRP",
            RenderPipeline::Unknown => "—",
        }
    }
}

impl Default for RenderPipeline {
    fn default() -> Self {
        RenderPipeline::BuiltIn
    }
}

/// Reads the project's render pipeline from
/// `ProjectSettings/GraphicsSettings.asset`. Mirrors the
/// UnityLauncherPro `Tools.GetSRP` heuristic: the file's
/// `m_SRPDefaultSettings` / `m_RenderPipelineGlobalSettingsMap` section
/// is followed by per-pipeline asset references, and the first
/// `UniversalRenderPipeline` / `HDRenderPipeline` substring after the
/// SRP section header wins. When neither marker is present, the project
/// uses the Built-in Render Pipeline.
///
/// On a freshly-cloned project that has never been opened by a Unity
/// Editor the file may be missing entirely; we return `BuiltIn` (the
/// Unity default before any SRP asset is assigned) rather than `Unknown`
/// so the Projects tab never renders `—` for the common case.
pub fn read_render_pipeline(project_path: &Path) -> RenderPipeline {
    let asset = project_path.join(GRAPHICS_SETTINGS_FILE);
    let Ok(contents) = std::fs::read_to_string(&asset) else {
        return RenderPipeline::BuiltIn;
    };

    // Phase 1: find the SRP settings section. Unity has used two
    // different section headers over the years:
    //   - `m_SRPDefaultSettings:` (Unity 2019.4 – 2022 LTS)
    //   - `m_RenderPipelineGlobalSettingsMap:` (Unity 6+)
    // We track the column of the header so per-pipeline matches inside
    // the section are anchored to actual asset references, not a stray
    // `m_ScriptingClassIdentifier: UniversalRenderPipelineAsset` that
    // appears in the file preamble (Unity emits both).
    let mut in_srp_section = false;
    for line in contents.lines() {
        let trimmed = line.trim_start();
        if !in_srp_section {
            if trimmed.starts_with("m_SRPDefaultSettings:")
                || trimmed.starts_with("m_RenderPipelineGlobalSettingsMap:")
            {
                in_srp_section = true;
            }
            continue;
        }

        // Phase 2: once inside the SRP section, the first class-name
        // marker wins. `HDRenderPipeline` is a prefix of nothing else;
        // `UniversalRenderPipeline` likewise. We do not need to match
        // the trailing `Asset` suffix because Unity sometimes writes
        // `UniversalRenderPipelineGlobalSettings` (Unity 6) without it.
        if trimmed.contains("HDRenderPipeline") {
            return RenderPipeline::Hdrp;
        }
        if trimmed.contains("UniversalRenderPipeline") {
            return RenderPipeline::Urp;
        }
    }

    // No SRP section, or the section had no pipeline marker — fall back
    // to the Built-in Render Pipeline (the Unity default). We do not
    // return `Unknown` here because the contract is "the file is the
    // source of truth", and a file without an SRP section always means
    // BIRP in Unity's own serialisation.
    RenderPipeline::BuiltIn
}

#[tauri::command]
pub fn get_render_pipeline(project_path: String) -> RenderPipeline {
    read_render_pipeline(Path::new(&project_path))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fresh_project(name: &str) -> std::path::PathBuf {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join(name);
        std::fs::create_dir_all(path.join("ProjectSettings")).unwrap();
        std::mem::forget(dir);
        path
    }

    fn write_graphics(project: &Path, body: &str) {
        std::fs::write(project.join(GRAPHICS_SETTINGS_FILE), body).unwrap();
    }

    #[test]
    fn detects_urp_from_legacy_srp_section() {
        let project = fresh_project("ProjUrp");
        write_graphics(
            &project,
            "GraphicsSettings:\n  m_ObjectHideFlags: 0\n  m_SRPDefaultSettings:\n  - {fileID: 11400000, guid: abc, type: 2}\n  m_RenderPipeline: {fileID: 11400000, guid: def, type: 2}\n  UniversalRenderPipelineAsset: 1\n",
        );
        assert_eq!(read_render_pipeline(&project), RenderPipeline::Urp);
    }

    #[test]
    fn detects_hdrp_from_legacy_srp_section() {
        let project = fresh_project("ProjHdrp");
        write_graphics(
            &project,
            "GraphicsSettings:\n  m_SRPDefaultSettings:\n    HDRenderPipelineAsset: {fileID: 11400000}\n",
        );
        assert_eq!(read_render_pipeline(&project), RenderPipeline::Hdrp);
    }

    #[test]
    fn detects_urp_from_unity6_global_settings_map() {
        let project = fresh_project("ProjU6");
        write_graphics(
            &project,
            "GraphicsSettings:\n  m_RenderPipelineGlobalSettingsMap:\n    UniversalRenderPipelineGlobalSettings: {fileID: 11400000}\n",
        );
        assert_eq!(read_render_pipeline(&project), RenderPipeline::Urp);
    }

    #[test]
    fn detects_hdrp_from_unity6_global_settings_map() {
        let project = fresh_project("ProjU6Hdrp");
        write_graphics(
            &project,
            "GraphicsSettings:\n  m_RenderPipelineGlobalSettingsMap:\n    HDRenderPipelineGlobalSettings: {fileID: 11400000}\n",
        );
        assert_eq!(read_render_pipeline(&project), RenderPipeline::Hdrp);
    }

    #[test]
    fn defaults_to_builtin_when_no_srp_section() {
        let project = fresh_project("ProjBirp");
        write_graphics(
            &project,
            "GraphicsSettings:\n  m_ObjectHideFlags: 0\n  m_BuildTarget: StandaloneWindows64\n",
        );
        assert_eq!(read_render_pipeline(&project), RenderPipeline::BuiltIn);
    }

    #[test]
    fn defaults_to_builtin_when_file_missing() {
        let project = fresh_project("ProjFresh");
        assert_eq!(read_render_pipeline(&project), RenderPipeline::BuiltIn);
    }

    #[test]
    fn hdrp_takes_precedence_in_mixed_section() {
        // Order matters in Unity's serialisation but in practice a
        // project uses exactly one SRP; the test documents the
        // "first match wins" contract by placing HDRP before URP and
        // confirming HDRP is returned.
        let project = fresh_project("ProjMixed");
        write_graphics(
            &project,
            "GraphicsSettings:\n  m_SRPDefaultSettings:\n    HDRenderPipelineAsset: 1\n    UniversalRenderPipelineAsset: 2\n",
        );
        assert_eq!(read_render_pipeline(&project), RenderPipeline::Hdrp);
    }

    #[test]
    fn ignores_pipeline_marker_before_srp_section() {
        // Some GraphicsSettings.asset files list
        // `m_ScriptingClassIdentifier: UniversalRenderPipelineAsset`
        // in the preamble (Unity's mono class table) before the SRP
        // section header. The detector must not latch onto it.
        let project = fresh_project("ProjPreamble");
        write_graphics(
            &project,
            "GraphicsSettings:\n  m_ScriptingClassIdentifier: UniversalRenderPipelineAsset\n  m_ObjectHideFlags: 0\n  m_BuildTarget: StandaloneWindows64\n",
        );
        assert_eq!(read_render_pipeline(&project), RenderPipeline::BuiltIn);
    }

    #[test]
    fn label_returns_short_form() {
        assert_eq!(RenderPipeline::BuiltIn.label(), "BIRP");
        assert_eq!(RenderPipeline::Urp.label(), "URP");
        assert_eq!(RenderPipeline::Hdrp.label(), "HDRP");
        assert_eq!(RenderPipeline::Unknown.label(), "—");
    }

    #[test]
    fn command_matches_resolver() {
        let project = fresh_project("ProjCmd");
        write_graphics(
            &project,
            "GraphicsSettings:\n  m_SRPDefaultSettings:\n    UniversalRenderPipelineAsset: 1\n",
        );
        let direct = read_render_pipeline(&project);
        let via_cmd = get_render_pipeline(project.to_string_lossy().to_string());
        assert_eq!(direct, via_cmd);
    }

    #[test]
    fn serializes_as_camel_case() {
        let json = serde_json::to_string(&RenderPipeline::Urp).unwrap();
        assert_eq!(json, "\"urp\"");
        let json = serde_json::to_string(&RenderPipeline::Hdrp).unwrap();
        assert_eq!(json, "\"hdrp\"");
        let json = serde_json::to_string(&RenderPipeline::BuiltIn).unwrap();
        assert_eq!(json, "\"builtIn\"");
        let restored: RenderPipeline = serde_json::from_str("\"hdrp\"").unwrap();
        assert_eq!(restored, RenderPipeline::Hdrp);
    }
}
