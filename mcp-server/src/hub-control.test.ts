// Tests for hub-control.ts. Pure-function parsers are tested with string
// fixtures (like unity-install-discovery.test.ts); the side-effecting layer is
// tested via injected runners (like dialog-dismiss.test.ts's makeFakeProbe).
// No real child-process mocking (no such infra exists in the suite).

import { test } from "node:test";
import assert from "node:assert/strict";

import {
  friendlyPlaybackEngineName,
  scanPlaybackEngines,
  listInstalledEditors,
  parseInstalledEditors,
  parseInstallPath,
  resolveHubCliPath,
  _resetHubCliPathCacheForTests,
  streamFromUnityStr,
  extractChangeset,
  normalizeReleaseDate,
  parseArchivePayload,
  snapshotReleases,
  buildInstallDeepLink,
  getInstallPath,
  setInstallPath,
  openInstallDeepLink,
  fetchAvailableReleases,
  releaseTypeFor,
  type HubCliRunner,
  type UrlOpener,
  type ArchiveFetcher,
} from "./hub-control.js";

// ── friendlyPlaybackEngineName ─────────────────────────────────────

test("friendlyPlaybackEngineName: maps known platform keys", () => {
  assert.equal(friendlyPlaybackEngineName("AndroidPlayer"), "Android");
  assert.equal(friendlyPlaybackEngineName("windowsstandalonesupport"), "Win64");
  assert.equal(friendlyPlaybackEngineName("LinuxStandaloneSupport"), "Linux64");
  assert.equal(friendlyPlaybackEngineName("OSXStandaloneSupport"), "OSX");
  assert.equal(friendlyPlaybackEngineName("WebGLSupport"), "WebGL");
  assert.equal(friendlyPlaybackEngineName("iOSSupport"), "iOS");
  assert.equal(friendlyPlaybackEngineName("MetroSupport"), "UWP");
  assert.equal(friendlyPlaybackEngineName("AppleTVSupport"), "tvOS");
  assert.equal(friendlyPlaybackEngineName("VisionOSPlayer"), "visionOS");
  assert.equal(friendlyPlaybackEngineName("SwitchPlayer"), "Switch");
});

test("friendlyPlaybackEngineName: PS4/PS5 uppercased, unknown passes through unchanged", () => {
  assert.equal(friendlyPlaybackEngineName("PS4Player"), "PS4PLAYER");
  assert.equal(friendlyPlaybackEngineName("PS5Player"), "PS5PLAYER");
  assert.equal(friendlyPlaybackEngineName("SomeNewPlatform"), "SomeNewPlatform");
});

// ── scanPlaybackEngines (filesystem) ───────────────────────────────

test("scanPlaybackEngines: returns empty when Data folder missing", () => {
  assert.deepEqual(scanPlaybackEngines("/nonexistent/path/that/does/not/exist"), []);
});

// ── releaseTypeFor ─────────────────────────────────────────────────

test("releaseTypeFor: LTS lines resolve to LTS", () => {
  assert.equal(releaseTypeFor("6000.0.1f1"), "LTS");
  assert.equal(releaseTypeFor("2022.3.62f2"), "LTS");
  assert.equal(releaseTypeFor("2021.3.28f1"), "LTS");
});

test("releaseTypeFor: non-LTS final resolves to TECH", () => {
  assert.equal(releaseTypeFor("6000.4.12f1"), "TECH");
});

test("releaseTypeFor: alpha/beta stream markers", () => {
  assert.equal(releaseTypeFor("6000.2.0a3"), "Alpha");
  assert.equal(releaseTypeFor("6000.1.0b5"), "Beta");
});

test("releaseTypeFor: no kind marker -> empty", () => {
  assert.equal(releaseTypeFor("garbage"), "");
  assert.equal(releaseTypeFor("1.2.3"), "");
});

// ── listInstalledEditors ───────────────────────────────────────────

test("listInstalledEditors: empty roots -> empty list", () => {
  assert.deepEqual(listInstalledEditors(["/nonexistent"]), []);
});

test("listInstalledEditors: never throws on unreadable roots", () => {
  // Passing a file (not a dir) as a root must not throw.
  assert.deepEqual(listInstalledEditors(["/etc/hosts"]), []);
});

// ── parseInstalledEditors ──────────────────────────────────────────

