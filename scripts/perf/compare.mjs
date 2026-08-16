import { readFile } from "node:fs/promises";
import path from "node:path";
import { RUNS_DIR } from "./paths.mjs";
import { renderConsoleTable } from "./report.mjs";

export function compareRuns(a, b) {
  const rows = [];
  const regressions = [];
  const bIndex = new Map((b.steps || []).filter((s) => s.status === "ok").map((s) => [s.id, s]));
  for (const stepA of (a.steps || []).filter((s) => s.status === "ok")) {
    for (const [segment, segA] of Object.entries(stepA.segments || {})) {
      const segB = bIndex.get(stepA.id)?.segments?.[segment];
      const aP50 = segA.p50 || 0;
      const bP50 = segB?.p50 || null;
      const deltaPct = bP50 === null ? null : aP50 > 0 ? ((bP50 - aP50) / aP50) * 100 : null;
      const row = { id: stepA.id, segment, aP50, bP50, deltaPct };
      rows.push(row);
      if (deltaPct !== null && deltaPct > 15 && bP50 > 20) {
        regressions.push(row);
      }
    }
  }
  return { steps: rows, regressions };
}

export function renderCompareHtml(diff, runA, runB) {
  const rows = diff.steps
    .map(
      (r) =>
        `<tr><td>${r.id}</td><td>${r.segment}</td><td>${Math.round(
          r.aP50
        )}</td><td>${r.bP50 === null ? "—" : Math.round(r.bP50)}</td><td class="${
          r.deltaPct > 15 && r.bP50 > 20 ? "fail" : ""
        }">${r.deltaPct === null ? "—" : Math.round(r.deltaPct) + "%"}</td></tr>`
    )
    .join("");
  return `<!doctype html>
<html lang="en"><head><meta charset="utf-8" /><title>Perf compare</title>
<style>body{font:14px/1.5 -apple-system,sans-serif;background:#14161a;color:#e6e6e6;margin:2rem}
table{border-collapse:collapse}td,th{border:1px solid #333;padding:4px 12px;text-align:left}.fail{color:#e4574c}</style>
</head><body>
<h1>Perf compare</h1>
<p>${runA.meta?.timestamp} → ${runB.meta?.timestamp}</p>
<table><tr><th>step</th><th>seg</th><th>A p50</th><th>B p50</th><th>Δ</th></tr>${rows}</table>
<p>${diff.regressions.length} regression(s) &gt; 15%</p>
</body></html>`;
}

export async function writeCompare(dirA, dirB) {
  const load = async (dir) => JSON.parse(await readFile(path.join(dir, "run.json"), "utf8"));
  const runA = await load(dirA);
  const runB = await load(dirB);
  const diff = compareRuns(runA, runB);
  const out = path.join(RUNS_DIR, `compare-${path.basename(dirA)}-${path.basename(dirB)}.html`);
  const { writeFile, mkdir } = await import("node:fs/promises");
  await mkdir(RUNS_DIR, { recursive: true });
  await writeFile(out, renderCompareHtml(diff, runA, runB));
  for (const r of diff.steps) {
    console.log(
      `${r.id}/${r.segment}: ${Math.round(r.aP50)}ms → ${
        r.bP50 === null ? "missing" : Math.round(r.bP50) + "ms"
      } (${r.deltaPct === null ? "n/a" : Math.round(r.deltaPct) + "%"})`
    );
  }
  console.log(`\nReport: ${out}\nRegressions >15%: ${diff.regressions.length}`);
  return 0;
}
