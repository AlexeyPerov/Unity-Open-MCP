import test from "node:test";
import assert from "node:assert/strict";

import {
  extractCompilerErrors,
  extractStructuredCompilerErrors,
  MAX_COMPILER_ERRORS,
} from "./compiler-errors.js";

const SAMPLE_LINE =
  "Assets/Foo/Bar.cs(75,27): error CS1061: 'SceneSetup' does not contain a definition for 'isDirty'";

// ---------------------------------------------------------------------------
// extractStructuredCompilerErrors
// ---------------------------------------------------------------------------

test("extractStructuredCompilerErrors parses file/line/code/message from a diagnostic line", () => {
  const out = [
    "Some preamble line",
    SAMPLE_LINE,
    "Assets/Other.cs(10,2): error CS0103: The name 'Bar' does not exist in the current context",
  ].join("\n");

  const errors = extractStructuredCompilerErrors(out);
  assert.equal(errors.length, 2);

  assert.deepEqual(errors[0], {
    raw: SAMPLE_LINE,
    file: "Assets/Foo/Bar.cs",
    line: 75,
    code: "CS1061",
    message:
      "'SceneSetup' does not contain a definition for 'isDirty'",
  });

  assert.equal(errors[1].file, "Assets/Other.cs");
  assert.equal(errors[1].line, 10);
  assert.equal(errors[1].code, "CS0103");
});

test("extractStructuredCompilerErrors handles a line range locator path(line,line)", () => {
  // Unity sometimes emits a range instead of (line,col).
  const out = "Assets/X.cs(10,5,12,7): error CS1002: ; expected";
  const errors = extractStructuredCompilerErrors(out);
  assert.equal(errors.length, 1);
  assert.equal(errors[0].line, 10, "first number in the locator is the line");
  assert.equal(errors[0].code, "CS1002");
});

test("extractStructuredCompilerErrors dedupes identical raw lines", () => {
  const out = `${SAMPLE_LINE}\n${SAMPLE_LINE}\n${SAMPLE_LINE}`;
  const errors = extractStructuredCompilerErrors(out);
  assert.equal(errors.length, 1);
});

test("extractStructuredCompilerErrors captures indented error lines", () => {
  const out = "  error CS1002: ; expected (indented, no locator)";
  const errors = extractStructuredCompilerErrors(out);
  // No asset locator → the structured regex requires a `path(...)` prefix,
  // so this line does NOT match the structured extractor. It is captured by
  // the legacy tail-only extractor instead (tested below).
  assert.equal(errors.length, 0);
});

test("extractStructuredCompilerErrors returns [] for empty / no-error input", () => {
  assert.deepEqual(extractStructuredCompilerErrors(""), []);
  assert.deepEqual(extractStructuredCompilerErrors("all good, no errors"), []);
});

test("extractStructuredCompilerErrors caps at MAX_COMPILER_ERRORS", () => {
  const one = "Assets/F.cs(1,1): error CS0000: x\n";
  const out = one.repeat(MAX_COMPILER_ERRORS + 50);
  const errors = extractStructuredCompilerErrors(out);
  // All lines are identical (same file/line/code/message) → deduped to 1.
  assert.equal(errors.length, 1);

  // Distinct lines cap at the bound.
  const distinct = Array.from(
    { length: MAX_COMPILER_ERRORS + 50 },
    (_, i) => `Assets/F${i}.cs(${i + 1},1): error CS0000: msg ${i}`,
  ).join("\n");
  const capped = extractStructuredCompilerErrors(distinct);
  assert.equal(capped.length, MAX_COMPILER_ERRORS);
});

// ---------------------------------------------------------------------------
// extractCompilerErrors (legacy tail-only extractor)
// ---------------------------------------------------------------------------

test("extractCompilerErrors pulls CSxxxx lines from raw output", () => {
  const out = [
    "Some preamble line",
    "Assets/Broken.cs(10,14): error CS0246: The type or namespace name 'Foo' could not be found",
    "Assets/Broken.cs(20,2): error CS0103: The name 'Bar' does not exist in the current context",
    "a non-error line",
    "  error CS1002: ; expected (indented variant)",
  ].join("\n");
  const errors = extractCompilerErrors(out);
  assert.equal(errors.length, 3);
  assert.ok(errors[0].includes("CS0246"));
  assert.ok(errors[1].includes("CS0103"));
  assert.ok(errors[2].includes("CS1002"), "indented error lines are captured");
});

test("extractCompilerErrors returns [] when no CS errors present", () => {
  assert.deepEqual(extractCompilerErrors("all good, no errors here"), []);
  assert.deepEqual(extractCompilerErrors(""), []);
});

test("extractCompilerErrors dedupes repeated lines", () => {
  const line = "Assets/Broken.cs(10,14): error CS0246: The type 'Foo' could not be found";
  const out = `${line}\n${line}\n${line}`;
  const errors = extractCompilerErrors(out);
  assert.equal(errors.length, 1);
});