test("parseInstalledEditors: parses version-at-path lines", () => {
  const stdout = [
    "2022.3.0f1 , installed at C:\\Program Files\\Unity\\Hub\\Editor\\2022.3.0f1",
    "6000.0.1f1, installed at /Applications/Unity/Hub/Editor/6000.0.1f1",
    "",
    "this is not a release line",
  ].join("\n");
  const editors = parseInstalledEditors(stdout);
  assert.equal(editors.length, 2);
  assert.equal(editors[0].version, "2022.3.0f1");
  assert.equal(editors[0].path, "C:\\Program Files\\Unity\\Hub\\Editor\\2022.3.0f1");
  assert.equal(editors[1].version, "6000.0.1f1");
  assert.equal(editors[1].path, "/Applications/Unity/Hub/Editor/6000.0.1f1");
});

test("parseInstalledEditors: empty input -> empty list", () => {
  assert.deepEqual(parseInstalledEditors(""), []);
  assert.deepEqual(parseInstalledEditors("no matching lines here\n"), []);
});

// ── parseInstallPath ───────────────────────────────────────────────

test("parseInstallPath: returns trimmed path", () => {
  assert.equal(
    parseInstallPath("Default install path: /Applications/Unity/Hub/Editor\n"),
    "/Applications/Unity/Hub/Editor",
  );
});

test("parseInstallPath: bare path line", () => {
  assert.equal(
    parseInstallPath("C:\\Program Files\\Unity\\Hub\\Editor"),
    "C:\\Program Files\\Unity\\Hub\\Editor",
  );
});

test("parseInstallPath: empty -> null", () => {
  assert.equal(parseInstallPath(""), null);
  assert.equal(parseInstallPath("   \n  "), null);
});

// ── resolveHubCliPath ──────────────────────────────────────────────

test("resolveHubCliPath: honors UNITY_HUB_PATH env when file exists", () => {
  _resetHubCliPathCacheForTests();
  const saved = process.env.UNITY_HUB_PATH;
  // A path that exists on POSIX CI hosts.
  process.env.UNITY_HUB_PATH = process.platform === "win32" ? "C:\\Windows\\System32\\cmd.exe" : "/bin/sh";
  try {
    const p = resolveHubCliPath();
    assert.equal(p, process.env.UNITY_HUB_PATH);
  } finally {
    if (saved === undefined) delete process.env.UNITY_HUB_PATH;
    else process.env.UNITY_HUB_PATH = saved;
    _resetHubCliPathCacheForTests();
  }
});

test("resolveHubCliPath: falls through to OS defaults for a missing env override", () => {
  _resetHubCliPathCacheForTests();
  const saved = process.env.UNITY_HUB_PATH;
  process.env.UNITY_HUB_PATH = "/definitely/not/a/real/path/12345";
  try {
    const p = resolveHubCliPath();
    // Env override is missing -> falls through to OS defaults, which may or may
    // not exist on the test host. Assert it is a string-or-null, not undefined.
    assert.equal(typeof p === "string" || p === null, true);
  } finally {
    if (saved === undefined) delete process.env.UNITY_HUB_PATH;
    else process.env.UNITY_HUB_PATH = saved;
    _resetHubCliPathCacheForTests();
  }
});

// ── stream / changeset / date helpers ──────────────────────────────

test("streamFromUnityStr: known streams map; unknown falls back to TECH", () => {
  assert.equal(streamFromUnityStr("LTS"), "LTS");
  assert.equal(streamFromUnityStr("SUPPORTED"), "Supported");
  assert.equal(streamFromUnityStr("TECH"), "TECH");
  assert.equal(streamFromUnityStr("BETA"), "Beta");
  assert.equal(streamFromUnityStr("ALPHA"), "Alpha");
  assert.equal(streamFromUnityStr("NEW_UNKNOWN"), "TECH");
});

test("extractChangeset: parses version/changeset deep link", () => {
  assert.equal(extractChangeset("unityhub://6000.5.0f1/88b47c5e7076"), "88b47c5e7076");
  assert.equal(extractChangeset("unityhub://6000.0.0f1/"), null);
  assert.equal(extractChangeset("unityhub://6000.0.0f1"), null);
  assert.equal(extractChangeset(undefined), null);
  assert.equal(extractChangeset("not a hub link"), null);
});

test("normalizeReleaseDate: trims ISO timestamp to date", () => {
  assert.equal(normalizeReleaseDate("2026-06-17T15:09:23.805Z"), "2026-06-17");
  assert.equal(normalizeReleaseDate("2026-06-17"), "2026-06-17");
  assert.equal(normalizeReleaseDate(undefined), null);
  assert.equal(normalizeReleaseDate(""), null);
  // Non-ISO passes through unchanged.
  assert.equal(normalizeReleaseDate("Summer 2026"), "Summer 2026");
});

// ── parseArchivePayload ────────────────────────────────────────────

