// Tests for routers.ts — resolveEnv + buildRouterStack.
//
// resolveEnv is the precedence authority for project-path / port / auth-token.
// buildRouterStack is the shared wiring the stdio server and the CLI both use,
// so "CLI and server see the same resolved config" holds by construction — we
// assert the wiring parity directly.
//
// resolveEnv reads process.env and (via instance-discovery) the on-disk lock;
// these tests stub process.env and rely on the deterministic hash fallback
// (no lock file present for the synthetic paths used here).

import { test } from "node:test";
import assert from "node:assert/strict";
import {
  resolveEnv,
  ResolveEnvError,
  buildRouterStack,
  type ResolvedEnv,
  type RouterStack,
} from "./routers.js";
import {
  PROJECT_PATH_ENV_VAR,
  PORT_ENV_VAR,
} from "./constants.js";
import { computePort } from "./instance-discovery.js";

// Save/restore process.env around every test that mutates it. The node:test
// runner shares one process; leaking UNITY_* env vars would poison later tests.
function withEnv(
  envPatch: Record<string, string | undefined>,
  fn: () => void,
): void {
  const saved = new Map<string, string | undefined>();
  for (const [k, v] of Object.entries(envPatch)) {
    saved.set(k, process.env[k]);
    if (v === undefined) delete process.env[k];
    else process.env[k] = v;
  }
  try {
    fn();
  } finally {
    for (const [k, v] of saved) {
      if (v === undefined) delete process.env[k];
      else process.env[k] = v;
    }
  }
}

// A synthetic project path with no instance lock on disk. resolvePort falls
// through to the deterministic hash for it.
const SYNTH_PROJECT = "/tmp/unity-open-mcp-test-project-no-lock";

// ---------------------------------------------------------------------------
// resolveEnv — project path resolution
// ---------------------------------------------------------------------------

test("resolveEnv throws ResolveEnvError when no project path is set", () => {
  withEnv({ [PROJECT_PATH_ENV_VAR]: undefined, [PORT_ENV_VAR]: undefined }, () => {
    assert.throws(
      () => resolveEnv(),
      (err: unknown) => {
        assert.ok(err instanceof ResolveEnvError, "should be ResolveEnvError");
        assert.match(
          (err as Error).message,
          new RegExp(PROJECT_PATH_ENV_VAR),
          "message must name the missing env var",
        );
        return true;
      },
    );
  });
});

test("resolveEnv accepts a project-path override (CLI flag)", () => {
  withEnv({ [PROJECT_PATH_ENV_VAR]: undefined, [PORT_ENV_VAR]: undefined }, () => {
    const env = resolveEnv("/explicit/cli/path");
    assert.equal(env.projectPath, "/explicit/cli/path");
  });
});

test("resolveEnv reads project path from the env var when no override is given", () => {
  withEnv(
    { [PROJECT_PATH_ENV_VAR]: "/from/env", [PORT_ENV_VAR]: undefined },
    () => {
      const env = resolveEnv();
      assert.equal(env.projectPath, "/from/env");
    },
  );
});

test("resolveEnv: explicit override wins over the env var", () => {
  withEnv(
    { [PROJECT_PATH_ENV_VAR]: "/from/env", [PORT_ENV_VAR]: undefined },
    () => {
      const env = resolveEnv("/override/wins");
      assert.equal(env.projectPath, "/override/wins");
    },
  );
});

// ---------------------------------------------------------------------------
// resolveEnv — port precedence (override > env var > hash fallback)
// ---------------------------------------------------------------------------

test("resolveEnv: port override (CLI) wins over env var and hash", () => {
  withEnv(
    { [PROJECT_PATH_ENV_VAR]: SYNTH_PROJECT, [PORT_ENV_VAR]: "22222" },
    () => {
      const env = resolveEnv(SYNTH_PROJECT, 33333);
      assert.equal(env.port, 33333);
      // envPort threads the override through so LiveClient respects it.
      assert.equal(env.envPort, 33333);
    },
  );
});

test("resolveEnv: port env var used when no CLI override", () => {
  withEnv(
    { [PROJECT_PATH_ENV_VAR]: SYNTH_PROJECT, [PORT_ENV_VAR]: "22222" },
    () => {
      const env = resolveEnv(SYNTH_PROJECT);
      assert.equal(env.port, 22222);
      assert.equal(env.envPort, 22222);
    },
  );
});

test("resolveEnv: deterministic hash fallback when no port override or env var", () => {
  withEnv(
    { [PROJECT_PATH_ENV_VAR]: SYNTH_PROJECT, [PORT_ENV_VAR]: undefined },
    () => {
      const env = resolveEnv(SYNTH_PROJECT);
      assert.equal(env.port, computePort(SYNTH_PROJECT));
      // No explicit port anywhere → envPort is undefined.
      assert.equal(env.envPort, undefined);
    },
  );
});

test("resolveEnv: invalid port env var (non-integer) falls through to hash", () => {
  withEnv(
    { [PROJECT_PATH_ENV_VAR]: SYNTH_PROJECT, [PORT_ENV_VAR]: "not-a-port" },
    () => {
      const env = resolveEnv(SYNTH_PROJECT);
      assert.equal(env.port, computePort(SYNTH_PROJECT));
      // Invalid env port → treated as undefined.
      assert.equal(env.envPort, undefined);
    },
  );
});

