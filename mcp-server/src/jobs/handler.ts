import { JobManager, JobManagerError, type JobOwner, type JobState } from "./job-manager.js";

/** Action-specific validation also applies when invoked outside the stdio server. */
export async function handleJobs(manager: JobManager, owner: JobOwner, args: Record<string, unknown>): Promise<unknown> {
  const allowed: Record<string, string[]> = {
    start: ["tool_or_command", "args", "idempotency_key"], status: ["job_id"],
    wait: ["job_id", "timeout_ms"], cancel: ["job_id"], list: ["state", "operation", "cursor", "limit"],
  };
  const action = String(args.action);
  if (!Object.hasOwn(allowed, action) || Object.keys(args).some(key => key !== "action" && !allowed[action].includes(key)))
    throw new JobManagerError("invalid_arguments", "Use only arguments belonging to the selected jobs action.");
  if (action === "start") {
    if (typeof args.tool_or_command !== "string" || !args.tool_or_command.trim()) throw new JobManagerError("invalid_arguments", "start requires tool_or_command.");
    return manager.start(owner, args.tool_or_command, (args.args ?? {}) as Record<string, unknown>, args.idempotency_key as string | undefined);
  }
  if (action === "list") return manager.list(owner, {
    ...(args.state === undefined ? {} : { state: args.state as JobState }),
    ...(args.operation === undefined ? {} : { operation: args.operation as string }),
  }, args.cursor as string | undefined, args.limit as number | undefined);
  if (typeof args.job_id !== "string" || !args.job_id.trim()) throw new JobManagerError("invalid_arguments", `${action} requires job_id.`);
  if (action === "status") return manager.status(owner, args.job_id);
  if (action === "cancel") return manager.cancel(owner, args.job_id);
  return manager.wait(owner, args.job_id, args.timeout_ms as number | undefined);
}
