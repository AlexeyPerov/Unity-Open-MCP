import { test } from "node:test";
import assert from "node:assert/strict";

import {
  isManifestReady,
  isMcpSourceReady,
  isProjectReady,
  stepPassing,
} from "./readiness.ts";
import type {
  BridgeStatusKind,
  ManifestMergePlan,
  NodeProbe,
  ProjectState,
  ToolkitValidation,
} from "../../services/config.ts";

function readyDetection(overrides: Partial<ProjectState> = {}): ProjectState {
  return {
    path: "/p",
    name: "p",
    isValidUnityProject: true,
    unityVersion: "6000.0.0f1",
    meetsMinUnityVersion: true,
    meetsRecommendedUnityVersion: true,
    manifestPresent: true,
    bridgeInstalled: true,
    verifyInstalled: true,
    mcpConfigured: {
      cursor: false,
      claudeDesktop: false,
      opencodeGlobal: false,
      opencodeProject: false,
      zcodeGlobal: false,
      zcodeProject: false,
    },
    anySkillInstalled: false,
    manifestWritable: true,
    hasSpacesInPath: false,
    bridgeStatus: { kind: "notChecked" },
    unityDomainDeps: [],
    ...overrides,
  };
}

function okNode(): NodeProbe {
  return {
    ok: true,
    version: "v20.0.0",
    major: 20,
    requiredMajor: 18,
    error: null,
  };
}

// ---- isProjectReady ----

test("isProjectReady: false when no detection", () => {
  assert.equal(
    isProjectReady({ detection: null, nodeProbe: okNode() }),
    false,
  );
});

test("isProjectReady: false when not a valid Unity project", () => {
  assert.equal(
    isProjectReady({
      detection: readyDetection({ isValidUnityProject: false }),
      nodeProbe: okNode(),
    }),
    false,
  );
});

test("isProjectReady: false when below min Unity version", () => {
  assert.equal(
    isProjectReady({
      detection: readyDetection({ meetsMinUnityVersion: false }),
      nodeProbe: okNode(),
    }),
    false,
  );
});

test("isProjectReady: false when manifest not writable", () => {
  assert.equal(
    isProjectReady({
      detection: readyDetection({ manifestWritable: false }),
      nodeProbe: okNode(),
    }),
    false,
  );
});

test("isProjectReady: false when Node probe failed", () => {
  assert.equal(
    isProjectReady({
      detection: readyDetection(),
      nodeProbe: { ...okNode(), ok: false },
    }),
    false,
  );
});

test("isProjectReady: true when all gates pass", () => {
  assert.equal(
    isProjectReady({ detection: readyDetection(), nodeProbe: okNode() }),
    true,
  );
});

// ---- isMcpSourceReady ----

test("isMcpSourceReady: npx path is always ready", () => {
  assert.equal(
    isMcpSourceReady({
      useLocalCheckout: false,
      toolkitValidation: null,
    }),
    true,
  );
});

test("isMcpSourceReady: local checkout needs validated toolkit root", () => {
  assert.equal(
    isMcpSourceReady({
      useLocalCheckout: true,
      toolkitValidation: null,
    }),
    false,
  );
  assert.equal(
    isMcpSourceReady({
      useLocalCheckout: true,
      toolkitValidation: {
        ok: false,
        root: "/r",
        fingerprints: [],
        mcpDistMissing: false,
      } satisfies ToolkitValidation,
    }),
    false,
  );
  assert.equal(
    isMcpSourceReady({
      useLocalCheckout: true,
      toolkitValidation: {
        ok: true,
        root: "/r",
        fingerprints: [],
        mcpDistMissing: false,
      } satisfies ToolkitValidation,
    }),
    true,
  );
});

// ---- isManifestReady ----

function pkgEntry(id: string): {
  id: string;
  url: string;
  tag: string;
  packagePath: string;
} {
  return { id, url: "https://example/repo.git", tag: "v1", packagePath: "packages/x" };
}

function mergePlan(
  changes: ManifestMergePlan["changes"],
  overrides: Partial<ManifestMergePlan> = {},
): ManifestMergePlan {
  return {
    projectPath: "/p",
    changes,
    proposedDependencies: {},
    hasUpgrades: false,
    derivedUrls: {
      toolkitRoot: "/r",
      gitRemote: "https://example/repo.git",
      bridge: pkgEntry("com.alexeyperov.unity-open-mcp-bridge"),
      verify: pkgEntry("com.alexeyperov.unity-open-mcp-verify"),
      unityDomainDeps: [],
    },
    useLocalPackages: false,
    manifestUsesLocalPackages: false,
    manifestRead: {
      projectPath: "/p",
      present: true,
      readable: true,
      parseError: null,
      raw: null,
      dependencies: {},
    },
    ...overrides,
  };
}