// ---------------------------------------------------------------------------
// resolveEnv — auth-token precedence
// ---------------------------------------------------------------------------

test("resolveEnv: explicit port override ⇒ no auth token discoverable", () => {
  // With a port override, resolveAuthToken skips the lock read (no lock for
  // the synthetic path anyway) and returns undefined.
  withEnv(
    { [PROJECT_PATH_ENV_VAR]: SYNTH_PROJECT, [PORT_ENV_VAR]: undefined },
    () => {
      const env = resolveEnv(SYNTH_PROJECT, 33333);
      assert.equal(env.authToken, undefined);
    },
  );
});

test("resolveEnv: no live lock ⇒ auth token undefined (hash fallback path)", () => {
  withEnv(
    { [PROJECT_PATH_ENV_VAR]: SYNTH_PROJECT, [PORT_ENV_VAR]: undefined },
    () => {
      const env = resolveEnv(SYNTH_PROJECT);
      assert.equal(env.authToken, undefined);
    },
  );
});

test("resolveEnv: port env var also suppresses auth-token discovery", () => {
  // An env-var port (UNITY_OPEN_MCP_BRIDGE_PORT) is threaded as envPort, which
  // resolveAuthToken treats the same as an explicit override → undefined.
  withEnv(
    { [PROJECT_PATH_ENV_VAR]: SYNTH_PROJECT, [PORT_ENV_VAR]: "22222" },
    () => {
      const env = resolveEnv(SYNTH_PROJECT);
      assert.equal(env.authToken, undefined);
    },
  );
});

test("ResolvedEnv shape: all four fields present", () => {
  withEnv(
    { [PROJECT_PATH_ENV_VAR]: SYNTH_PROJECT, [PORT_ENV_VAR]: undefined },
    () => {
      const env = resolveEnv(SYNTH_PROJECT);
      assert.ok(typeof env.projectPath === "string");
      assert.ok(typeof env.port === "number");
      // authToken is string | undefined; envPort is number | undefined.
      assert.ok(env.authToken === undefined || typeof env.authToken === "string");
      assert.ok(env.envPort === undefined || typeof env.envPort === "number");
    },
  );
});

// ---------------------------------------------------------------------------
// buildRouterStack — wiring parity (CLI and server share this path)
// ---------------------------------------------------------------------------

test("buildRouterStack returns a stack with all documented collaborators", () => {
  withEnv(
    { [PROJECT_PATH_ENV_VAR]: SYNTH_PROJECT, [PORT_ENV_VAR]: undefined },
    () => {
      const env = resolveEnv(SYNTH_PROJECT, 33333);
      const stack: RouterStack = buildRouterStack(env);
      // Every field the interface promises is present and non-null.
      for (const key of [
        "live",
        "batch",
        "router",
        "pingCache",
        "resourceRouter",
        "eventStream",
        "sessionState",
      ] as const) {
        assert.ok(stack[key], `stack.${key} must be present`);
      }
    },
  );
});

test("buildRouterStack threads resolved env (port, projectPath, authToken) onto the stack", () => {
  withEnv(
    { [PROJECT_PATH_ENV_VAR]: SYNTH_PROJECT, [PORT_ENV_VAR]: undefined },
    () => {
      const env = resolveEnv(SYNTH_PROJECT, 33333);
      const stack = buildRouterStack(env);
      assert.equal(stack.port, env.port);
      assert.equal(stack.projectPath, env.projectPath);
      assert.equal(stack.authToken, env.authToken);
    },
  );
});

test("buildRouterStack creates a fresh ToolSessionState (default-active groups)", () => {
  withEnv(
    { [PROJECT_PATH_ENV_VAR]: SYNTH_PROJECT, [PORT_ENV_VAR]: undefined },
    () => {
      const env = resolveEnv(SYNTH_PROJECT, 33333);
      const stack = buildRouterStack(env);
      // Two stacks built independently have independent session state.
      const stack2 = buildRouterStack(env);
      assert.notEqual(stack.sessionState, stack2.sessionState);
      // Activating a group on one does not affect the other.
      stack.sessionState.activate("navigation");
      assert.equal(stack.sessionState.isGroupActive("navigation"), true);
      assert.equal(stack2.sessionState.isGroupActive("navigation"), false);
    },
  );
});

test("buildRouterStack from the same ResolvedEnv twice is equivalent in wiring (parity)", () => {
  // The core CLI/server parity claim: both code paths call buildRouterStack
  // with the same resolved env, so they produce structurally equivalent
  // stacks. We assert the shared env-derived fields match across two builds.
  withEnv(
    { [PROJECT_PATH_ENV_VAR]: SYNTH_PROJECT, [PORT_ENV_VAR]: undefined },
    () => {
      const env: ResolvedEnv = resolveEnv(SYNTH_PROJECT, 33333);
      const a = buildRouterStack(env);
      const b = buildRouterStack(env);
      assert.equal(a.port, b.port);
      assert.equal(a.projectPath, b.projectPath);
      assert.equal(a.authToken, b.authToken);
    },
  );
});
