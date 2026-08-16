import { test } from "node:test";
import assert from "node:assert/strict";
import { renderConsoleTable, renderHtmlReport } from "./report.mjs";

const run = {
  meta: { timestamp: "2026-08-16T00:00:00.000Z", appVersion: "0.3.35", cpuRate: 4, tv: true },
  steps: [
    {
      id: "home_dpad_row",
      status: "ok",
      segments: {
        main: {
          n: 5,
          p50: 120,
          p95: 200,
          max: 210,
          phases: { script: 80, style: 20, layout: 10, paint: 5, composite: 2, parse: 1, other: 2 },
          topFunctions: [{ name: "focusNode", url: "homeScreen.js", line: 100, selfMs: 60 }],
          topEvents: [{ name: "UpdateLayoutTree", durMs: 20, phase: "style" }],
          longtaskCount: 1,
          longtaskMax: 90
        }
      }
    },
    { id: "grid_seeall", status: "failed", segments: {}, error: "pagination did not append cards" }
  ]
};

test("console table renders every segment and flags failures", () => {
  const out = renderConsoleTable(run);
  assert.match(out, /home_dpad_row\s+main\s+5\s+120\s+200/);
  assert.match(out, /grid_seeall\s+FAILED/);
});

test("html report is self-contained and embeds the data", () => {
  const html = renderHtmlReport(run);
  assert.match(html, /<!doctype html>/i);
  assert.match(html, /home_dpad_row/);
  assert.match(html, /__RUN_DATA__/);
  assert.doesNotMatch(html, /src="http|href="http/);
});
