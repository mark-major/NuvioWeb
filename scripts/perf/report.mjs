import { writeFile, mkdir } from "node:fs/promises";
import path from "node:path";

function pad(value, width) {
  return String(value).padEnd(width, " ");
}

export function renderConsoleTable(run) {
  const lines = [];
  lines.push(
    `${run.meta?.timestamp || ""}  v${run.meta?.appVersion || "?"}  cpu=${run.meta?.cpuRate}x  tv=${run.meta?.tv ? "on" : "off"}`
  );
  lines.push(
    pad("step", 22) +
      pad("seg", 9) +
      pad("n", 4) +
      pad("p50", 8) +
      pad("p95", 8) +
      pad("script", 9) +
      pad("style", 8) +
      pad("layout", 9) +
      pad("paint", 8) +
      "longtasks"
  );
  for (const step of run.steps || []) {
    if (step.status !== "ok") {
      lines.push(`${pad(step.id, 22)}FAILED/SKIPPED: ${step.error || ""}`);
      continue;
    }
    for (const [name, seg] of Object.entries(step.segments || {})) {
      lines.push(
        pad(step.id, 22) +
          pad(name, 9) +
          pad(seg.n, 4) +
          pad(Math.round(seg.p50), 8) +
          pad(Math.round(seg.p95), 8) +
          pad(Math.round(seg.phases?.script || 0), 9) +
          pad(Math.round(seg.phases?.style || 0), 8) +
          pad(Math.round(seg.phases?.layout || 0), 9) +
          pad(Math.round(seg.phases?.paint || 0), 8) +
          `${seg.longtaskCount || 0} (max ${Math.round(seg.longtaskMax || 0)}ms)`
      );
    }
  }
  return lines.join("\n");
}

export function renderHtmlReport(run) {
  const data = JSON.stringify(run).replace(/</g, "\\u003c");
  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8" />
<title>Nuvio Perf Report — ${run.meta?.timestamp || ""}</title>
<style>
  body { font: 14px/1.5 -apple-system, sans-serif; background: #14161a; color: #e6e6e6; margin: 2rem; }
  h1 { font-size: 1.2rem; } h2 { font-size: 1rem; margin-top: 2rem; }
  .card { background: #1d2026; border-radius: 8px; padding: 1rem 1.25rem; margin: 1rem 0; }
  .seg { display: flex; gap: 2rem; flex-wrap: wrap; }
  .metric b { display: block; font-size: 1.3rem; }
  .bar { display: flex; height: 10px; border-radius: 5px; overflow: hidden; margin: 0.5rem 0; max-width: 640px; }
  .bar span { display: block; height: 100%; }
  .script { background: #4f8ef7; } .style { background: #b085f5; } .layout { background: #f5a623; }
  .paint { background: #50c878; } .composite { background: #35b5b0; } .parse { background: #e4574c; } .other { background: #777; }
  details { margin: 0.25rem 0; } summary { cursor: pointer; color: #9fc3ff; }
  table { border-collapse: collapse; margin-top: 0.5rem; }
  td, th { border: 1px solid #333; padding: 2px 8px; text-align: left; }
  .legend span { margin-right: 1rem; font-size: 12px; }
  .fail { color: #e4574c; }
</style>
</head>
<body>
<h1>Nuvio UI Performance</h1>
<div class="legend">
  <span class="script">■ script</span><span class="style">■ style</span><span class="layout">■ layout</span>
  <span class="paint">■ paint</span><span class="composite">■ composite</span><span class="parse">■ parse</span><span class="other">■ other</span>
</div>
<div id="root"></div>
<script>window.__RUN_DATA__ = ${data};</script>
<script>
const run = window.__RUN_DATA__;
const root = document.getElementById("root");
for (const step of run.steps || []) {
  const card = document.createElement("div");
  card.className = "card";
  let inner = "<h2>" + step.id + (step.status !== "ok" ? ' <span class="fail">' + step.status + ": " + (step.error || "") + "</span>" : "") + "</h2>";
  for (const [name, seg] of Object.entries(step.segments || {})) {
    const total = Object.values(seg.phases || {}).reduce((a, b) => a + b, 0) || 1;
    inner += '<div class="seg"><h3>' + name + "</h3>";
    for (const m of [["n", seg.n], ["p50 ms", Math.round(seg.p50)], ["p95 ms", Math.round(seg.p95)], ["longtasks", (seg.longtaskCount || 0) + " / " + Math.round(seg.longtaskMax || 0) + "ms"]]) {
      inner += '<div class="metric"><span>' + m[0] + "</span><b>" + m[1] + "</b></div>";
    }
    inner += "</div>";
    inner += '<div class="bar">' + Object.entries(seg.phases || {}).map(([k, v]) => '<span class="' + k + '" style="width:' + (100 * v / total) + '%" title="' + k + " " + Math.round(v) + 'ms"></span>').join("") + "</div>";
    inner += "<details><summary>top JS functions</summary><table>" + (seg.topFunctions || []).map((f) => "<tr><td>" + Math.round(f.selfMs) + "ms</td><td>" + f.name + "</td><td>" + f.url + ":" + f.line + "</td></tr>").join("") + "</table></details>";
    inner += "<details><summary>top trace events</summary><table>" + (seg.topEvents || []).map((e) => "<tr><td>" + Math.round(e.durMs) + "ms</td><td>" + e.name + "</td><td>" + e.phase + "</td></tr>").join("") + "</table></details>";
  }
  card.innerHTML = inner;
  root.appendChild(card);
}
</script>
</body>
</html>`;
}

export async function writeRunArtifacts(runDir, run) {
  await mkdir(runDir, { recursive: true });
  const jsonPath = path.join(runDir, "run.json");
  const htmlPath = path.join(runDir, "report.html");
  await writeFile(jsonPath, JSON.stringify(run, null, 2));
  await writeFile(htmlPath, renderHtmlReport(run));
  return { jsonPath, htmlPath };
}
