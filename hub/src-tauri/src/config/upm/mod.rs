//! UPM (Unity Package Manager) package editing — a port of the
//! relevant pieces of `UPM-Template-Creator/upm_manager.go`.
//!
//! Unlike the Go original, the manifest struct models the **full**
//! Unity package.json schema including `dependencies` (which the Go
//! tool omitted). The `.meta` generation, GUID regeneration, and
//! changelog bumping are also ported.
//!
//! Modules:
//!   - [`manifest`] — read/write package.json (full schema)
//!   - [`meta`]     — .meta content generation, GUID regen, missing-meta fixup
//!   - [`changelog`] — Keep-a-Changelog version section prepending
//!   - [`migrate`]  — append-and-replace file copy from a source folder

pub mod manifest;
pub mod meta;
pub mod changelog;
pub mod migrate;
pub mod create;
