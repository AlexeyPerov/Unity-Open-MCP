import { makeTool } from "./schema-fragments.js";
import { JOB_STATES } from "../jobs/job-manager.js";

export const jobs = makeTool("unity_open_mcp_jobs",
  "Observe session-owned asynchronous jobs: start, status, bounded wait, cooperative cancel, and paged list. Supported targets: unity_senses_run_tests and available async project commands. Other tools are unsupported. Test args are ordinary filters without run_id; project args contain nested args plus scope/gate options. Mutating starts require idempotency_key. Records expire 30 minutes after completion; server restart loses all records. Never blindly retry an orphaned job.", {
    required: ["action"],
    properties: {
      action: { type: "string", enum: ["start", "status", "wait", "cancel", "list"] },
      tool_or_command: { type: "string", minLength: 1, maxLength: 256 },
      args: { type: "object", description: "Adapter arguments, including scope/gate intent where applicable." },
      idempotency_key: { type: "string", minLength: 1, maxLength: 256 },
      job_id: { type: "string", minLength: 1, maxLength: 128 },
      timeout_ms: { type: "integer", minimum: 0, maximum: 30000 },
      state: { type: "string", enum: [...JOB_STATES] },
      operation: { type: "string", maxLength: 256 },
      cursor: { type: "string", maxLength: 4096 },
      limit: { type: "integer", minimum: 1, maximum: 100 },
    },
  });
