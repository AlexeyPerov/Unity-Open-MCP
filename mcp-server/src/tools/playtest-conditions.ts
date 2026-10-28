// Condition shapes shared by unity_open_mcp_playtest_wait (`condition`) and
// unity_senses_capture_series (`start_on`). A condition is an object with
// exactly one key: a leaf (log / ui / object / project_command / frames / ms)
// or `all` / `any` over leaves (one level deep). The bridge parses and
// enforces the one-key rule; the schema publishes the vocabulary.

export const OBJECT_STATES = ["exists", "active", "inactive", "destroyed"] as const;
export const LOG_LEVELS = ["any", "log", "warning", "error", "exception"] as const;
export const CONDITION_LEAVES = ["log", "ui", "object", "project_command", "frames", "ms"] as const;
export const CONDITION_MAX_CHILDREN = 16;

const leafProperties = {
  log: {
    type: "object",
    required: ["pattern"],
    description: "A console message logged after the wait started matches `pattern` (latched once seen; reports the frame it was logged in).",
    properties: {
      pattern: { type: "string", minLength: 1, description: ".NET regex, case-sensitive, matched against the message text." },
      level: {
        type: "string",
        enum: [...LOG_LEVELS],
        description: "any (default) | log | warning | error (Error, Exception, Assert) | exception.",
      },
    },
    additionalProperties: false,
  },
  ui: {
    type: "object",
    description:
      "A ui_snapshot query over the uGUI screen model matches (or, with matches:false, stops matching). " +
      "Needs text_contains and/or path. Requires com.unity.ugui.",
    properties: {
      text_contains: { type: "string", minLength: 1, description: "Node display text contains this (normalised, case-insensitive; a control's text is its label)." },
      path: { type: "string", minLength: 1, description: "Node hierarchy path or trailing path suffix ('Popup/Title')." },
      visible: { type: "boolean", description: "true (default) = count on-screen nodes only; false = count nodes that exist but are not visible." },
      matches: { type: "boolean", description: "true (default) = holds while at least one node matches; false = holds while none does (a popup closed)." },
    },
    additionalProperties: false,
  },
  object: {
    type: "object",
    description:
      "A GameObject (loaded scenes + DontDestroyOnLoad, inactive included) by path or object_id is in `state`. " +
      "Once found it is tracked: destroyed means that object was destroyed.",
    properties: {
      path: { type: "string", minLength: 1, description: "Hierarchy path (plain names, 'Root/Child') or a bare name (first match anywhere)." },
      object_id: { type: "string", description: "Instance id (e.g. a ui_snapshot node id). Alternative to path." },
      state: { type: "string", enum: [...OBJECT_STATES], description: "exists (default) | active (activeInHierarchy) | inactive | destroyed (absent)." },
    },
    additionalProperties: false,
  },
  project_command: {
    type: "object",
    required: ["id"],
    description:
      "The project's own predicate: a synchronous, read-only project command (unity_open_mcp_project_commands) " +
      "called at each poll holds when it returns JSON true — or, with `field`, an object whose `field` is true. " +
      "Mutating or async commands are refused (command_not_allowed); there is no arbitrary C# predicate.",
    properties: {
      id: { type: "string", minLength: 1, description: "Project command id (project.<owner>.<command>)." },
      args: { type: "object", description: "The command's typed arguments (validated against its schema once, at start)." },
      field: { type: "string", minLength: 1, description: "Top-level field of the command's JSON object result that must be true." },
    },
    additionalProperties: false,
  },
  frames: { type: "integer", minimum: 0, maximum: 1_000_000, description: "Holds after this many frames have run since the start." },
  ms: { type: "number", minimum: 0, maximum: 3_600_000, description: "Holds after this many ms (the call's `time` base) since the start." },
} as const;

const leafCondition = {
  type: "object",
  description: "Exactly one of log, ui, object, project_command, frames, ms.",
  properties: leafProperties,
  additionalProperties: false,
} as const;

/** The condition object schema (leaf, or all / any over leaves). */
export function conditionSchema(description: string) {
  return {
    type: "object",
    description,
    properties: {
      ...leafProperties,
      all: {
        type: "array",
        minItems: 1,
        maxItems: CONDITION_MAX_CHILDREN,
        items: leafCondition,
        description: "Holds when every leaf holds at the same poll (log leaves latch once seen).",
      },
      any: {
        type: "array",
        minItems: 1,
        maxItems: CONDITION_MAX_CHILDREN,
        items: leafCondition,
        description: "Holds when one leaf holds; the result names it (fired.path, e.g. condition.any[1]).",
      },
    },
    additionalProperties: false,
  } as const;
}

/** True when a condition object has exactly one key (the bridge enforces the same rule). */
export function isSingleKeyCondition(value: unknown): boolean {
  return !!value && typeof value === "object" && !Array.isArray(value) && Object.keys(value as object).length === 1;
}
