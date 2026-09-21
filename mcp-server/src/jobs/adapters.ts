import { setTimeout as delay } from "node:timers/promises";
import type { LiveClient } from "../live-client.js";
import { validateSchema } from "../tool-contract.js";
import { runTests } from "../tools/run-tests.js";
import { JobManagerError, type JobOperation, type JobOwner } from "./job-manager.js";

type ClientFor = (owner: JobOwner) => LiveClient;
function invalid(errors: string[]): void {
  if (errors.length) throw new JobManagerError("invalid_arguments", errors.join("; "));
}
export function testRunOperation(client: ClientFor): JobOperation {
  return {
    // A test run has side effects even though the legacy tool is classified as a sense.
    mutating: true, cancellable: false,
    validate(args) {
      invalid(validateSchema(args, runTests.inputSchema));
      if (args.run_id !== undefined) throw new JobManagerError("invalid_arguments", "Jobs own run_id; use idempotency_key to retry.");
    },
    async run(args, context) {
      context.report({ phase: "starting test run" });
      const response = await client(context.owner).runTestsJob({ ...args, run_id: context.jobId, timeout_ms: args.timeout_ms ?? 600000 }, () => {
        context.lifecycle("connected", `Test results owned by run id ${context.jobId}`, true);
        context.report({ phase: "awaiting test result file" });
      });
      const text = response.content.find(item => item.type === "text");
      const result = text?.type === "text" ? JSON.parse(text.text) : {};
      if (result.error?.code === "test_results_timeout" || result.error?.code === "timeout") {
        context.lifecycle("disconnected", "Test result observation budget expired; run may still be executing.", false);
      }
      if (response.isError || result.status === "aborted" || result.summary?.failed > 0)
        return { state: "failed", error: { code: result.error?.code ?? "test_run_failed", message: result.error?.message ?? "Test run did not pass." }, result };
      if (result.status !== "completed" || result.runId !== context.jobId || !result.summary)
        throw new Error("Test result did not confirm terminal ownership.");
      context.report({ phase: "test results received" });
      return { state: "succeeded", result };
    },
  };
}

/** Bound the bridge status poll so a hung async command cannot own the project queue forever. */
export const PROJECT_JOB_DEFAULT_DEADLINE_MS = 30 * 60_000;
export interface ProjectJobPollOptions { deadlineMs?: number; pollIntervalMs?: number }

export function projectCommandOperation(id: string, cancellable: boolean, client: ClientFor, poll: ProjectJobPollOptions = {}): JobOperation {
  const deadlineMs = poll.deadlineMs ?? PROJECT_JOB_DEFAULT_DEADLINE_MS;
  const pollIntervalMs = poll.pollIntervalMs ?? 500;
  return {
    mutating: true, cancellable,
    validate(args) {
      const allowed = ["args", "schema_version", "paths_hint", "gate", "ignore_scene_dirty", "confirm_bypass"];
      invalid(Object.keys(args).filter(key => !allowed.includes(key)).map(key => `Unknown project job option: ${key}`));
    },
    async run(args, context) {
      const live = client(context.owner);
      context.report({ phase: "preflight" });
      const catalog = await live.projectCommands({ action: "describe", id });
      const command = catalog.command as Record<string, any> | undefined;
      if (!command?.available || !command.async || command.cancellable !== cancellable)
        return { state: "failed", error: { code: "command_unavailable", message: "Describe the command again; async contract is unavailable or changed." } };
      const errors = validateSchema(args.args ?? {}, command.inputSchema);
      if (errors.length) return { state: "failed", error: { code: "invalid_arguments", message: errors.join("; ") } };
      let response = await live.projectCommandJob({ action: "start", job_id: context.jobId,
        invocation: { ...args, action: "invoke", command_id: id, schema_version: args.schema_version ?? command.schemaVersion } });
      let cancelSent = false;
      let phase: string | undefined;
      let ownershipConfirmed = false;
      const startedAt = Date.now();
      while (true) {
        if (response.error || response.mutation?.success === false) {
          if (response.error?.code === "job_not_found") context.lifecycle("disconnected", "Bridge lost domain-local job ownership.", false);
          return { state: "failed", error: { code: response.error?.code ?? response.mutation?.error?.code ?? "command_rejected", message: response.error?.message ?? response.mutation?.error?.message ?? "Bridge rejected command." }, result: response };
        }
        if (response.job_id !== context.jobId) throw new Error("Bridge did not confirm job ownership");
        if (!ownershipConfirmed) {
          context.lifecycle("connected", `Bridge owns job ${context.jobId}`, true);
          ownershipConfirmed = true;
        }
        if (phase !== response.phase) { phase = response.phase; context.report({ phase: response.phase }); }
        if (response.state === "succeeded") return { state: "succeeded", result: response.result };
        if (response.state === "cancelled") return { state: "cancelled", result: response.result };
        if (response.state === "failed") return { state: "failed", error: { code: "command_failed", message: "See terminal gate/command result." }, result: response.result };
        if (Date.now() - startedAt >= deadlineMs) {
          // The bridge never reported a terminal state. Ask it to stop when the
          // contract allows, then release this job's execution slot as orphaned:
          // the remote outcome is unknown, so a blind retry is not safe.
          if (cancellable && !cancelSent) {
            cancelSent = true;
            try { await live.projectCommandJob({ action: "cancel", job_id: context.jobId }); } catch { /* best effort */ }
          }
          context.lifecycle("disconnected", `Bridge job reported no terminal state within ${deadlineMs} ms; outcome unknown.`, false);
          return { state: "failed", error: { code: "job_deadline_exceeded", message: `No terminal state within ${deadlineMs} ms.` }, result: response };
        }
        await delay(pollIntervalMs);
        const action = context.signal.aborted && !cancelSent ? "cancel" : "status";
        response = await live.projectCommandJob({ action, job_id: context.jobId });
        if (action === "cancel") cancelSent = true;
      }
    },
  };
}