test("isManifestReady: false when nothing selected", () => {
  assert.equal(
    isManifestReady({
      installBridge: false,
      installVerify: false,
      selectedUnityDomainDeps: new Set(),
      mergePlan: mergePlan([]),
    }),
    false,
  );
});

test("isManifestReady: false when no merge plan", () => {
  assert.equal(
    isManifestReady({
      installBridge: true,
      installVerify: true,
      selectedUnityDomainDeps: new Set(),
      mergePlan: null,
    }),
    false,
  );
});

test("isManifestReady: false on manifest parse error", () => {
  assert.equal(
    isManifestReady({
      installBridge: true,
      installVerify: true,
      selectedUnityDomainDeps: new Set(),
      mergePlan: mergePlan(
        [
          {
            id: "com.alexeyperov.unity-open-mcp-bridge",
            kind: "unchanged",
            before: "x",
            after: "x",
          },
        ],
        {
          manifestRead: {
            projectPath: "/p",
            present: true,
            readable: true,
            dependencies: {},
            parseError: "bad json",
            raw: null,
          },
        },
      ),
    }),
    false,
  );
});

test("isManifestReady: true when selected package unchanged", () => {
  assert.equal(
    isManifestReady({
      installBridge: true,
      installVerify: false,
      selectedUnityDomainDeps: new Set(),
      mergePlan: mergePlan([
        {
          id: "com.alexeyperov.unity-open-mcp-bridge",
          kind: "unchanged",
          before: "x",
          after: "x",
        },
      ]),
    }),
    true,
  );
});

test("isManifestReady: upgrade is allowed for navigation (ack gates action)", () => {
  assert.equal(
    isManifestReady({
      installBridge: true,
      installVerify: false,
      selectedUnityDomainDeps: new Set(),
      mergePlan: mergePlan(
        [
          {
            id: "com.alexeyperov.unity-open-mcp-bridge",
            kind: "upgrade",
            before: "old",
            after: "new",
          },
        ],
        { hasUpgrades: true },
      ),
    }),
    true,
  );
});

test("isManifestReady: Unity-dep-only selection can be ready when present", () => {
  assert.equal(
    isManifestReady({
      installBridge: false,
      installVerify: false,
      selectedUnityDomainDeps: new Set(["com.unity.ai.navigation"]),
      mergePlan: mergePlan([
        {
          id: "com.unity.ai.navigation",
          kind: "unchanged",
          before: "1.x",
          after: "1.x",
        },
      ]),
    }),
    true,
  );
});

// ---- stepPassing ----

const PASSING_INPUT = {
  detection: readyDetection(),
  nodeProbe: okNode(),
  useLocalCheckout: false,
  toolkitValidation: null,
  installBridge: true,
  installVerify: true,
  selectedUnityDomainDeps: new Set<string>(),
  mergePlan: null,
  step5BridgeStatus: { kind: "notChecked" } as BridgeStatusKind,
};

test("stepPassing: step0 never passes (no readiness check)", () => {
  assert.equal(stepPassing("step0", PASSING_INPUT), false);
});

test("stepPassing: done never passes", () => {
  assert.equal(stepPassing("done", PASSING_INPUT), false);
});

test("stepPassing: step3 green only when both packages installed", () => {
  assert.equal(stepPassing("step3", PASSING_INPUT), true);
  assert.equal(
    stepPassing("step3", {
      ...PASSING_INPUT,
      detection: readyDetection({ verifyInstalled: false }),
    }),
    false,
  );
});

test("stepPassing: step4 green when any MCP client configured", () => {
  assert.equal(
    stepPassing(
      "step4",
      {
        ...PASSING_INPUT,
        detection: readyDetection({
          mcpConfigured: {
            cursor: true,
            claudeDesktop: false,
            opencodeGlobal: false,
            opencodeProject: false,
            zcodeGlobal: false,
            zcodeProject: false,
          },
        }),
      },
    ),
    true,
  );
  assert.equal(stepPassing("step4", PASSING_INPUT), false);
});

test("stepPassing: step5 green when bridge ok", () => {
  assert.equal(
    stepPassing("step5", {
      ...PASSING_INPUT,
      step5BridgeStatus: {
        kind: "ok",
        connected: true,
        projectPath: "/p",
        compiling: false,
        isPlaying: false,
      },
    }),
    true,
  );
  assert.equal(stepPassing("step5", PASSING_INPUT), false);
});
