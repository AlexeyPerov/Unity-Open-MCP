# Hexa Sort — Failure Triage (v1)

Every failed step in a run gets exactly one taxonomy code. The auto-tagger
proposes the first label from observable signals; the operator confirms or
corrects it during triage. Codes are stable identifiers used by
[`rubric.md`](rubric.md) (critical gates) and the analysis-pass scorecard.

## Taxonomy

| Code                       | Meaning                                                                 |
| -------------------------- | ----------------------------------------------------------------------- |
| `transport_failure`        | Bridge / network / process / lifecycle is down or unstable.             |
| `tool_contract_mismatch`   | Tool call rejected for schema/argument reasons (wrong shape, bad args). |
| `tool_quality_issue`       | Tool ran (no error) but produced a wrong or low-quality result.         |
| `prompt_ambiguity`         | The instruction itself is too vague, conflicting, or led agents astray. |
| `agent_reasoning_failure`  | Model misunderstood the goal or planned/executed poorly.                |
| `project_state_issue`      | Unity state / import / recompile / environment drift caused the failure.|

## Auto-tag heuristics

Evaluate top-down; the first match wins. Signals come from `run-log.jsonl`
tool-call events and step-end events (see [`artifacts.md`](artifacts.md)).

### `transport_failure`
- `unity_open_mcp_ping` returns `connected: false`, times out, or errors.
- A tool call returns a transport/connection error (no tool-level response).
- A-00 preflight Overall is FAIL on the `connected` or `baseline compile` line.
- Bridge process exited / restarted mid-step (lifecycle event in the log).

### `tool_contract_mismatch`
- Tool call returns an error envelope whose code indicates invalid arguments,
  unknown property/parameter, wrong type, or "method not found".
- `find_members`/`type_schema`-style discovery was needed but skipped, and the
  call used a field/path that does not exist on the target type.
- `read_compile_errors` shows the agent called a tool with a name/shape the
  server does not accept.

### `tool_quality_issue`
- Tool returned success but the artifact it claims to have produced is missing
  or does not satisfy the step's `done_when` (e.g. `compile_check` says 0 errors
  but `read_console` filtered to errors is non-empty).
- A mutation tool reported success but the scene/object state afterwards
  contradicts the reported change (`scene_get_data` diff).

### `prompt_ambiguity`
- The step failed **and** the agent's behavior shows it invented rules, a board,
  or a win condition not present in [`canonical-spec.md`](canonical-spec.md).
- Two or more runs of the same step produce materially different interpretations.
- The prompt text itself contains an internal contradiction or an undefined term
  the agent had to guess.

### `agent_reasoning_failure`
- Step failed for none of the above reasons: tools and transport were healthy,
  the prompt was unambiguous, the project compiled — but the model still produced
  an incorrect result, skipped a required section, or planned poorly.
- Default fallback **only after** the four categories above are ruled out.

### `project_state_issue`
- `read_compile_errors` or `read_console` shows errors caused by a stale import,
  domain reload, missing asset reference, or recompile race — not by the agent's
  own code.
- A `dirty`/unsaved-scene guard refused an op (see bridge gate policy).
- Required package/module missing at run time (extension group `available: false`
  after being `true` in preflight).

## Auto-tag → operator-confirm flow

1. **Auto-tag** the failure using the heuristics above and write the proposed
   code into the step-end event's `failure_code` field (provisional).
2. **Operator review.** Read the failing step's `done_when` assertion, the tool
   envelopes, and `feedback.md`. Confirm the proposed code or reassign.
3. **Record** the confirmed code (and a one-line root-cause note) back into the
   step-end event and the analysis-pass scorecard.
4. **Gate impact.** If the confirmed code is `transport_failure` or
   `tool_contract_mismatch`, it trips a critical gate (see [`rubric.md`](rubric.md)
   §Critical gates); a `project_state_issue` tagged as unsafe also trips the
   mutation-safety gate.

## Ambiguity resolution

If two codes plausibly apply, prefer the one **closest to the root cause**:

- A tool that errored because the *bridge was down* → `transport_failure`, not
  `tool_contract_mismatch`.
- A tool that errored because the *agent sent bad args* → `tool_contract_mismatch`,
  not `agent_reasoning_failure`.
- A result that was wrong because the *project was in a bad state* →
  `project_state_issue`, not `tool_quality_issue`.

When still unclear after review, tag `agent_reasoning_failure` and note the
ambiguity; this is the catch-all and signals a prompt/process to refine.
