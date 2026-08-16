import { test } from "node:test";
import assert from "node:assert/strict";
import { buildInitScript, summarizeEventTimings } from "./timing.mjs";

test("init script defines the perf API and observers", () => {
  const src = buildInitScript();
  assert.match(src, /window\.__nuvioPerf/);
  assert.match(src, /type:\s*"event"/);
  assert.match(src, /type:\s*"longtask"/);
  assert.match(src, /requestAnimationFrame/g);
});

test("summarizeEventTimings computes percentiles and delays", () => {
  const entries = [10, 20, 30, 40, 100].map((duration, i) => ({
    name: "keydown",
    startTime: 1000 + i * 100,
    processingStart: 1000 + i * 100 + 5,
    processingEnd: 1000 + i * 100 + 5 + duration / 2,
    duration,
    interactionId: i + 1
  }));
  const s = summarizeEventTimings(entries);
  assert.equal(s.count, 5);
  assert.equal(s.p50, 30);
  assert.equal(s.p95, 100);
  assert.equal(s.inputDelayP50, 5);
  assert.equal(s.processingP50, 15);
});

test("summarizeEventTimings is safe when empty", () => {
  assert.deepEqual(summarizeEventTimings([]), {
    count: 0,
    p50: 0,
    p95: 0,
    inputDelayP50: 0,
    processingP50: 0
  });
});