// Build a minimal RSC payload segment matching Unity's archive page shape so
// the parser can be exercised without a network fetch.
function makeArchiveHtml(nodeJson: string): string {
  // The segment body is a JSON-string-escaped "31:<inner-json>". We build the
  // inner JSON, prefix it, JSON-encode it as a string literal, and embed it in
  // the marker the parser scans for.
  const inner = "31:" + nodeJson;
  const literal = JSON.stringify(inner).slice(1, -1); // strip outer quotes, keep escapes
  const marker = `self.__next_f.push([1,"${literal}"])`;
  // Surround with decoy segments to prove the parser picks the right one.
  return `<html>self.__next_f.push([1,"99:decoy"])\n${marker}\nmore</html>`;
}

test("parseArchivePayload: extracts release entries newest-first", () => {
  const nodeJson = JSON.stringify({
    getUnityReleases: {
      edges: [
        {
          node: {
            version: "6000.3.18f1",
            releaseDate: "2026-06-17T15:09:23.805Z",
            unityHubDeepLink: "unityhub://6000.3.18f1/5ebeb53e4c07",
            stream: "LTS",
          },
        },
        {
          node: {
            version: "6000.4.12f1",
            releaseDate: "2026-06-20T00:00:00.000Z",
            unityHubDeepLink: "unityhub://6000.4.12f1/3ca267ce8005",
            stream: "SUPPORTED",
          },
        },
      ],
    },
  });
  const html = makeArchiveHtml(nodeJson);
  const entries = parseArchivePayload(html);
  assert.ok(entries, "expected parsed entries");
  assert.equal(entries!.length, 2);
  // Newest-first sort.
  assert.equal(entries![0].version, "6000.4.12f1");
  assert.equal(entries![0].stream, "Supported");
  assert.equal(entries![0].changeset, "3ca267ce8005");
  assert.equal(entries![0].releaseDate, "2026-06-20");
  assert.equal(entries![0].releaseNotesUrl, "https://unity.com/releases/editor/whats-new/6000.4.12f1");
  assert.equal(entries![1].version, "6000.3.18f1");
  assert.equal(entries![1].stream, "LTS");
});

test("parseArchivePayload: missing deep link -> null changeset", () => {
  const nodeJson = JSON.stringify({
    getUnityReleases: {
      edges: [
        { node: { version: "6000.0.32f1", releaseDate: "2026-05-14", stream: "TECH" } },
      ],
    },
  });
  const entries = parseArchivePayload(makeArchiveHtml(nodeJson));
  assert.ok(entries);
  assert.equal(entries![0].changeset, null);
  assert.equal(entries![0].stream, "TECH");
});

test("parseArchivePayload: returns null when no releases segment present", () => {
  const html = '<html>self.__next_f.push([1,"31:nope"])\nrandom</html>';
  assert.equal(parseArchivePayload(html), null);
});

test("parseArchivePayload: returns null for empty input", () => {
  assert.equal(parseArchivePayload(""), null);
});

test("snapshotReleases: returns a non-empty newest-first list", () => {
  const entries = snapshotReleases();
  assert.ok(entries.length >= 3);
  // Newest-first by date.
  for (let i = 1; i < entries.length; i++) {
    const prev = entries[i - 1].releaseDate ?? "";
    const cur = entries[i].releaseDate ?? "";
    assert.ok(prev >= cur, `expected newest-first; ${prev} < ${cur}`);
  }
});

// ── buildInstallDeepLink ───────────────────────────────────────────

test("buildInstallDeepLink: includes changeset when present", () => {
  assert.equal(
    buildInstallDeepLink("6000.5.0f1", "88b47c5e7076"),
    "unityhub://6000.5.0f1/88b47c5e7076",
  );
});

test("buildInstallDeepLink: omits changeset when absent", () => {
  assert.equal(buildInstallDeepLink("6000.5.0f1"), "unityhub://6000.5.0f1");
  assert.equal(buildInstallDeepLink("6000.5.0f1", ""), "unityhub://6000.5.0f1");
});

// ── getInstallPath (injectable runner) ─────────────────────────────

const fakeCliOk: HubCliRunner = () => ({
  stdout: "Default install path: /Applications/Unity/Hub/Editor\n",
  stderr: "",
  exitCode: 0,
});

const fakeCliNotFound: HubCliRunner = () => ({ stdout: "", stderr: "", exitCode: -1 });

test("getInstallPath: parses Hub CLI output", () => {
  const res = getInstallPath({ runHubCli: fakeCliOk });
  assert.equal(res.path, "/Applications/Unity/Hub/Editor");
  assert.equal(res.source, "hub-cli");
  assert.equal(res.error, null);
});

