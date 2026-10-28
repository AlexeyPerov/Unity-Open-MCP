// Playtest — player-state sandbox for a live playtest.
//
// snapshot / diff / restore / list / delete over the state a play session
// changes: PlayerPrefs (all keys, typed — read from the platform store after the
// bridge flushes PlayerPrefs.Save, since Unity has no enumeration API), opt-in
// EditorPrefs by key prefix, and the files under persistentDataPath. Snapshots
// live in the platform app-data folder outside the project
// (`…/unity-open-mcp/player-state/<company>/<product>/<name>/`). Only `restore`
// with `confirm: true` writes state: the bridge classifies it as a mutation per
// request, so it runs the normal gate + audit path; its `paths_hint` defaults to
// ProjectSettings/ProjectSettings.asset (the company / product identity the
// snapshot is bound to — restore writes no project asset). Group `playtest`.
import { GATE_PROP, makeTool } from "./schema-fragments.js";

export const PLAYER_STATE_ACTIONS = ["snapshot", "diff", "restore", "list", "delete"] as const;
export const PLAYER_STATE_RESTORE_SCOPE = "ProjectSettings/ProjectSettings.asset";
const SNAPSHOT_NAME_PATTERN = "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$";

export const playerState = makeTool(
  "unity_open_mcp_player_state",
  "Isolate the player state a live playtest changes: snapshot it, see what a session changed, put it back. " +
    "STATE = PlayerPrefs (every key with its type, read from the platform store after PlayerPrefs.Save so it " +
    "is what the game wrote, even unsaved), persistentDataPath files (size + sha256; default excludes " +
    "Unity/** and TestResults.xml, cap max_files_mb), and EditorPrefs only under editor_prefs_prefixes " +
    "(opt-in; EditorPrefs are shared by every project on the machine). Unity's own session keys " +
    "(unity.player_session*, unity_connect.*) are excluded by default; excluded keys and paths are never " +
    "captured, diffed or touched. Snapshots are stored outside the project in the app-data folder " +
    "(…/unity-open-mcp/player-state/<company>/<product>/<name>/). ACTIONS: snapshot = capture into `name` " +
    "(snapshot_exists unless overwrite); diff = `name` vs the current state (or vs snapshot `against`): " +
    "prefs / editor_prefs / files added, removed, changed — small values shown, long strings and files as " +
    "size + sha256; restore = EDIT MODE ONLY; without confirm a dry run returning the diff it would apply " +
    "(current → snapshot) with EVERY removal listed; with confirm:true it OVERWRITES the current state — " +
    "PlayerPrefs.DeleteAll + the snapshot's keys + Save (so the Editor's in-memory cache and the store agree), " +
    "EditorPrefs only under the snapshot's prefixes, persistentDataPath mirrored exactly (files not in the " +
    "snapshot are DELETED) — then re-reads and reports verified; it is the only gated action (paths_hint " +
    "defaults to ProjectSettings/ProjectSettings.asset) and refuses edit_mode_required in play mode and " +
    "snapshot_project_mismatch for another project's snapshot; list / delete = the snapshots of this " +
    "project (company + product address another project's store). Prefer a project's own isolated-save " +
    "hook (project_commands playtest.begin / playtest.end) when it exists; otherwise snapshot → play → diff " +
    "→ stop → restore (dry run, then confirm). macOS verified; Windows registry and Linux readers are " +
    "untested in the field.",
  {
    required: ["action"],
    properties: {
      action: {
        type: "string",
        enum: [...PLAYER_STATE_ACTIONS],
        description:
          "snapshot: capture into name. diff: name vs current (or against). restore: dry run, or apply with " +
          "confirm:true (edit mode). list: snapshots of this project. delete: remove snapshot name.",
      },
      name: {
        type: "string",
        pattern: SNAPSHOT_NAME_PATTERN,
        description:
          "[snapshot / diff / restore / delete] Snapshot name: 1-64 of [A-Za-z0-9._-], starting with a letter or digit.",
      },
      against: {
        type: "string",
        pattern: SNAPSHOT_NAME_PATTERN,
        description: "[diff] Compare `name` with this other snapshot instead of the current state.",
      },
      confirm: {
        type: "boolean",
        default: false,
        description:
          "[restore] false (default) = dry run: returns the diff restore would apply, nothing changes. " +
          "true = apply: overwrites PlayerPrefs, opted-in EditorPrefs and persistentDataPath (removing files not " +
          "in the snapshot). Review the dry run's removals first.",
      },
      editor_prefs_prefixes: {
        type: "array",
        items: { type: "string", minLength: 3 },
        minItems: 1,
        maxItems: 16,
        description:
          "[snapshot] Also capture EditorPrefs whose key starts with one of these prefixes (≥ 3 characters each). " +
          "Off by default: EditorPrefs are shared by all projects on this machine. restore touches only keys " +
          "under the snapshot's prefixes (never EditorPrefs.DeleteAll).",
      },
      exclude: {
        type: "array",
        items: { type: "string", minLength: 1 },
        maxItems: 32,
        description:
          "[snapshot] Extra glob patterns of persistentDataPath paths to leave out (added to the defaults " +
          "Unity/** and TestResults.xml): '**' any depth, '*' / '?' within a name; a pattern without '/' " +
          "matches a file or folder name at any depth (e.g. '*.log', 'Cache/**'). Excluded paths are never " +
          "restored or removed.",
      },
      exclude_keys: {
        type: "array",
        items: { type: "string", minLength: 1 },
        maxItems: 32,
        description:
          "[snapshot] Extra PlayerPrefs key patterns to leave out ('*' any run, '?' one character), added to " +
          "the defaults unity.player_session* and unity_connect.* (Unity's per-session bookkeeping). Excluded " +
          "keys keep their current values on restore.",
      },
      max_files_mb: {
        type: "integer",
        minimum: 1,
        maximum: 1024,
        description:
          "[snapshot] Size cap for the persistentDataPath copy after excludes (default 64; at most 5000 files). " +
          "Over the cap → persistent_data_too_large with the largest files, nothing written.",
      },
      overwrite: {
        type: "boolean",
        description: "[snapshot] Replace an existing snapshot of the same name (default false → snapshot_exists).",
      },
      company: {
        type: "string",
        minLength: 1,
        description:
          "[diff / restore / list / delete] Company name of another project's snapshot store (default: the open " +
          "project's). restore refuses a snapshot whose company / product differ from the open project.",
      },
      product: {
        type: "string",
        minLength: 1,
        description: "[diff / restore / list / delete] Product name of another project's snapshot store (default: the open project's).",
      },
      limit: {
        type: "integer",
        minimum: 1,
        maximum: 1000,
        description:
          "[diff / restore] Entries listed per added / changed list (default 100; counts are always complete, " +
          "`truncated` says how many were left out). restore always lists every removal.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        default: [PLAYER_STATE_RESTORE_SCOPE],
        description:
          "Audit / gate scope for restore with confirm:true, materialized automatically: restore writes no " +
          "project asset; ProjectSettings.asset holds the company / product the snapshot is bound to. Ignored " +
          "by every other action.",
      },
      gate: {
        ...GATE_PROP,
        description: "[restore confirm:true] Gate mode for the applied restore. Other actions are not gated.",
      },
    },
  },
);
