import test from "node:test";
import assert from "node:assert/strict";

import { PingCache } from "./ping-cache.ts";

function makePingBody() {
  return {
    connected: true,
    projectPath: "/path/to/MyGame",
    unityVersion: "6000.0.23f1",
    bridgeVersion: "0.1.0",
    mode: "live",
    compiling: false,
    isPlaying: false,
  };
}

test("PingCache starts empty", () => {
  const cache = new PingCache();
  assert.equal(cache.get(), null);
});

test("PingCache records a ping snapshot with asOf", () => {
  const cache = new PingCache();
  cache.record(makePingBody());
  const snapshot = cache.get();
  assert.ok(snapshot);
  assert.equal(snapshot!.connected, true);
  assert.equal(snapshot!.projectPath, "/path/to/MyGame");
  assert.ok(snapshot!.asOf);
  assert.ok(!isNaN(Date.parse(snapshot!.asOf)));
});

test("PingCache overwrites on subsequent records", () => {
  const cache = new PingCache();
  cache.record(makePingBody());
  const first = cache.get()!;
  assert.equal(first.connected, true);

  cache.record({ ...makePingBody(), connected: false });
  const second = cache.get()!;
  assert.equal(second.connected, false);
});

test("PingCache snapshot is independent of the input object", () => {
  const cache = new PingCache();
  const body = makePingBody();
  cache.record(body);
  body.connected = false;

  const snapshot = cache.get()!;
  assert.equal(snapshot.connected, true, "cache should not reflect mutation");
});
