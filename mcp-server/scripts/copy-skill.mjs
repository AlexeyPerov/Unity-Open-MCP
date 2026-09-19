import { copyFile, mkdir } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const packageRoot = join(dirname(fileURLToPath(import.meta.url)), "..");
const source = join(packageRoot, "..", "skills", "unity-open-mcp", "SKILL.md");
const destination = join(packageRoot, "dist", "skill", "SKILL.md");

await mkdir(dirname(destination), { recursive: true });
await copyFile(source, destination);
