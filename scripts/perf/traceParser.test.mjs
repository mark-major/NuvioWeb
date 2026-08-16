import { test } from "node:test";
import assert from "node:assert/strict";
import { parseTrace } from "./traceParser.mjs";

const MARK_START = { name: "nuvio:step:start", ph: "R", ts: 1_000_000, pid: 1, tid: 1 };
const MARK_END = { name: "nuvio:step:end", ph: "R", ts: 2_000_000, pid: 1, tid: 1 };

function fixtureTrace() {
  return {
    traceEvents: [
      MARK_START,
      MARK_END,
      { name: "FunctionCall", ph: "X", ts: 1_000_000, dur: 300_000, pid: 1, tid: 1 },
      { name: "UpdateLayoutTree", ph: "X", ts: 1_100_000, dur: 100_000, pid: 1, tid: 1 },
      { name: "Layout", ph: "X", ts: 1_250_000, dur: 80_000, pid: 1, tid: 1 },
      { name: "Paint", ph: "X", ts: 1_400_000, dur: 40_000, pid: 1, tid: 1 },
      { name: "CompositeLayers", ph: "X", ts: 1_500_000, dur: 20_000, pid: 1, tid: 1 },
      { name: "FunctionCall", ph: "X", ts: 2_500_000, dur: 500_000, pid: 1, tid: 1 },
      {
        name: "Profile",
        ph: "P",
        ts: 900_000,
        pid: 1,
        tid: 1,
        args: { data: { startTime: 900_000 } }
      },
      {
        name: "ProfileChunk",
        ph: "P",
        ts: 1_050_000,
        pid: 1,
        tid: 1,
        args: {
          data: {
            cpuProfile: {
              nodes: [
                { id: 1, callFrame: { functionName: "root", url: "a.js", lineNumber: 1 } },
                { id: 2, callFrame: { functionName: "hotFn", url: "b.js", lineNumber: 42 } }
              ],
              samples: [2, 2, 1, 2, 2, 2, 2, 2, 2, 2]
            },
            timeDeltas: [
              100000, 100000, 100000, 100000, 100000, 100000, 100000, 100000, 100000, 100000
            ]
          }
        }
      }
    ]
  };
}

test("parseTrace slices to the mark window and splits phases", () => {
  const result = parseTrace(fixtureTrace());
  assert.equal(result.windowMs, 1000);
  assert.equal(result.phases.script, 120);
  assert.equal(result.phases.style, 100);
  assert.equal(result.phases.layout, 80);
  assert.equal(result.phases.paint, 40);
  assert.equal(result.phases.composite, 20);
  assert.equal(result.phases.other, 0);
});

test("parseTrace reports top events inside the window only", () => {
  const result = parseTrace(fixtureTrace());
  const names = result.topEvents.map((e) => e.name);
  assert.ok(names.includes("FunctionCall"));
  assert.equal(result.topEvents[0].durMs, 300);
});

test("parseTrace joins cpu samples to frames", () => {
  const result = parseTrace(fixtureTrace());
  assert.equal(result.topFunctions[0].name, "hotFn");
  assert.equal(result.topFunctions[0].selfMs, 900);
  assert.equal(result.topFunctions[0].url, "b.js");
  assert.equal(result.topFunctions[0].line, 43);
});

test("parseTrace survives malformed input", () => {
  assert.deepEqual(parseTrace({ traceEvents: [] }), {
    windowMs: 0,
    phases: { script: 0, style: 0, layout: 0, paint: 0, composite: 0, parse: 0, other: 0 },
    topFunctions: [],
    topEvents: []
  });
  assert.equal(parseTrace(null).windowMs, 0);
});
