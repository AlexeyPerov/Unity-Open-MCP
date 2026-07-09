import { test } from "node:test";
import assert from "node:assert/strict";

import {
  WIZARD_PRESETS,
  applyPresetToForm,
  customPreset,
  presetById,
  type PresetId,
  type WizardPreset,
} from "./wizard_presets.ts";

const EXPECTED_IDS: PresetId[] = [
  "regular-npm",
  "contributor",
  "team-ci",
  "secure-remote",
  "custom",
];

test("catalog ships exactly the five documented presets in display order", () => {
  assert.deepEqual(
    WIZARD_PRESETS.map((p) => p.id),
    EXPECTED_IDS,
  );
});

test("exactly one preset is marked recommended (regular-npm)", () => {
  const recommended = WIZARD_PRESETS.filter((p) => p.recommended);
  assert.equal(recommended.length, 1);
  assert.equal(recommended[0].id, "regular-npm");
});

test("primary presets are the three common-path choices; niche presets are demoted", () => {
  const primaryIds = WIZARD_PRESETS.filter(
    (p) => (p.tier ?? "primary") === "primary",
  ).map((p) => p.id);
  const moreIds = WIZARD_PRESETS.filter((p) => p.tier === "more").map((p) => p.id);
  // The three first-viewport choices.
  assert.deepEqual([...primaryIds].sort(), ["contributor", "custom", "regular-npm"].sort());
  // Niche presets stay reachable but are not peers of Recommended.
  assert.deepEqual([...moreIds].sort(), ["secure-remote", "team-ci"].sort());
});

test("every preset has a non-empty label, description, and tooltip", () => {
  for (const p of WIZARD_PRESETS) {
    assert.ok(p.label.length > 0, `${p.id} label empty`);
    assert.ok(p.description.length > 0, `${p.id} description empty`);
    assert.ok(p.tooltip.length > 0, `${p.id} tooltip empty`);
  }
});

test("presetById returns the matching preset", () => {
  assert.equal(presetById("contributor").id, "contributor");
  assert.equal(presetById("team-ci").id, "team-ci");
});

test("presetById falls back to custom for unknown / empty / undefined", () => {
  assert.equal(presetById("does-not-exist").id, "custom");
  assert.equal(presetById("").id, "custom");
  assert.equal(presetById(undefined).id, "custom");
  assert.equal(customPreset().id, "custom");
});

test("custom preset carries no pre-fill values", () => {
  assert.deepEqual(customPreset().values, {});
  assert.deepEqual(applyPresetToForm(customPreset()), {});
});

test("regular-npm pre-fills npx mode + bridge/verify on + domain deps off", () => {
  const form = applyPresetToForm(presetById("regular-npm"));
  assert.equal(form.useLocalCheckout, false);
  assert.equal(form.useGlobalInstall, false);
  assert.equal(form.useLocalPackages, false);
  assert.equal(form.installBridge, true);
  assert.equal(form.installVerify, true);
  assert.deepEqual(form.selectedUnityDomainDeps, []);
  // No client pin for the regular preset.
  assert.equal(form.mcpClient, undefined);
});

test("contributor pre-fills local checkout + local packages", () => {
  const form = applyPresetToForm(presetById("contributor"));
  assert.equal(form.useLocalCheckout, true);
  assert.equal(form.useGlobalInstall, false);
  assert.equal(form.useLocalPackages, true);
  assert.equal(form.installBridge, true);
  assert.equal(form.installVerify, true);
  assert.deepEqual(form.selectedUnityDomainDeps, []);
});

test("team-ci pre-fills global install + manual client", () => {
  const form = applyPresetToForm(presetById("team-ci"));
  assert.equal(form.useLocalCheckout, false);
  assert.equal(form.useGlobalInstall, true);
  assert.equal(form.mcpClient, "manual");
  assert.deepEqual(form.selectedUnityDomainDeps, []);
  // skillEnabled is advisory and not part of the form state.
  assert.equal("skillEnabled" in form, false);
});

test("team-ci preset advertises skillEnabled=false so the wizard can auto-skip the skill step", () => {
  const preset = presetById("team-ci");
  assert.equal(preset.values.skillEnabled, false);
});

test("secure-remote pre-fills published sources only (auth/bind are bridge-side)", () => {
  const form = applyPresetToForm(presetById("secure-remote"));
  assert.equal(form.useLocalCheckout, false);
  assert.equal(form.useGlobalInstall, false);
  assert.equal(form.installBridge, true);
  assert.equal(form.installVerify, true);
  assert.deepEqual(form.selectedUnityDomainDeps, []);
  // No client pin and no domain deps — the preset's job is the published
  // sources; token auth / remote bind live on the bridge window.
  assert.equal(form.mcpClient, undefined);
});

test("applyPresetToForm only includes keys the preset explicitly sets", () => {
  // regular-npm does not set mcpClient — the partial must omit it.
  const form = applyPresetToForm(presetById("regular-npm"));
  assert.equal(form.mcpClient, undefined);
  // contributor does not set mcpClient either.
  const form2 = applyPresetToForm(presetById("contributor"));
  assert.equal(form2.mcpClient, undefined);
});

test("every preset resolves through applyPresetToForm without throwing", () => {
  for (const p of WIZARD_PRESETS as readonly WizardPreset[]) {
    const form = applyPresetToForm(p);
    assert.ok(typeof form === "object" && form !== null);
  }
});
