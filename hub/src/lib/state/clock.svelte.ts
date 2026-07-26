/**
 * Shared "current time" clock for relative-time displays.
 *
 * H33: {@link RelativeTime} and any other component that shows a
 * "2m ago"-style label need a ticking `now` value to re-render as time
 * passes, otherwise a row rendered "just now" keeps that text for the
 * whole session. Reading `Date.now()` inside a `$derived` does not work
 * — a `$derived` only recomputes when its *inputs* change, and
 * `Date.now()` is not an input Svelte can track.
 *
 * This module exposes a single reactive `now` (`$state`) backed by ONE
 * `setInterval` for the whole app. The interval is armed lazily when the
 * first consumer mounts (via `useNow()` in an `$effect`) and torn down
 * when the last consumer unmounts, so an app with no relative-time
 * widgets pays nothing. Every consumer shares the same tick, so a list of
 * 50 relative timestamps repaints on one timer, not 50.
 *
 * The tick interval (30 s) matches the granularity of the relative
 * labels: "just now" covers <45 s, "Nm" rounds to the minute, so a 30 s
 * tick is frequent enough that a label never drifts more than one bucket
 * behind wall-clock time without being a busy loop.
 */

/** Tick cadence for the shared clock, in milliseconds. */
const TICK_MS = 30_000;

/**
 * The shared wall-clock value, in ms since the epoch. Read this from a
 * `$derived` (or template) to make the consumer re-render on every tick.
 * It is `let` + `$state` rather than a class field so the reactivity is
 * module-wide: every importer observes the same tick.
 */
let now = $state(Date.now());

/** Number of currently-mounted consumers driving the interval. */
let consumers = 0;
/** The armed interval handle, or `null` when no consumer is mounted. */
let timer: ReturnType<typeof setInterval> | null = null;

function arm(): void {
  if (timer !== null) return;
  timer = setInterval(() => {
    now = Date.now();
  }, TICK_MS);
}

function disarm(): void {
  if (timer === null) return;
  clearInterval(timer);
  timer = null;
}

/**
 * Subscribe a consumer to the shared clock. Call this exactly once from a
 * component's `$effect` (which also gives you the cleanup hook); the
 * interval is armed on the first subscriber and torn down on the last
 * unsubscriber. Returns nothing — read {@link now} reactively instead.
 *
 * @returns a cleanup function that decrements the consumer count and
 *   disarms the interval when the last consumer leaves. Invoke it from
 *   the `$effect`'s return-statement cleanup so unmounting the component
 *   releases its subscription.
 */
export function useNow(): () => void {
  consumers += 1;
  arm();
  return () => {
    consumers -= 1;
    if (consumers <= 0) {
      consumers = 0;
      disarm();
    }
  };
}

/**
 * The current wall-clock time in ms since the epoch. This is a reactive
 * `$state` read — a `$derived` or template expression that touches `now`
 * recomputes on every tick of the shared interval.
 */
export function getNow(): number {
  return now;
}
