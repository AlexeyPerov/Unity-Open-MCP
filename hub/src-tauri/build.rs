use std::fs;
use std::path::PathBuf;

fn main() {
    tauri_build::build();

    // M28 Plan 5 — the wizard's default bridge/verify git-URL tag pins must
    // derive from the shared trio version source (repo-root `version.json`)
    // so they can never drift to a nonexistent tag. We read the version here
    // and emit the full tag strings as env vars (`TRIO_BRIDGE_TAG` /
    // `TRIO_VERIFY_TAG`), which `config::wizard` bakes into the
    // `DEFAULT_BRIDGE_TAG` / `DEFAULT_VERIFY_TAG` constants via `env!`.
    //
    // This mirrors what `scripts/sync-version.mjs` is the source of truth for
    // on the JS side; both pull from the same `version.json`. The CI
    // version-sync gate fails any PR where a generated trio target (including
    // the wizard Packages-step UI example) has drifted from `version.json`.
    //
    // The version.json body is a single-line `{"version":"X.Y.Z"}` object, so
    // a tiny regex-free extractor is enough — we avoid pulling serde_json in
    // as a build-dependency just for this.
    let manifest_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    let version_json = manifest_dir.join("..").join("version.json");
    // Fall back to a sentinel so the crate still compiles in a standalone
    // `cargo build` without the repo root. The CI version-sync gate catches
    // any real drift, and the wizard surfaces a clear upgrade error if a
    // sentinel tag ever shipped.
    let trio_version = fs::read_to_string(&version_json)
        .ok()
        .and_then(|b| extract_version_field(&b))
        .unwrap_or_else(|| "0.0.0-unknown".to_string());
    println!("cargo:rustc-env=TRIO_BRIDGE_TAG=bridge-v{}", trio_version);
    println!("cargo:rustc-env=TRIO_VERIFY_TAG=verify-v{}", trio_version);
    println!("cargo:rerun-if-changed=../version.json");
}

/// Extract the `"version": "X.Y.Z"` string from a version.json body without a
/// JSON parser. Returns the first quoted value following the `"version"` key.
fn extract_version_field(body: &str) -> Option<String> {
    let key = "\"version\"";
    let key_idx = body.find(key)?;
    let after_key = &body[key_idx + key.len()..];
    let colon_idx = after_key.find(':')?;
    let after_colon = &after_key[colon_idx + 1..];
    let quote_idx = after_colon.find('"')?;
    let value_start = after_colon[quote_idx + 1..].as_bytes();
    let mut end = 0usize;
    while end < value_start.len() && value_start[end] != b'"' {
        end += 1;
    }
    let value = std::str::from_utf8(&value_start[..end]).ok()?;
    let trimmed = value.trim();
    if trimmed.is_empty() {
        None
    } else {
        Some(trimmed.to_string())
    }
}
