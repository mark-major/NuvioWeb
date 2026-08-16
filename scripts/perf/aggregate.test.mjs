import { test } from "node:test";
import assert from "node:assert/strict";
import { percentile, aggregateLatencies, selectMedianRep, aggregateStep } from "./aggregate.mjs";

const rep = (p50, extra = {}) => ({
  segments: {
    main: {
      latency: { summary: { p50, p95: p50, inputDelayP50: 0, processingP50: 0 } },
      phases: { script: p50, style: 0, layout: 0, paint: 0, composite: 0, parse: 0, other: 0 },
      topFunctions: [{ name: "fn", url: "u.js", line: 1, selfMs: p50 }],
      topEvents: [],
      longtasks: []
    },
    ...extra.segments
  }
});

test("percentile uses nearest-rank", () => {
  assert.equal(percentile([10, 20, 30, 40, 100], 50), 30);
  assert.equal(percentile([10, 20, 30, 40, 100], 95), 100);
  assert.equal(percentile([], 50), 0);
});

test("aggregateLatencies spans reps", () => {
  const agg = aggregateLatencies([rep(10), rep(20), rep(100)], "main");
  assert.equal(agg.n, 3);
  assert.equal(agg.p50, 20);
  assert.equal(agg.max, 100);
});

test("selectMedianRep picks the middle rep", () => {
  const reps = [rep(100), rep(20), rep(10)];
  const median = selectMedianRep(reps, "main");
  assert.equal(median.segments.main.latency.summary.p50, 20);
});

test("aggregateStep takes attribution from the median rep", () => {
  const step = { id: "x", status: "ok", reps: [rep(100), rep(20), rep(10)] };
  const agg = aggregateStep(step);
  assert.equal(agg.id, "x");
  assert.equal(agg.segments.main.phases.script, 20);
  assert.equal(agg.segments.main.topFunctions[0].selfMs, 20);
});
