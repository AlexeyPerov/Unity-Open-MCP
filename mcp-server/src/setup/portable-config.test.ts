import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

import {
  PORTABLE_CLIENT_MATRIX,
  catalogIdForSetupClient,
  portableEnv,
  portableProjectPathValue,
  portableServerArgs,
  portableSupportFor,
  renderWrapperScript,
  resolveWrapperTemplatePath,
  wrapperRelativePath,
} from "./portable-config.js";

const PIN = "unity-open-mcp@1.2.3";

test("every setup config writer has a portable strategy", () => {
  for (const setupClient of ["cursor", "claude", "opencode", "agents"]) {
    const support = portableSupportFor(catalogIdForSetupClient(setupClient));
    assert.ok(support, `${setupClient} is in the portable catalog`);
    assert.notEqual(support.strategy, "absolute");
  }
});

test("catalog ids are unique", () => {
  const ids = PORTABLE_CLIENT_MATRIX.map((client) => client.id);
  assert.equal(new Set(ids).size, ids.length);
});

test("interpolation clients declare a workspace variable", () => {
  for (const client of PORTABLE_CLIENT_MATRIX) {
    if (client.strategy === "interpolation") {
      assert.equal(client.workspaceVar, "${workspaceFolder}");
    }
  }
});

test("absolute-only clients are the global ones (no workspace config file)", () => {
  for (const client of PORTABLE_CLIENT_MATRIX) {
    if (client.strategy === "absolute") assert.equal(client.configPath, "");
  }
});

// --- args strategy ----------------------------------------------------------

test("args strategy emits --project-from-cwd for a unity-root layout", () => {
  assert.deepEqual(
    portableServerArgs("args", { layout: "unity-root", unitySubpath: "", npmPin: PIN }),
    ["-y", PIN, "--project-from-cwd"],
  );
});

test("args strategy appends --unity-subpath for a monorepo layout", () => {
  assert.deepEqual(
    portableServerArgs("args", {
      layout: "monorepo",
      unitySubpath: "Client",
      npmPin: PIN,
    }),
    ["-y", PIN, "--project-from-cwd", "--unity-subpath", "Client"],
  );
});

test("interpolation strategy keeps the plain npx args", () => {
  assert.deepEqual(
    portableServerArgs("interpolation", {
      layout: "monorepo",
      unitySubpath: "Client",
      npmPin: PIN,
    }),
    ["-y", PIN],
  );
});

// --- interpolation strategy -------------------------------------------------

test("Cursor gets ${workspaceFolder}/Client in a monorepo", () => {
  const support = portableSupportFor("cursor")!;
  const target = { layout: "monorepo" as const, unitySubpath: "Client", npmPin: PIN };
  assert.equal(portableProjectPathValue(support, target), "${workspaceFolder}/Client");
  assert.deepEqual(portableEnv(support, target), {
    UNITY_PROJECT_PATH: "${workspaceFolder}/Client",
  });
});

test("Cursor gets a bare ${workspaceFolder} when the workspace is the Unity root", () => {
  const support = portableSupportFor("cursor")!;
  const target = { layout: "unity-root" as const, unitySubpath: "", npmPin: PIN };
  assert.equal(portableProjectPathValue(support, target), "${workspaceFolder}");
});

test("args clients carry no UNITY_PROJECT_PATH at all", () => {
  const support = portableSupportFor("claude-code")!;
  const target = { layout: "monorepo" as const, unitySubpath: "Client", npmPin: PIN };
  assert.equal(portableProjectPathValue(support, target), undefined);
  assert.deepEqual(portableEnv(support, target), {});
});

// --- wrapper ----------------------------------------------------------------

test("the wrapper template ships with the package", () => {
  const body = readFileSync(resolveWrapperTemplatePath(), "utf8");
  assert.match(body, /^#!\/usr\/bin\/env bash/);
});

test("the rendered wrapper pins the running package version", () => {
  const script = renderWrapperScript({
    version: "9.8.7",
    layout: "monorepo",
    unitySubpath: "Client",
  });
  assert.match(script, /exec npx -y "unity-open-mcp@9\.8\.7"/);
  assert.ok(!script.includes("__UNITY_OPEN_MCP_VERSION__"));
});

test("the rendered wrapper carries no placeholders and no machine path", () => {
  const script = renderWrapperScript({
    version: "1.2.3",
    layout: "monorepo",
    unitySubpath: "Client",
  });
  assert.ok(!script.includes("__"), "every placeholder is substituted");
  assert.ok(!script.includes("/Users/"), "no machine-specific path");
});

test("the monorepo wrapper climbs out of scripts/mcp/ to the workspace root", () => {
  assert.equal(wrapperRelativePath("monorepo"), "scripts/mcp/unity-open-mcp.sh");
  const script = renderWrapperScript({
    version: "1.2.3",
    layout: "monorepo",
    unitySubpath: "Client",
  });
  assert.match(script, /\$\{script_dir\}\/\.\.\/\.\./);
  assert.match(script, /subpath="\$\{UNITY_SUBPATH-Client\}"/);
});

test("the unity-root wrapper climbs one level and has an empty subpath", () => {
  assert.equal(wrapperRelativePath("unity-root"), ".unity-open-mcp/mcp-wrapper.sh");
  const script = renderWrapperScript({
    version: "1.2.3",
    layout: "unity-root",
    unitySubpath: "",
  });
  assert.match(script, /\$\{script_dir\}\/\.\./);
  assert.match(script, /subpath="\$\{UNITY_SUBPATH-\}"/);
});

test("a Windows-style subpath is normalized to forward slashes", () => {
  const script = renderWrapperScript({
    version: "1.2.3",
    layout: "monorepo",
    unitySubpath: "games\\Client",
  });
  assert.match(script, /subpath="\$\{UNITY_SUBPATH-games\/Client\}"/);
  const support = portableSupportFor("cursor")!;
  assert.equal(
    portableProjectPathValue(support, {
      layout: "monorepo",
      unitySubpath: "games\\Client",
      npmPin: PIN,
    }),
    "${workspaceFolder}/games/Client",
  );
});
