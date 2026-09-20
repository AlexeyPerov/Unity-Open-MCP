import { cp, stat } from "node:fs/promises";
import { dirname, join } from "node:path";

/** All clients receive the same reference tree alongside the canonical entrypoint. */
export async function copySkillReferences(sourceSkill: string, targetSkill: string): Promise<void> {
  const source = join(dirname(sourceSkill), "references");
  try { await stat(source); } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return;
    throw error;
  }
  await cp(source, join(dirname(targetSkill), "references"), { recursive: true });
}
