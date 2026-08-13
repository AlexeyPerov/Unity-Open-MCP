import test from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, relative } from "node:path";

import {
  nearestAsmdef,
  countProjectAsmdefs,
  countAssembliesWithErrors,
} from "./asmdef-discovery.js";

interface TestProject {
  root: string;
  pkgRoot: string;
}

/** Build a throwaway project tree under the OS temp dir. Returns the project
 *  root and the external file:-package root (outside the project). */
function makeProject(): TestProject {
  const root = mkdtempSync(join(tmpdir(), "asmdef-test-"));
  // Assets/Foo/Foo.asmdef + source + a sub-source (same assembly).
  mkdirSync(join(root, "Assets", "Foo"), { recursive: true });
  writeFileSync(join(root, "Assets", "Foo", "Foo.asmdef"), '{"name":"Foo"}');
  writeFileSync(join(root, "Assets", "Foo", "Foo.cs"), "// src");
  mkdirSync(join(root, "Assets", "Foo", "Sub"), { recursive: true });
  writeFileSync(join(root, "Assets", "Foo", "Sub", "Bar.cs"), "// src");
  // Assets/Bar/Bar.asmdef — a second assembly.
  mkdirSync(join(root, "Assets", "Bar"), { recursive: true });
  writeFileSync(join(root, "Assets", "Bar", "Bar.asmdef"), '{"name":"Bar"}');
  writeFileSync(join(root, "Assets", "Bar", "Bar.cs"), "// src");
  // An external file: package (sibling temp dir), referenced from manifest.
  const pkgRoot = mkdtempSync(join(tmpdir(), "asmdef-pkg-"));
  mkdirSync(join(pkgRoot, "Editor"), { recursive: true });
  writeFileSync(join(pkgRoot, "Editor", "Pkg.asmdef"), '{"name":"Pkg"}');
  writeFileSync(join(pkgRoot, "Editor", "Pkg.cs"), "// src");
  // Unity resolves file: paths relative to the Packages/ directory.
  mkdirSync(join(root, "Packages"), { recursive: true });
  const relFromPackages = relative(join(root, "Packages"), pkgRoot);
  writeFileSync(
    join(root, "Packages", "manifest.json"),
    JSON.stringify({
      dependencies: { "com.example.pkg": "file:" + relFromPackages },
    }),
  );
  return { root, pkgRoot };
}

function cleanup(p: TestProject): void {
  rmSync(p.root, { recursive: true, force: true });
  rmSync(p.pkgRoot, { recursive: true, force: true });
}

test("nearestAsmdef walks up to the owning assembly", () => {
  const p = makeProject();
  try {
    // A sub-source file resolves to its directory's asmdef.
    assert.equal(
      nearestAsmdef(join(p.root, "Assets", "Foo", "Sub", "Bar.cs")),
      join(p.root, "Assets", "Foo", "Foo.asmdef"),
    );
    // A file directly in the assembly dir.
    assert.equal(
      nearestAsmdef(join(p.root, "Assets", "Bar", "Bar.cs")),
      join(p.root, "Assets", "Bar", "Bar.asmdef"),
    );
    // null for empty input.
    assert.equal(nearestAsmdef(""), null);
  } finally {
    cleanup(p);
  }
});

test("countProjectAsmdef counts Assets + file: packages and skips Library", () => {
  const p = makeProject();
  try {
    // Add a Library asmdef that must be EXCLUDED (precompiled registry cache).
    mkdirSync(join(p.root, "Library", "PackageCache", "somepkg"), {
      recursive: true,
    });
    writeFileSync(
      join(p.root, "Library", "PackageCache", "somepkg", "Ignored.asmdef"),
      '{"name":"Ignored"}',
    );

    const result = countProjectAsmdefs(p.root);
    // Foo + Bar (Assets) + Pkg (file: package) = 3. Library/Ignored excluded.
    assert.equal(result.count, 3, `got ${result.count}: ${JSON.stringify(result.sample)}`);
    assert.ok(result.sample.length <= 8);
  } finally {
    cleanup(p);
  }
});

test("countProjectAsmdef returns 0 for null/missing project path", () => {
  assert.equal(countProjectAsmdefs(null).count, 0);
  assert.equal(countProjectAsmdefs(undefined).count, 0);
  assert.equal(countProjectAsmdefs(join(tmpdir(), "does-not-exist-xyz")).count, 0);
});

test("countAssembliesWithErrors dedupes by owning assembly", () => {
  const p = makeProject();
  try {
    // Two errors in the Foo assembly + one in Bar → 2 distinct assemblies.
    const files = [
      join(p.root, "Assets", "Foo", "Foo.cs"),
      join(p.root, "Assets", "Foo", "Sub", "Bar.cs"), // same Foo asmdef
      join(p.root, "Assets", "Bar", "Bar.cs"), // Bar asmdef
      "", // unparseable — dropped
    ];
    assert.equal(countAssembliesWithErrors(files), 2);
    assert.equal(countAssembliesWithErrors([]), 0);
  } finally {
    cleanup(p);
  }
});

// read_compile_errors' partial-compile signal passes compiler-error file paths,
// which are PROJECT-RELATIVE ("Assets/Foo/Foo.cs"). Without the projectRoot
// argument those resolved against process.cwd() and mapped to nothing — the
// assembliesWithErrors field was silently always null in production. These pin
// the projectRoot anchoring at both the single-file and the count level.
test("nearestAsmdef anchors a relative path to projectRoot", () => {
  const p = makeProject();
  try {
    assert.equal(
      nearestAsmdef("Assets/Foo/Sub/Bar.cs", p.root),
      join(p.root, "Assets", "Foo", "Foo.asmdef"),
    );
    // Absolute paths ignore projectRoot and still resolve.
    assert.equal(
      nearestAsmdef(join(p.root, "Assets", "Bar", "Bar.cs"), p.root),
      join(p.root, "Assets", "Bar", "Bar.asmdef"),
    );
  } finally {
    cleanup(p);
  }
});

test("countAssembliesWithErrors anchors relative error paths to projectRoot", () => {
  const p = makeProject();
  try {
    // Project-relative paths (the wire form compiler errors carry) must resolve
    // against projectRoot: Foo + Foo/Sub dedupe to Foo, plus Bar = 2.
    const relFiles = ["Assets/Foo/Foo.cs", "Assets/Foo/Sub/Bar.cs", "Assets/Bar/Bar.cs"];
    assert.equal(countAssembliesWithErrors(relFiles, p.root), 2);
  } finally {
    cleanup(p);
  }
});

test("countProjectAsmdefs memoizes per project path", () => {
  const p = makeProject();
  try {
    // The walk stats the whole Assets/ tree synchronously; the memo returns the
    // same result object for the same project path so repeat calls are free.
    const a = countProjectAsmdefs(p.root);
    const b = countProjectAsmdefs(p.root);
    assert.equal(a.count, b.count);
    assert.equal(a, b, "memoized result should be the same object reference");
  } finally {
    cleanup(p);
  }
});