test("getInstallPath: falls back to filesystem when CLI missing", () => {
  const res = getInstallPath({ runHubCli: fakeCliNotFound, roots: ["/fake/Unity/Hub/Editor"] });
  assert.equal(res.path, "/fake/Unity/Hub/Editor");
  assert.equal(res.source, "filesystem");
  assert.equal(res.error, null);
});

test("getInstallPath: errors when both CLI and filesystem unavailable", () => {
  const res = getInstallPath({ runHubCli: fakeCliNotFound, roots: [] });
  assert.equal(res.path, null);
  assert.equal(res.source, "none");
  assert.equal(res.error!.code, "hub_cli_not_found");
});

test("getInstallPath: errors when CLI output unparseable", () => {
  const res = getInstallPath({
    runHubCli: () => ({ stdout: "   \n   ", stderr: "", exitCode: 0 }),
  });
  assert.equal(res.path, null);
  assert.equal(res.error!.code, "install_path_unparseable");
});

// ── setInstallPath (injectable runner) ─────────────────────────────

test("setInstallPath: success on exit 0", () => {
  const res = setInstallPath("/new/path", {
    runHubCli: (args) => {
      assert.deepEqual(args, ["install-path", "--set", "/new/path"]);
      return { stdout: "ok", stderr: "", exitCode: 0 };
    },
  });
  assert.equal(res.success, true);
  assert.equal(res.error, null);
});

test("setInstallPath: hub_cli_not_found when CLI missing", () => {
  const res = setInstallPath("/new/path", { runHubCli: fakeCliNotFound });
  assert.equal(res.success, false);
  assert.equal(res.error!.code, "hub_cli_not_found");
});

test("setInstallPath: failure on non-zero exit", () => {
  const res = setInstallPath("/new/path", {
    runHubCli: () => ({ stdout: "", stderr: "permission denied", exitCode: 2 }),
  });
  assert.equal(res.success, false);
  assert.equal(res.error!.code, "set_install_path_failed");
  assert.equal(res.error!.message, "permission denied");
});

// ── openInstallDeepLink (injectable opener) ────────────────────────

const fakeOpenerOk: UrlOpener = (url) => {
  assert.ok(url.startsWith("unityhub://"));
  return { opened: true, error: null };
};

const fakeOpenerFail: UrlOpener = () => ({ opened: false, error: "no handler" });

test("openInstallDeepLink: success when opener accepts", () => {
  const res = openInstallDeepLink("6000.5.0f1", "88b47c5e7076", { openUrl: fakeOpenerOk });
  assert.equal(res.opened, true);
  assert.equal(res.deepLink, "unityhub://6000.5.0f1/88b47c5e7076");
  assert.equal(res.error, null);
});

test("openInstallDeepLink: error when opener rejects", () => {
  const res = openInstallDeepLink("6000.5.0f1", undefined, { openUrl: fakeOpenerFail });
  assert.equal(res.opened, false);
  assert.equal(res.error!.code, "deep_link_open_failed");
  assert.ok(res.error!.message.includes("unityhub://6000.5.0f1"));
});

test("openInstallDeepLink: missing version -> missing_parameter", () => {
  const res = openInstallDeepLink("  ", undefined, { openUrl: fakeOpenerOk });
  assert.equal(res.opened, false);
  assert.equal(res.error!.code, "missing_parameter");
});

// ── fetchAvailableReleases (injectable fetcher) ────────────────────

test("fetchAvailableReleases: parses live archive HTML", async () => {
  const nodeJson = JSON.stringify({
    getUnityReleases: {
      edges: [
        {
          node: {
            version: "6000.4.12f1",
            releaseDate: "2026-06-20",
            unityHubDeepLink: "unityhub://6000.4.12f1/3ca267ce8005",
            stream: "SUPPORTED",
          },
        },
      ],
    },
  });
  const fetcher: ArchiveFetcher = async () =>
    makeArchiveHtml(nodeJson);
  const res = await fetchAvailableReleases({ fetcher });
  assert.equal(res.stale, false);
  assert.equal(res.entries.length, 1);
  assert.equal(res.entries[0].version, "6000.4.12f1");
});

test("fetchAvailableReleases: falls back to snapshot on fetch failure", async () => {
  const fetcher: ArchiveFetcher = async () => {
    throw new Error("network down");
  };
  const res = await fetchAvailableReleases({ fetcher });
  assert.equal(res.stale, true);
  assert.ok(res.entries.length >= 3);
});

test("fetchAvailableReleases: falls back to snapshot when parse returns null", async () => {
  const fetcher: ArchiveFetcher = async () => "<html>no payload here</html>";
  const res = await fetchAvailableReleases({ fetcher });
  assert.equal(res.stale, true);
  assert.ok(res.entries.length >= 3);
});
