import { test } from "node:test";
import assert from "node:assert/strict";
import { compareRuns } from "./compare.mjs";

const runA = {
  steps: [
    {
      id: "home_dpad_row",
      status: "ok",
      segments: { main: { n: 5, p50: 100, p95: 120, phases: {} } }
    }
  ]
};
const runB = {
  steps: [
    {
      id: "home_dpad_row",
      status: "ok",
      segments: { main: { n: 5, p50: 160, p95: 200, phases: {} } }
    }
  ]
};

test("compareRuns computes per-segment deltas", () => {
  const diff = compareRuns(runA, runB);
  assert.equal(diff.steps[0].id, "home_dpad_row");
  assert.equal(diff.steps[0].aP50, 100);
  assert.equal(diff.steps[0].bP50, 160);
  assert.equal(Math.round(diff.steps[0].deltaPct), 60);
  assert.equal(diff.regressions.length, 1);
});

test("compareRuns flags missing steps", () => {
  const diff = compareRuns(runA, { steps: [] });
  assert.equal(diff.steps[0].bP50, null);
  assert.equal(diff.steps[0].deltaPct, null);
});
