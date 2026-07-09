import type {
  GenerateSkillError,
  LaunchForVerifyError,
  McpConfigError,
  SkillCopyError,
} from "../../services/config.ts";

/** Coerce a thrown value into the {@link McpConfigError} shape. */
export function toMcpConfigError(e: unknown): McpConfigError {
  if (e && typeof e === "object" && "kind" in e && "message" in e) {
    return e as McpConfigError;
  }
  return {
    kind: "unknown",
    message: e instanceof Error ? e.message : String(e),
  };
}

/** Coerce a thrown value into the {@link SkillCopyError} shape. */
export function toSkillCopyError(e: unknown): SkillCopyError {
  if (e && typeof e === "object" && "kind" in e && "message" in e) {
    return e as SkillCopyError;
  }
  return {
    kind: "unknown",
    message: e instanceof Error ? e.message : String(e),
  };
}

/** Coerce a thrown value into the {@link GenerateSkillError} shape. */
export function toGenerateSkillError(e: unknown): GenerateSkillError {
  if (e && typeof e === "object" && "kind" in e && "message" in e) {
    return e as GenerateSkillError;
  }
  return {
    kind: "unknown",
    message: e instanceof Error ? e.message : String(e),
  };
}

/** Human-readable description of an MCP config error. */
export function describeMcpConfigError(err: McpConfigError): string {
  switch (err.kind) {
    case "mcpPathInvalid":
      return `MCP server entry point does not exist on disk. Run \`npm run build\` in the toolkit's mcp-server/ folder.`;
    case "homeMissing":
      return "Cannot resolve the home directory for a global MCP config target.";
    case "noFileTarget":
      return "This client does not back a writable config file.";
    case "invalidJson":
      return `Existing config is not valid JSON: ${err.message}`;
    case "readFailed":
      return `Cannot read existing config: ${err.message}`;
    case "writeFailed":
      return `Failed to write config: ${err.message}. Check folder permissions.`;
    case "backupFailed":
      return `Cannot create backup: ${err.message}`;
    default:
      return `${err.kind}: ${err.message}`;
  }
}

/** Human-readable description of a skill-copy error. */
export function describeSkillCopyError(err: SkillCopyError): string {
  switch (err.kind) {
    case "sourceMissing":
      return `Toolkit source skill file is missing. Run the wizard with a valid toolkit root.`;
    case "manifestMissing":
    case "manifestInvalid":
      return `Skill client-paths manifest problem: ${err.message}. Make sure your toolkit root is the unity-open-mcp monorepo checkout.`;
    case "writeFailed":
      return `Failed to copy skill: ${err.message}. Check folder permissions.`;
    case "backupFailed":
      return `Cannot create backup: ${err.message}`;
    case "notAUnityProject":
      return `Project path is not a directory.`;
    case "overwriteNotConfirmed":
      return err.message;
    default:
      return `${err.kind}: ${err.message}`;
  }
}

/** Human-readable description of a generate-skill error. */
export function describeGenerateSkillError(err: GenerateSkillError): string {
  switch (err.kind) {
    case "notAUnityProject":
      return `Project path is not a directory.`;
    case "mcpPathInvalid":
      return `MCP server entry not found. Run \`npm run build\` in the toolkit's mcp-server/ folder. (${err.message})`;
    case "noClientTargets":
      return `No skill folder is mapped for the selected client. Pick a different client or use Manual.`;
    case "spawnFailed":
      return `Failed to run node: ${err.message}. Check Node.js is installed and on PATH.`;
    case "cliError":
      return `Skill generator CLI failed: ${err.message}`;
    case "toolError":
      return `Skill generation tool error: ${err.message}`;
    case "manifestInvalid":
      return `Skill client-paths manifest problem: ${err.message}.`;
    default:
      return `${err.kind}: ${err.message}`;
  }
}

/** Human-readable description of a launch-for-verify error. */
export function describeLaunchForVerifyError(e: unknown): string {
  if (
    e &&
    typeof e === "object" &&
    "kind" in e &&
    typeof (e as LaunchForVerifyError).kind === "string"
  ) {
    const err = e as LaunchForVerifyError;
    switch (err.kind) {
      case "projectNotFound":
        return `Project ${err.projectId} is no longer in the Hub project list. Reopen the wizard.`;
      case "pathInvalid":
        return `Project path is invalid: ${err.path}.`;
      case "versionMissing":
        return `Unity version is unknown. Open the project once in the Editor to refresh the version, then retry.`;
      case "installNotFound":
        return `Unity ${err.version} is not installed on this machine. Open the Installs tab to add it.`;
      case "launchFailed":
        return `Failed to launch Unity: ${err.message}. Open the launch log from the Status drawer.`;
      case "portInvalid":
        return `Bridge port ${err.port} is not a valid TCP port. Pick a port in 1..65535.`;
      default:
        return `${(e as { kind?: string }).kind ?? "unknown"}: ${
          (e as { message?: string }).message ?? "unknown error"
        }`;
    }
  }
  return e instanceof Error ? e.message : String(e);
}
