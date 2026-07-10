// Plain-JS test-time shim for Svelte 5 `$state` / `$derived` / `$effect` runes.
//
// The hub state stores use `$state<T>(initial)` as a field initializer. Under
// the Svelte compiler this is transformed into a reactive signal; under Node's
// test runner the rune is left as a bare `$state(...)` call — a runtime
// ReferenceError that blocks class construction entirely.
//
// This shim defines the runes as identity functions on the global scope so the
// store classes construct as plain objects: `$state(x)` returns `x` as a normal
// property. Reactivity is lost — these tests exercise the state-transition
// *logic* (add/remove/cap/lifecycle), not the reactivity wiring.
//
// This is the JS entry (importable from the .mjs registration hook without
// --experimental-strip-types). The TS-typed sibling (_test-shim.ts) documents
// the global declarations for editors; it is not imported at runtime.
globalThis.$state = (initial) => initial;
globalThis.$derived = (compute) => compute();
globalThis.$effect = (fn) => {
  try {
    const cleanup = fn();
    if (typeof cleanup === "function") cleanup();
  } catch {
    // $effect bodies often reference DOM/window; swallow in tests.
  }
};
