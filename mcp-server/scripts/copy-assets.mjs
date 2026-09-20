// Copy non-TypeScript assets the published package ships alongside dist/:
//   - the canonical core skill (byte-for-byte, for `setup`)
//   - the MCP wrapper template (for `setup --wrapper`)
import { copyFile, mkdir } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const packageRoot = join(dirname(fileURLToPath(import.meta.url)), "..");

const assets = [
  {
    source: join(packageRoot, "..", "skills", "unity-open-mcp", "SKILL.md"),
    destination: join(packageRoot, "dist", "skill", "SKILL.md"),
  },
  {
    source: join(packageRoot, "templates", "mcp-wrapper.sh"),
    destination: join(packageRoot, "dist", "templates", "mcp-wrapper.sh"),
  },
];

for (const asset of assets) {
  await mkdir(dirname(asset.destination), { recursive: true });
  await copyFile(asset.source, asset.destination);
}
