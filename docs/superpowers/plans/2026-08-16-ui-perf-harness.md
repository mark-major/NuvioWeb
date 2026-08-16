# UI Performance Harness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Automated Playwright+CDP harness measuring NuvioWeb UI interaction latency and attributing time to JS vs CSS/style/layout/paint work, per the approved spec.

**Architecture:** `scripts/perf/` ESM modules driven by one CLI (`npm run perf -- login|run|compare`). Runs in desktop Chromium under a TV profile (Tizen UA → app's real Tizen code paths, 4× CPU throttle). Per rep: in-page Event Timing observer (input→paint) + CDP trace sliced by user-timing marks (phase split, top JS stacks). Reports: console, `run.json`, self-contained HTML.

**Tech Stack:** Node ESM, Playwright 1.62.1 (already in `node_modules`, Chromium binaries cached at `~/Library/Caches/ms-playwright`), CDP `Tracing`/`IO`/`Emulation`, node `zlib`, node --test.

**Spec:** `docs/superpowers/specs/2026-08-16-ui-perf-harness-design.md`

## Global Constraints

- Node scripts are ESM (`"type": "module"`), 2-space indent, prettier-formatted, no comments unless a non-obvious why demands one.
- Zero new npm deps beyond `playwright@1.62.1` in devDependencies (installed with `PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1`).
- No app-code changes except `.gitignore` and `package.json`. The harness treats the app as a black box driven by keyboard input only (TV reality).
- Never commit anything under `perf-runs/` (auth state + traces).
- All commands run from repo root. `npm run build` must keep passing after Task 1.
- Unit tests must not launch browsers (they run under `npm test` = `node --test "**/*.test.mjs"`); browser-dependent verification happens in Task 10's smoke run.

---

### Task 1: Remove in-flight harness

**Files:**

- Delete: `scripts/perf-test-playwright.mjs`, `scripts/perf-run.mjs`, `scripts/perf-collect.mjs`, `js/core/diagnostics/perfFlags.js`, `js/core/diagnostics/perfMetrics.js`, `PERF_IMPLEMENTATION_SPEC.md`, `perf-runs/` (entire dir)
- Modify: `.gitignore`

**Interfaces:**

- Consumes: nothing.
- Produces: a clean tree; `perf-runs/` exists only as a gitignored output dir recreated by later tasks.

- [ ] **Step 1: Verify nothing references the removed modules**

Run: `grep -rn "perfFlags\|PerfMetrics\|perfMonitor\|perfEndpoint\|perf-run\|perf-collect\|perf-test-playwright" js/ scripts/ index.html boot-guard.js package.json appinfo.json`
Expected: matches only inside the files being deleted (and none in `js/` outside `js/core/diagnostics/`). If any other file matches, STOP and report — the spec's clean-removal assumption is broken.

- [ ] **Step 2: Delete the files**

```bash
git rm --cached -r . 2>/dev/null; # no — files are untracked; just delete:
rm scripts/perf-test-playwright.mjs scripts/perf-run.mjs scripts/perf-collect.mjs
rm js/core/diagnostics/perfFlags.js js/core/diagnostics/perfMetrics.js
rm PERF_IMPLEMENTATION_SPEC.md
rm -rf perf-runs
```

- [ ] **Step 3: Ignore perf outputs**

Append to `.gitignore`:

```
perf-runs/
```

- [ ] **Step 4: Verify app still builds**

Run: `npm run build`
Expected: exits 0.

- [ ] **Step 5: Commit**

```bash
git add .gitignore
git commit -m "Remove in-flight perf harness in favor of new tooling"
```

(The deleted files were untracked, so only `.gitignore` lands in the commit; `git status` must show no `perf-` leftovers.)

---

### Task 2: CLI scaffold + browser factory

**Files:**

- Create: `scripts/perf/cli.mjs`
- Create: `scripts/perf/browser.mjs`
- Create: `scripts/perf/paths.mjs`
- Modify: `package.json` (devDependency + script)

**Interfaces:**

- Produces (used by all later tasks):
  - `paths.mjs`: `ROOT_DIR`, `RUNS_DIR` (`<root>/perf-runs`), `AUTH_STATE_PATH` (`<root>/perf-runs/.auth/state.json`), `runDirFor(timestamp)` → `perf-runs/<timestamp>`.
  - `browser.mjs`: `TV_UA` (exported const), `launchPerfBrowser({ tv = true, headed = false })` → `Promise<BrowserContext>` (storageState applied by caller), `openPerfPage(context, { cpuRate = 4, netProfile = null } = {})` → `Promise<{ page, cdp }>` where `cdp` is a Playwright CDPSession with CPU throttling already applied, `NET_PROFILES = { tv: { latencyMs: 40, downloadBps: 1250000, uploadBps: 62500 } }`.
  - `cli.mjs`: usage text on unknown command/`--help`, exit 0 for `--help`, exit 2 for unknown.

- [ ] **Step 1: Install playwright as a saved devDependency**

```bash
PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1 npm install --save-dev playwright@1.62.1
```

- [ ] **Step 2: Add the npm script**

In `package.json` scripts (after `"serve"`): `"perf": "node ./scripts/perf/cli.mjs"`.

- [ ] **Step 3: Write `scripts/perf/paths.mjs`**

```js
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
export const ROOT_DIR = path.resolve(__dirname, "..", "..");
export const RUNS_DIR = path.join(ROOT_DIR, "perf-runs");
export const AUTH_STATE_PATH = path.join(RUNS_DIR, ".auth", "state.json");

export function runDirFor(timestamp) {
  return path.join(RUNS_DIR, timestamp);
}
```

- [ ] **Step 4: Write `scripts/perf/browser.mjs`**

```js
import { chromium } from "playwright";

export const TV_UA =
  "Mozilla/5.0 (SMART-TV; LINUX; Tizen 5.5) AppleWebkit/537.36 (KHTML, like Gecko) Version/5.5 TV Safari/537.36";

export const NET_PROFILES = {
  tv: { latencyMs: 40, downloadBps: 1250000, uploadBps: 62500 }
};

export async function launchPerfBrowser({ tv = true, headed = false, storageState = null } = {}) {
  const browser = await chromium.launch({ headless: !headed });
  const context = await browser.newContext({
    viewport: tv ? { width: 1920, height: 1080 } : undefined,
    userAgent: tv ? TV_UA : undefined,
    storageState: storageState || undefined
  });
  context.on("page", (page) => {
    page.setDefaultTimeout(20000);
  });
  return context;
}

export async function openPerfPage(context, { cpuRate = 4, netProfile = null } = {}) {
  const page = await context.newPage();
  const cdp = await context.newCDPSession(page);
  if (Number.isFinite(cpuRate) && cpuRate > 1) {
    await cdp.send("Emulation.setCPUThrottlingRate", { rate: cpuRate });
  }
  const net = netProfile ? NET_PROFILES[netProfile] : null;
  if (net) {
    await cdp.send("Network.enable");
    await cdp.send("Network.emulateNetworkConditions", {
      offline: false,
      latency: net.latencyMs,
      downloadThroughput: net.downloadBps,
      uploadThroughput: net.uploadBps
    });
  }
  return { page, cdp };
}
```

- [ ] **Step 5: Write `scripts/perf/cli.mjs` (skeleton)**

```js
const USAGE = `Usage: npm run perf -- <command> [options]

Commands:
  login                     One-time interactive sign-in; saves session for runs
  run [--steps <list>]      Run the measurement protocol (default: all steps)
      [--reps <n>]          Repetitions per step (default 5, +1 warmup)
      [--cpu <n>]           CPU throttle factor (default 4; 1 = off)
      [--net tv|off]        Network profile (default off)
      [--no-tv]             Plain desktop profile (no Tizen UA / 1080p)
      [--headed]            Show the browser
      [--url <url>]         App URL (default http://127.0.0.1:4173)
      [--skip-build]        Don't build before ensuring the server
      [--keep-traces]       Keep raw .json.gz traces (kept by default anyway)
      [--out <dir>]         Output dir (default perf-runs/<timestamp>)
  compare <runA> <runB>     Diff two runs; writes compare-<A>-<B>.html
Step ids: home_dpad_row home_dpad_rows grid_seeall grid_library
          transition_detail transition_settings settings_theme_toggle
Special: --steps smoke (1 rep of home_dpad_row)`;

export function parseArgs(argv) {
  const positional = [];
  const flags = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith("--")) {
      const key = a.slice(2);
      const next = argv[i + 1];
      if (next !== undefined && !next.startsWith("--")) {
        flags[key] = next;
        i++;
      } else {
        flags[key] = true;
      }
    } else {
      positional.push(a);
    }
  }
  return { command: positional[0] || null, positional, flags };
}

async function main() {
  const { command } = parseArgs(process.argv.slice(2));
  if (!command || command === "help" || command === "--help") {
    console.log(USAGE);
    return 0;
  }
  if (command === "login") {
    const { runLogin } = await import("./login.mjs");
    return runLogin(parseArgs(process.argv.slice(2)).flags);
  }
  if (command === "run") {
    const { runProtocol: exec } = await import("./runner.mjs");
    return exec(parseArgs(process.argv.slice(2)).flags);
  }
  if (command === "compare") {
    const { writeCompare } = await import("./compare.mjs");
    const { positional } = parseArgs(process.argv.slice(2));
    if (positional.length < 3) {
      console.error("compare requires two run dirs");
      return 2;
    }
    return writeCompare(positional[1], positional[2]);
  }
  console.error(`Unknown command: ${command}\n`);
  console.log(USAGE);
  return 2;
}

main()
  .then((code) => process.exit(code ?? 0))
  .catch((err) => {
    console.error(err);
    process.exit(1);
  });
```

- [ ] **Step 6: Verify CLI + context factory**

Run: `npm run perf -- --help`
Expected: usage text, exit 0.
Run: `npm run perf -- bogus`
Expected: "Unknown command", exit 2.
Run: `node -e "import('./scripts/perf/browser.mjs').then(m => process.exit(typeof m.launchPerfBrowser === 'function' && m.TV_UA.includes('Tizen') ? 0 : 1))"`
Expected: exit 0.

- [ ] **Step 7: Commit**

```bash
git add package.json package-lock.json scripts/perf/cli.mjs scripts/perf/browser.mjs scripts/perf/paths.mjs
git commit -m "Perf harness: CLI scaffold and TV-profile browser factory"
```

---

### Task 3: In-page timing instrumentation

**Files:**

- Create: `scripts/perf/timing.mjs`
- Test: `scripts/perf/timing.test.mjs`

**Interfaces:**

- Produces:
  - `buildInitScript()` → string of JS injected once per context via `context.addInitScript`; defines `window.__nuvioPerf = { beginStep(id), finishStep(): Promise<RepPayload> }`. RepPayload: `{ id, stepStart, eventTimingSupported, eventTimings: [{name, startTime, processingStart, processingEnd, duration, interactionId}], longtasks: [{startTime, duration}], rafLatencies: [{key, latency}] }`.
  - `installTiming(context)` → calls `context.addInitScript(buildInitScript())`.
  - `measureSegment(page, id, fn)` → `Promise<RepPayload>`: `beginStep(id)`, `await fn()`, `await finishStep()`.
  - `summarizeEventTimings(entries)` → `{ count, p50, p95, inputDelayP50, processingP50 }` (pure; ms numbers, 0 when empty). `inputDelay = processingStart - startTime`; `processing = processingEnd - processingStart`; p50/p95 computed on `duration`.

- [ ] **Step 1: Write the failing test**

`scripts/perf/timing.test.mjs`:

```js
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `node --test scripts/perf/timing.test.mjs`
Expected: FAIL — `Cannot find module './timing.mjs'`.

- [ ] **Step 3: Write `scripts/perf/timing.mjs`**

```js
function percentile(sorted, p) {
  if (!sorted.length) return 0;
  const idx = Math.min(Math.ceil((p / 100) * sorted.length) - 1, sorted.length - 1);
  return sorted[idx];
}

export function summarizeEventTimings(entries) {
  if (!entries.length) {
    return { count: 0, p50: 0, p95: 0, inputDelayP50: 0, processingP50: 0 };
  }
  const durations = entries.map((e) => e.duration).sort((a, b) => a - b);
  const delays = entries.map((e) => e.processingStart - e.startTime).sort((a, b) => a - b);
  const processing = entries.map((e) => e.processingEnd - e.processingStart).sort((a, b) => a - b);
  return {
    count: entries.length,
    p50: percentile(durations, 50),
    p95: percentile(durations, 95),
    inputDelayP50: percentile(delays, 50),
    processingP50: percentile(processing, 50)
  };
}

export function buildInitScript() {
  return `(() => {
  if (window.__nuvioPerf) return;
  const state = { stepId: null, stepStart: 0, events: [], longtasks: [], raf: [], supported: false };
  try {
    new PerformanceObserver((list) => {
      for (const e of list.getEntries()) {
        if (e.startTime < state.stepStart) continue;
        state.events.push({
          name: e.name,
          startTime: e.startTime,
          processingStart: e.processingStart,
          processingEnd: e.processingEnd,
          duration: e.duration,
          interactionId: e.interactionId || 0
        });
      }
    }).observe({ type: "event", buffered: false });
    state.supported = true;
  } catch (_) { state.supported = false; }
  try {
    new PerformanceObserver((list) => {
      for (const e of list.getEntries()) {
        if (e.startTime >= state.stepStart) {
          state.longtasks.push({ startTime: e.startTime, duration: e.duration });
        }
      }
    }).observe({ type: "longtask", buffered: false });
  } catch (_) {}
  document.addEventListener(
    "keydown",
    (ev) => {
      const t0 = performance.now();
      requestAnimationFrame(() => {
        requestAnimationFrame(() => {
          if (state.stepStart && t0 >= state.stepStart) {
            state.raf.push({ key: ev.key, latency: performance.now() - t0 });
          }
        });
      });
    },
    { capture: true }
  );
  window.__nuvioPerf = {
    beginStep(id) {
      state.stepId = id;
      state.stepStart = performance.now();
      state.events = [];
      state.longtasks = [];
      state.raf = [];
      try { performance.mark("nuvio:step:start"); } catch (_) {}
    },
    async finishStep() {
      try { performance.mark("nuvio:step:end"); } catch (_) {}
      await new Promise((resolve) =>
        requestAnimationFrame(() => requestAnimationFrame(resolve))
      );
      return {
        id: state.stepId,
        stepStart: state.stepStart,
        eventTimingSupported: state.supported,
        eventTimings: state.events.slice(),
        longtasks: state.longtasks.slice(),
        rafLatencies: state.raf.slice()
      };
    }
  };
})();`;
}

export function installTiming(context) {
  context.addInitScript(buildInitScript());
}

export async function measureSegment(page, id, fn) {
  await page.evaluate((stepId) => window.__nuvioPerf.beginStep(stepId), id);
  await fn();
  return page.evaluate(() => window.__nuvioPerf.finishStep());
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `node --test scripts/perf/timing.test.mjs`
Expected: 3 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add scripts/perf/timing.mjs scripts/perf/timing.test.mjs
git commit -m "Perf harness: in-page event-timing instrumentation"
```

---

### Task 4: CDP trace collection

**Files:**

- Create: `scripts/perf/trace.mjs`

**Interfaces:**

- Consumes: a Playwright CDPSession (from `openPerfPage`).
- Produces:
  - `TRACE_CATEGORIES` (exported const array).
  - `startTrace(cdp)` → `Promise<void>` (`Tracing.start`, ReturnAsStream).
  - `collectTrace(cdp)` → `Promise<object>` (parsed trace JSON; resolves after `Tracing.tracingComplete`).
  - `saveTraceGz(runDir, stepId, repIndex, trace)` → `Promise<string>` path written (`trace-<step>-<rep>.json.gz` via `zlib.gzipSync`).

- [ ] **Step 1: Write `scripts/perf/trace.mjs`**

```js
import { gzipSync } from "node:zlib";
import { writeFile, mkdir } from "node:fs/promises";
import path from "node:path";

export const TRACE_CATEGORIES = [
  "devtools.timeline",
  "v8.execute",
  "disabled-by-default-v8.cpu_profiler",
  "blink.user_timing"
];

export async function startTrace(cdp) {
  await cdp.send("Tracing.start", {
    transferMode: "ReturnAsStream",
    categories: TRACE_CATEGORIES.join(","),
    options: "sampling-frequency=10000"
  });
}

export async function collectTrace(cdp) {
  const chunks = [];
  const done = new Promise((resolve, reject) => {
    cdp.on("Tracing.tracingComplete", async (event) => {
      try {
        const handle = event.stream;
        for (;;) {
          const result = await cdp.send("IO.read", { handle });
          if (result.data) chunks.push(Buffer.from(result.data, "base64"));
          if (result.eof) break;
        }
        await cdp.send("IO.close", { handle });
        resolve();
      } catch (error) {
        reject(error);
      }
    });
    cdp.send("Tracing.end").catch(reject);
  });
  await done;
  return JSON.parse(Buffer.concat(chunks).toString("utf8"));
}

export async function saveTraceGz(runDir, stepId, repIndex, trace) {
  await mkdir(runDir, { recursive: true });
  const file = path.join(runDir, `trace-${stepId}-${repIndex}.json.gz`);
  await writeFile(file, gzipSync(JSON.stringify(trace)));
  return file;
}
```

- [ ] **Step 2: Syntax check**

Run: `node --check scripts/perf/trace.mjs`
Expected: no output, exit 0. (Real trace collection is exercised in Task 10's smoke run — a unit test cannot produce a CDP trace.)

- [ ] **Step 3: Commit**

```bash
git add scripts/perf/trace.mjs
git commit -m "Perf harness: CDP tracing collection with gz persistence"
```

---

### Task 5: Trace parser

**Files:**

- Create: `scripts/perf/traceParser.mjs`
- Test: `scripts/perf/traceParser.test.mjs`

**Interfaces:**

- Produces: `parseTrace(traceJson, { startMark = "nuvio:step:start", endMark = "nuvio:step:end" })` →
  `{ windowMs, phases: { script, style, layout, paint, composite, parse, other }, topFunctions: [{ name, url, line, selfMs }], topEvents: [{ name, durMs, phase }] }`. All ms values rounded to 1 decimal. `phases` values are self-time sums (ms). Empty/malformed trace → `{ windowMs: 0, phases: all-zero, topFunctions: [], topEvents: [] }` (never throws).
- Semantics:
  - Window = [ts of `startMark` event, ts of `endMark` event] from `blink.user_timing` marks (`name === mark`, phase `"R"`). Missing marks → whole trace span.
  - Self-time: events with `ph === "X"` and `dur > 0`, nested sweep (child durations subtracted from parents).
  - `topEvents`: top 10 by `dur` within window, mapped to phase.
  - `topFunctions`: from `Profile`/`ProfileChunk` events — join `cpuProfile.samples` + `timeDeltas` (µs) to `nodes[].callFrame`, sum sample time inside window, top 10.

- [ ] **Step 1: Write the failing test (fixture trace)**

`scripts/perf/traceParser.test.mjs`:

```js
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
```

Fixture arithmetic: `Profile.startTime = 900_000` µs; each `timeDelta` is 100_000 µs (100 ms), so the 10 samples land at 1000, 1100, …, 1900 ms — all inside the [1000 ms, 2000 ms) window. Node 2 (`hotFn`) owns 9 samples → 900 ms; node 1 owns 1 → 100 ms. Script self-time: the outer `FunctionCall` (300 ms) contains `UpdateLayoutTree` (100 ms) and `Layout` (80 ms) as children (their `ts` fall inside its [1000, 1300) ms span), so script self = 300 − 180 = 120 ms; `Paint` and `CompositeLayers` start after the FunctionCall ends and are top-level. The second `FunctionCall` (ts 2500 ms) is outside the window and must be ignored.

- [ ] **Step 2: Run test to verify it fails**

Run: `node --test scripts/perf/traceParser.test.mjs`
Expected: FAIL — module missing.

- [ ] **Step 3: Write `scripts/perf/traceParser.mjs`**

```js
const PHASE_BY_NAME = {
  FunctionCall: "script",
  EvaluateScript: "script",
  "v8.compile": "script",
  V8Execute: "script",
  TimerInstall: "script",
  TimerFire: "script",
  EventDispatch: "script",
  FireAnimationFrame: "script",
  CancelAnimationFrame: "script",
  RunMicrotasks: "script",
  PromiseThen: "script",
  MajorGC: "script",
  MinorGC: "script",
  GCEvent: "script",
  AsyncTask: "script",
  UpdateLayoutTree: "style",
  RecalculateStyles: "style",
  InvalidateStyles: "style",
  Layout: "layout",
  LayoutInvalidationTracking: "layout",
  ParseHTML: "parse",
  ParseAuthorStyleSheet: "parse",
  Paint: "paint",
  PrePaint: "paint",
  UpdateLayerTree: "paint",
  Layerize: "paint",
  PaintImage: "paint",
  DecodeImage: "paint",
  ResizeImage: "paint",
  CompositeLayers: "composite"
};

const EMPTY_PHASES = {
  script: 0,
  style: 0,
  layout: 0,
  paint: 0,
  composite: 0,
  parse: 0,
  other: 0
};

const EMPTY_RESULT = () => ({
  windowMs: 0,
  phases: { ...EMPTY_PHASES },
  topFunctions: [],
  topEvents: []
});

function markWindow(events, startMark, endMark) {
  let start = null;
  let end = null;
  for (const e of events) {
    if (e.ph !== "R") continue;
    if (e.name === startMark && start === null) start = e.ts;
    if (e.name === endMark && end === null) end = e.ts;
  }
  return { start, end };
}

function selfTimes(events, start, end) {
  const inWindow = events
    .filter(
      (e) =>
        e.ph === "X" && typeof e.dur === "number" && e.dur > 0 && e.ts + e.dur > start && e.ts < end
    )
    .sort((a, b) => a.ts - b.ts || b.dur - a.dur);
  const phases = { ...EMPTY_PHASES };
  const all = [];
  const stack = [];
  for (const e of inWindow) {
    while (stack.length && stack[stack.length - 1].ts + stack[stack.length - 1].dur <= e.ts) {
      stack.pop();
    }
    if (stack.length) {
      const parent = stack[stack.length - 1];
      parent.childDur = (parent.childDur || 0) + e.dur;
    }
    e.childDur = e.childDur || 0;
    stack.push(e);
    all.push(e);
  }
  for (const e of all) {
    const phase = PHASE_BY_NAME[e.name] || "other";
    phases[phase] += Math.max(0, e.dur - (e.childDur || 0));
  }
  return { phases, all };
}

function parseProfiles(events, start, end) {
  const nodes = new Map();
  const samples = [];
  const deltas = [];
  let baseTime = 0;
  for (const e of events) {
    const data = e?.args?.data;
    if (!data) continue;
    if (e.name === "Profile") {
      baseTime = Number(data.startTime || 0);
      for (const n of data.nodes || []) nodes.set(n.id, n);
    } else if (e.name === "ProfileChunk") {
      const cpu = data.cpuProfile || {};
      for (const n of cpu.nodes || []) nodes.set(n.id, n);
      for (const s of cpu.samples || []) samples.push(s);
      for (const d of data.timeDeltas || []) deltas.push(Number(d) || 0);
    }
  }
  if (!samples.length) return [];
  const selfMs = new Map();
  let ts = baseTime;
  for (let i = 0; i < samples.length; i++) {
    ts += deltas[i] || 0;
    if (ts < start || ts >= end) continue;
    const nodeId = samples[i];
    if (!nodes.has(nodeId)) continue;
    const frame = nodes.get(nodeId).callFrame || {};
    const key = `${frame.functionName || "(anonymous)"}@${frame.url || ""}:${frame.lineNumber ?? -1}`;
    selfMs.set(key, (selfMs.get(key) || 0) + (deltas[i] || 0) / 1000);
  }
  return [...selfMs.entries()]
    .map(([key, ms]) => {
      const [name, rest] = key.split("@");
      const [url, line] = rest.split(":");
      return { name, url, line: Number(line) + 1, selfMs: Math.round(ms * 10) / 10 };
    })
    .sort((a, b) => b.selfMs - a.selfMs)
    .slice(0, 10);
}

export function parseTrace(
  traceJson,
  { startMark = "nuvio:step:start", endMark = "nuvio:step:end" } = {}
) {
  const events = Array.isArray(traceJson?.traceEvents) ? traceJson.traceEvents : [];
  if (!events.length) return EMPTY_RESULT();
  const { start, end } = markWindow(events, startMark, endMark);
  const windowStart =
    start ?? Math.min(...events.filter((e) => typeof e.ts === "number").map((e) => e.ts));
  const windowEnd =
    end ??
    Math.max(...events.filter((e) => typeof e.ts === "number").map((e) => e.ts + (e.dur || 0)));
  if (!Number.isFinite(windowStart) || !Number.isFinite(windowEnd) || windowEnd <= windowStart) {
    return EMPTY_RESULT();
  }
  const { phases, all } = selfTimes(events, windowStart, windowEnd);
  const topEvents = all
    .slice()
    .sort((a, b) => b.dur - a.dur)
    .slice(0, 10)
    .map((e) => ({
      name: e.name,
      durMs: Math.round(e.dur / 100) / 10,
      phase: PHASE_BY_NAME[e.name] || "other"
    }));
  const rounded = Object.fromEntries(
    Object.entries(phases).map(([k, v]) => [k, Math.round(v / 100) / 10])
  );
  return {
    windowMs: Math.round((windowEnd - windowStart) / 100) / 10,
    phases: rounded,
    topFunctions: parseProfiles(events, windowStart, windowEnd),
    topEvents
  };
}
```

- [ ] **Step 4: Run tests; reconcile fixture numbers**

Run: `node --test scripts/perf/traceParser.test.mjs`
Expected: PASS with the fixture arithmetic above. If an assertion disagrees, fix the **fixture**, not the parser — the invariants (out-of-window samples/events ignored, correct frame join, 1-based line, children subtracted from parents) must hold.

- [ ] **Step 5: Commit**

```bash
git add scripts/perf/traceParser.mjs scripts/perf/traceParser.test.mjs
git commit -m "Perf harness: trace parser with phase split and cpu-profile join"
```

---

### Task 6: Aggregation

**Files:**

- Create: `scripts/perf/aggregate.mjs`
- Test: `scripts/perf/aggregate.test.mjs`

**Interfaces:**

- Produces:
  - `percentile(values, p)` → number (nearest-rank on a copy).
  - `aggregateLatencies(reps, segment)` → `{ n, p50, p95, max }` over each rep's `segments[segment].latency.summary.p50` (one p50 per rep; see Task 8 for how `latency` is built).
  - `selectMedianRep(stepReps, segment)` → rep object (rep whose segment p50 is the median).
  - `aggregateStep(step)` → `{ id, status, segments: { [segment]: { n, p50, p95, max, phases, topFunctions, topEvents, longtaskCount, longtaskMax } } }` — phase/stack data copied from the median rep.

- [ ] **Step 1: Write the failing test**

`scripts/perf/aggregate.test.mjs`:

```js
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `node --test scripts/perf/aggregate.test.mjs`
Expected: FAIL — module missing.

- [ ] **Step 3: Write `scripts/perf/aggregate.mjs`**

```js
export function percentile(values, p) {
  if (!Array.isArray(values) || !values.length) return 0;
  const sorted = [...values].sort((a, b) => a - b);
  const idx = Math.min(Math.ceil((p / 100) * sorted.length) - 1, sorted.length - 1);
  return sorted[idx];
}

function segmentP50(rep, segment) {
  return rep?.segments?.[segment]?.latency?.summary?.p50 ?? 0;
}

export function aggregateLatencies(reps, segment) {
  const values = reps.map((r) => segmentP50(r, segment)).filter((v) => v > 0);
  return {
    n: values.length,
    p50: percentile(values, 50),
    p95: percentile(values, 95),
    max: values.length ? Math.max(...values) : 0
  };
}

export function selectMedianRep(reps, segment) {
  const sorted = [...reps].sort((a, b) => segmentP50(a, segment) - segmentP50(b, segment));
  return sorted[Math.floor(sorted.length / 2)] || reps[0] || null;
}

export function aggregateStep(step) {
  const segments = {};
  const segmentNames = new Set();
  for (const r of step.reps || []) {
    Object.keys(r.segments || {}).forEach((s) => segmentNames.add(s));
  }
  for (const name of segmentNames) {
    const median = selectMedianRep(step.reps || [], name);
    const seg = median?.segments?.[name] || {};
    const longtasks = step.reps?.flatMap((r) => r.segments?.[name]?.longtasks || []) || [];
    segments[name] = {
      ...aggregateLatencies(step.reps || [], name),
      phases: seg.phases || {},
      topFunctions: seg.topFunctions || [],
      topEvents: seg.topEvents || [],
      longtaskCount: longtasks.length,
      longtaskMax: longtasks.length ? Math.max(...longtasks.map((l) => l.duration)) : 0
    };
  }
  return { id: step.id, status: step.status, segments };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `node --test scripts/perf/aggregate.test.mjs`
Expected: 4 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add scripts/perf/aggregate.mjs scripts/perf/aggregate.test.mjs
git commit -m "Perf harness: rep aggregation with median-rep attribution"
```

---

### Task 7: Interaction steps

**Files:**

- Create: `scripts/perf/steps.mjs`
- Test: `scripts/perf/steps.test.mjs`

**Interfaces:**

- Consumes: `measureSegment` (Task 3), `startTrace`/`collectTrace` (Task 4), `parseTrace` (Task 5).
- Produces:
  - `STEP_IDS` (array of 7 ids below), `resolveSteps(spec)` → array (`"all"`/undefined → all; `"smoke"` → `["home_dpad_row"]`; comma list filtered to known ids — unknown ids throw).
  - `KEY_GAP_MS = 120` ( pacing between key presses).
  - `waitForAppReady(page, timeoutMs = 30000)` — boot overlay gone + focusables present.
  - `focusTo(page, selector, { maxPresses = 60 } = {})` — keyboard-only target acquisition: alternates ArrowRight/ArrowDown, pressing Enter on `[data-zone="content"]`-blocked sections is NOT attempted; returns `true`/`false`.
  - `focusedInfo(page)` → `{ selector, row }` helpers used in success checks.
  - `runStep(page, cdp, stepId, { reps = 5, traceDir = null })` → `{ id, status: "ok"|"failed"|"skipped", reps: [ { segments: { [name]: SegmentResult } } ], error? }` where SegmentResult = `{ latency: { summary, entries }, phases, topFunctions, topEvents, longtasks }` (latency entries = `summarizeEventTimings` output + `raf` fallback array).
  - The segment names per step: `main` everywhere except `transition_detail`/`transition_settings` which use `forward` + `back`.

- [ ] **Step 1: Write the failing test for `resolveSteps`**

`scripts/perf/steps.test.mjs`:

```js
import { test } from "node:test";
import assert from "node:assert/strict";
import { resolveSteps, STEP_IDS } from "./steps.mjs";

test("all is the default and covers the seven protocol steps", () => {
  assert.equal(STEP_IDS.length, 7);
  assert.deepEqual(resolveSteps(undefined), STEP_IDS);
  assert.deepEqual(resolveSteps("all"), STEP_IDS);
});

test("smoke is one rep of home d-pad", () => {
  assert.deepEqual(resolveSteps("smoke"), ["home_dpad_row"]);
});

test("explicit lists are filtered in order", () => {
  assert.deepEqual(resolveSteps("transition_settings,home_dpad_row"), [
    "transition_settings",
    "home_dpad_row"
  ]);
});

test("unknown ids throw", () => {
  assert.throws(() => resolveSteps("nope"));
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `node --test scripts/perf/steps.test.mjs`
Expected: FAIL — module missing.

- [ ] **Step 3: Write `scripts/perf/steps.mjs`**

```js
import { measureSegment, summarizeEventTimings } from "./timing.mjs";
import { startTrace, collectTrace, saveTraceGz } from "./trace.mjs";
import { parseTrace } from "./traceParser.mjs";

export const KEY_GAP_MS = 120;

export const STEP_IDS = [
  "home_dpad_row",
  "home_dpad_rows",
  "grid_seeall",
  "grid_library",
  "transition_detail",
  "transition_settings",
  "settings_theme_toggle"
];

export function resolveSteps(spec) {
  if (!spec || spec === "all") return [...STEP_IDS];
  if (spec === "smoke") return ["home_dpad_row"];
  const requested = String(spec)
    .split(",")
    .map((s) => s.trim())
    .filter(Boolean);
  const unknown = requested.filter((s) => !STEP_IDS.includes(s));
  if (unknown.length) throw new Error(`Unknown steps: ${unknown.join(", ")}`);
  return requested;
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

export async function waitForAppReady(page, timeoutMs = 30000) {
  await page.waitForFunction(
    () =>
      document.body &&
      document.body.children.length > 0 &&
      !document.querySelector(".boot-guard-overlay") &&
      Boolean(document.querySelector(".focusable")),
    { timeout: timeoutMs }
  );
  await sleep(1500);
}

async function press(page, key) {
  await page.keyboard.press(key);
  await sleep(KEY_GAP_MS);
}

async function isFocused(page, selector) {
  return page.evaluate((sel) => {
    const el = document.querySelector(sel);
    return Boolean(el && el.classList.contains("focused"));
  }, selector);
}

export async function focusTo(page, selector, { maxPresses = 60 } = {}) {
  if (await isFocused(page, selector)) return true;
  for (let i = 0; i < maxPresses; i++) {
    await press(page, i % 2 === 0 ? "ArrowRight" : "ArrowDown");
    if (await isFocused(page, selector)) return true;
  }
  return false;
}

async function focusedKey(page) {
  return page.evaluate(() => {
    const el = document.querySelector(".focusable.focused");
    if (!el) return null;
    const row = el.closest("[data-row-key]");
    return `${el.getAttribute("data-focus-key") || el.tagName}@${row?.getAttribute("data-row-key") || ""}`;
  });
}

async function countOf(page, selector) {
  return page.evaluate((sel) => document.querySelectorAll(sel).length, selector);
}

async function buildSegment(page, cdp, name, fn, traceDir, stepId, repIndex) {
  let payload;
  let traceJson = null;
  await startTrace(cdp);
  try {
    payload = await measureSegment(page, name, fn);
    traceJson = await collectTrace(cdp);
  } catch (error) {
    try {
      await collectTrace(cdp);
    } catch (_) {}
    throw error;
  }
  let parsed = null;
  try {
    parsed = parseTrace(traceJson);
  } catch (_) {
    parsed = null;
  }
  if (traceDir && traceJson && repIndex >= 0) {
    await saveTraceGz(traceDir, `${stepId}_${name}`, repIndex, traceJson).catch(() => {});
  }
  return {
    latency: { summary: summarizeEventTimings(payload.eventTimings), raf: payload.rafLatencies },
    phases: parsed?.phases || {},
    topFunctions: parsed?.topFunctions || [],
    topEvents: parsed?.topEvents || [],
    longtasks: payload.longtasks,
    eventTimingSupported: payload.eventTimingSupported
  };

async function runReps(page, cdp, stepId, repCount, repFn) {
  const reps = [];
  for (let i = 0; i < repCount + 1; i++) {
    const warmup = i === 0;
    const segments = await repFn(warmup ? -1 : i, warmup);
    if (!warmup) reps.push({ index: i, segments });
  }
  return { id: stepId, status: "ok", reps };
}

const STEPS = {
  async home_dpad_row(page, cdp, { reps, traceDir }) {
    await page.goto(appUrl(page), { waitUntil: "domcontentloaded" });
    await waitForAppReady(page);
    await focusTo(page, ".home-poster-card, .home-continue-card, .home-hero-card");
    return runReps(page, cdp, "home_dpad_row", reps, async (i, warmup) => {
      const segments = {};
      segments.main = await buildSegment(
        page,
        cdp,
        "main",
        async () => {
          const before = await focusedKey(page);
          for (let k = 0; k < 10; k++) await press(page, "ArrowRight");
          const after = await focusedKey(page);
          if (!warmup && before === after) throw new Error("focus did not move");
        },
        traceDir,
        "home_dpad_row",
        i
      );
      return segments;
    });
  },

  async home_dpad_rows(page, cdp, { reps, traceDir }) {
    await page.goto(appUrl(page), { waitUntil: "domcontentloaded" });
    await waitForAppReady(page);
    await focusTo(page, ".home-poster-card, .home-continue-card, .home-hero-card");
    return runReps(page, cdp, "home_dpad_rows", reps, async (i, warmup) => {
      const segments = {};
      segments.main = await buildSegment(
        page,
        cdp,
        "main",
        async () => {
          const before = await focusedKey(page);
          for (let k = 0; k < 5; k++) await press(page, "ArrowDown");
          const after = await focusedKey(page);
          if (!warmup && before === after) throw new Error("focus row did not change");
        },
        traceDir,
        "home_dpad_rows",
        i
      );
      return segments;
    });
  },

  async grid_seeall(page, cdp, { reps, traceDir }) {
    await page.goto(appUrl(page), { waitUntil: "domcontentloaded" });
    await waitForAppReady(page);
    if (!(await focusTo(page, ".home-seeall-card"))) {
      return { id: "grid_seeall", status: "skipped", reps: [], error: "no see-all card" };
    }
    await press(page, "Enter");
    await page.waitForSelector(".seeall-grid", { timeout: 20000 });
    await sleep(2500);
    return runReps(page, cdp, "grid_seeall", reps, async (i, warmup) => {
      const segments = {};
      segments.main = await buildSegment(
        page,
        cdp,
        "main",
        async () => {
          const before = await countOf(page, ".seeall-card");
          for (let k = 0; k < 14; k++) await press(page, "ArrowDown");
          await page
            .waitForFunction(
              (prev) => document.querySelectorAll(".seeall-card").length > prev,
              before,
              { timeout: 15000 }
            )
            .catch(() => {});
          const after = await countOf(page, ".seeall-card");
          if (!warmup && after <= before) throw new Error("pagination did not append cards");
        },
        traceDir,
        "grid_seeall",
        i
      );
      return segments;
    });
  },

  async grid_library(page, cdp, { reps, traceDir }) {
    await page.goto(appUrl(page), { waitUntil: "domcontentloaded" });
    await waitForAppReady(page);
    await press(page, "ArrowLeft");
    if (!(await focusTo(page, '[data-action="gotoLibrary"]'))) {
      return { id: "grid_library", status: "skipped", reps: [], error: "sidebar unreachable" };
    }
    await press(page, "Enter");
    const empty = await page
      .waitForSelector(".library-grid, .library-empty-state", { timeout: 20000 })
      .then(() => page.$(".library-empty-state"));
    if (empty) {
      return { id: "grid_library", status: "skipped", reps: [], error: "library empty" };
    }
    await sleep(2000);
    await focusTo(page, ".library-grid-card");
    return runReps(page, cdp, "grid_library", reps, async (i, warmup) => {
      const segments = {};
      segments.main = await buildSegment(
        page,
        cdp,
        "main",
        async () => {
          const before = await focusedKey(page);
          for (let k = 0; k < 10; k++) await press(page, "ArrowDown");
          for (let k = 0; k < 4; k++) await press(page, "ArrowRight");
          const after = await focusedKey(page);
          if (!warmup && before === after) throw new Error("library focus did not move");
        },
        traceDir,
        "grid_library",
        i
      );
      return segments;
    });
  },

  async transition_detail(page, cdp, { reps, traceDir }) {
    await page.goto(appUrl(page), { waitUntil: "domcontentloaded" });
    await waitForAppReady(page);
    await focusTo(page, ".home-poster-card, .home-continue-card");
    return runReps(page, cdp, "transition_detail", reps, async (i) => {
      const segments = {};
      segments.forward = await buildSegment(
        page,
        cdp,
        "forward",
        async () => {
          await press(page, "Enter");
          await page.waitForSelector(".series-detail-shell, .movie-detail-shell", {
            timeout: 20000
          });
        },
        traceDir,
        "transition_detail",
        i
      );
      segments.back = await buildSegment(
        page,
        cdp,
        "back",
        async () => {
          await press(page, "Escape");
          await page.waitForSelector(".home-shell.home-screen-shell", { timeout: 20000 });
        },
        traceDir,
        "transition_detail",
        i
      );
      await sleep(800);
      return segments;
    });
  },

  async transition_settings(page, cdp, { reps, traceDir }) {
    await page.goto(appUrl(page), { waitUntil: "domcontentloaded" });
    await waitForAppReady(page);
    return runReps(page, cdp, "transition_settings", reps, async (i) => {
      const segments = {};
      segments.forward = await buildSegment(
        page,
        cdp,
        "forward",
        async () => {
          await press(page, "ArrowLeft");
          if (!(await focusTo(page, '[data-action="gotoSettings"]'))) {
            throw new Error("settings sidebar item unreachable");
          }
          await press(page, "Enter");
          await page.waitForSelector(".settings-sidebar", { timeout: 20000 });
        },
        traceDir,
        "transition_settings",
        i
      );
      segments.back = await buildSegment(
        page,
        cdp,
        "back",
        async () => {
          await press(page, "Escape");
          await page.waitForSelector(".home-shell.home-screen-shell", { timeout: 20000 });
        },
        traceDir,
        "transition_settings",
        i
      );
      await sleep(800);
      return segments;
    });
  },

  async settings_theme_toggle(page, cdp, { reps, traceDir }) {
    await page.goto(appUrl(page), { waitUntil: "domcontentloaded" });
    await waitForAppReady(page);
    await press(page, "ArrowLeft");
    if (!(await focusTo(page, '[data-action="gotoSettings"]'))) {
      return { id: "settings_theme_toggle", status: "skipped", reps: [], error: "settings unreachable" };
    }
    await press(page, "Enter");
    await page.waitForSelector(".settings-sidebar", { timeout: 20000 });
    await sleep(1500);
    if (!(await focusTo(page, '[data-focus-key^="appearance:theme:"]'))) {
      return {
        id: "settings_theme_toggle",
        status: "skipped",
        reps: [],
        error: "theme card unreachable"
      };
    }
    return runReps(page, cdp, "settings_theme_toggle", reps, async (i, warmup) => {
      const segments = {};
      segments.main = await buildSegment(
        page,
        cdp,
        "main",
        async () => {
          const before = await page.evaluate(() =>
            document.documentElement.style.getPropertyValue("--bg-color")
          );
          await press(page, "ArrowRight");
          await press(page, "Enter");
          const after = await page.evaluate(() =>
            document.documentElement.style.getPropertyValue("--bg-color")
          );
          if (!warmup && before === after) throw new Error("theme did not change");
        },
        traceDir,
        "settings_theme_toggle",
        i
      );
      return segments;
    });
  }
};

function appUrl(page) {
  return page.url().split("#")[0];
}

export async function runStep(page, cdp, stepId, options = {}) {
  const fn = STEPS[stepId];
  if (!fn) throw new Error(`Unknown step: ${stepId}`);
  try {
    return await fn(page, cdp, options);
  } catch (error) {
    return { id: stepId, status: "failed", reps: [], error: String(error?.message || error) };
  }
}
```

Note: each rep of `home_dpad_row` presses ArrowRight 10× starting from wherever the previous rep ended — that is intentional (position advances), the success check is "focus identity changed". `transition_*` reps alternate forward/back so each rep starts from home again.

- [ ] **Step 4: Run tests to verify they pass**

Run: `node --test scripts/perf/steps.test.mjs`
Expected: 4 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add scripts/perf/steps.mjs scripts/perf/steps.test.mjs
git commit -m "Perf harness: keyboard-driven interaction protocol steps"
```

---

### Task 8: Reports

**Files:**

- Create: `scripts/perf/report.mjs`
- Test: `scripts/perf/report.test.mjs`

**Interfaces:**

- Consumes: aggregated run shape (Task 6).
- Produces:
  - `renderConsoleTable(run)` → string (fixed-width columns: step, seg, n, p50, p95, script, style, layout, paint, longtasks) — `run` is `{ meta, steps: [aggregateStep results] }`.
  - `renderHtmlReport(run)` → complete HTML string (inline CSS/JS, embeds `run` as JSON; per-step card with per-segment metric row, phase bars via proportional `div`s, `<details>` lists for topFunctions/topEvents; no external assets, no server).
  - `writeRunArtifacts(runDir, run)` → writes `run.json` + `report.html`, returns their paths.

- [ ] **Step 1: Write the failing test**

`scripts/perf/report.test.mjs`:

```js
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `node --test scripts/perf/report.test.mjs`
Expected: FAIL — module missing.

- [ ] **Step 3: Write `scripts/perf/report.mjs`**

```js
import { writeFile, mkdir } from "node:fs/promises";
import path from "node:path";

const METRICS = [
  ["p50", "p50 ms"],
  ["p95", "p95 ms"],
  ["script", "script"],
  ["style", "style"],
  ["layout", "layout"],
  ["paint", "paint"]
];

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
```

(`METRICS` may end up unused — delete it if so; the table hardcodes its columns.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `node --test scripts/perf/report.test.mjs`
Expected: 2 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add scripts/perf/report.mjs scripts/perf/report.test.mjs
git commit -m "Perf harness: console, JSON and HTML reports"
```

---

### Task 9: Login + runner + compare

**Files:**

- Create: `scripts/perf/login.mjs`
- Create: `scripts/perf/runner.mjs`
- Create: `scripts/perf/compare.mjs`
- Test: `scripts/perf/compare.test.mjs`

**Interfaces:**

- Produces:
  - `login.mjs`: `runLogin(flags)` → 0 on success; launches headed context (desktop profile, no throttle) at `flags.url || "http://127.0.0.1:4173"`, waits for `.home-shell.home-screen-shell` up to 10 min, saves `context.storageState({ path: AUTH_STATE_PATH })` (mkdir first), closes.
  - `runner.mjs`: `runProtocol(flags)` → orchestrates: ensure server → require auth → launch TV-profile context with storageState → installTiming → for each resolved step: fresh `openPerfPage` → `runStep` → close page → aggregate → write artifacts → print table → exit 1 if any step failed.
  - `compare.mjs`: `compareRuns(a, b)` → `{ steps: [{ id, segment, aP50, bP50, deltaPct }], regressions: [...] }` (regression = deltaPct > +15% and aP50 > 20ms); `renderCompareHtml(diff, a, b)` → HTML (same styling approach as report); `writeCompare(dirA, dirB)` → loads both `run.json`, writes `perf-runs/compare-<A>-<B>.html`, prints table, exit 0.

- [ ] **Step 1: Write the failing compare test**

`scripts/perf/compare.test.mjs`:

```js
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `node --test scripts/perf/compare.test.mjs`
Expected: FAIL — module missing.

- [ ] **Step 3: Write `scripts/perf/compare.mjs`**

```js
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
      rows.push({ id: stepA.id, segment, aP50, bP50, deltaPct });
      if (deltaPct !== null && deltaPct > 15 && bP50 > 20) {
        regressions.push(rows[rows.length - 1]);
      }
    }
  }
  return { steps: rows, regressions };
}

export function renderCompareHtml(diff, runA, runB) {
  const rows = diff.steps
    .map(
      (r) =>
        `<tr><td>${r.id}</td><td>${r.segment}</td><td>${Math.round(r.aP50)}</td><td>${r.bP50 === null ? "—" : Math.round(r.bP50)}</td><td class="${r.deltaPct > 15 ? "fail" : ""}">${r.deltaPct === null ? "—" : Math.round(r.deltaPct) + "%"}</td></tr>`
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
      `${r.id}/${r.segment}: ${Math.round(r.aP50)}ms → ${r.bP50 === null ? "missing" : Math.round(r.bP50) + "ms"} (${r.deltaPct === null ? "n/a" : Math.round(r.deltaPct) + "%"})`
    );
  }
  console.log(`\nReport: ${out}\nRegressions >15%: ${diff.regressions.length}`);
  return 0;
}
```

- [ ] **Step 4: Run compare test**

Run: `node --test scripts/perf/compare.test.mjs`
Expected: 2 tests PASS.

- [ ] **Step 5: Write `scripts/perf/login.mjs`**

```js
import { mkdir } from "node:fs/promises";
import path from "node:path";
import { launchPerfBrowser } from "./browser.mjs";
import { AUTH_STATE_PATH } from "./paths.mjs";

export async function runLogin(flags = {}) {
  const url = flags.url || "http://127.0.0.1:4173";
  const context = await launchPerfBrowser({ tv: false, headed: true });
  const page = await context.newPage();
  console.log(
    `Sign in at ${url} (QR or dev email login), then pick a profile. Waiting up to 10 minutes...`
  );
  await page.goto(url, { waitUntil: "domcontentloaded" });
  await page.waitForSelector(".home-shell.home-screen-shell", { timeout: 600000 });
  await mkdir(path.dirname(AUTH_STATE_PATH), { recursive: true });
  await context.storageState({ path: AUTH_STATE_PATH });
  await context.browser().close();
  console.log(`Saved session to ${AUTH_STATE_PATH}`);
  return 0;
}
```

Note: `launchPerfBrowser` returns a BrowserContext; `context.browser()` gives the browser for cleanup. Verify that API path compiles in the smoke run.

- [ ] **Step 6: Write `scripts/perf/runner.mjs`**

```js
import { spawn, execSync } from "node:child_process";
import { readFile } from "node:fs/promises";
import net from "node:net";
import { ROOT_DIR, AUTH_STATE_PATH, runDirFor } from "./paths.mjs";
import { launchPerfBrowser, openPerfPage } from "./browser.mjs";
import { installTiming } from "./timing.mjs";
import { resolveSteps, runStep } from "./steps.mjs";
import { aggregateStep } from "./aggregate.mjs";
import { renderConsoleTable, writeRunArtifacts } from "./report.mjs";

function canConnect(url) {
  return new Promise((resolve) => {
    const target = new URL(url);
    const socket = net.connect({ host: target.hostname, port: Number(target.port) || 80 }, () => {
      socket.destroy();
      resolve(true);
    });
    socket.on("error", () => resolve(false));
    socket.setTimeout(1500, () => {
      socket.destroy();
      resolve(false);
    });
  });
}

async function ensureServer(url, skipBuild) {
  if (await canConnect(url)) return null;
  if (!skipBuild) {
    console.log("[perf] Building...");
    execSync("npm run build", { cwd: ROOT_DIR, stdio: "inherit" });
  }
  console.log("[perf] Starting dev server...");
  const child = spawn("npm", ["run", "serve"], { cwd: ROOT_DIR, stdio: "ignore", detached: true });
  for (let i = 0; i < 60; i++) {
    if (await canConnect(url)) return child;
    await new Promise((r) => setTimeout(r, 500));
  }
  throw new Error(`Server did not come up at ${url}`);
}

export async function runProtocol(flags = {}) {
  const url = flags.url || "http://127.0.0.1:4173";
  const reps = Number(flags.reps || 5);
  const steps = resolveSteps(typeof flags.steps === "string" ? flags.steps : undefined);
  const timestamp = new Date().toISOString().replace(/[:.]/g, "-");
  const runDir = flags.out || runDirFor(timestamp);

  const server = await ensureServer(url, Boolean(flags["skip-build"]));
  const appInfo = JSON.parse(await readFile(`${ROOT_DIR}/appinfo.json`, "utf8"));
  let gitSha = "unknown";
  try {
    gitSha = execSync("git rev-parse --short HEAD", { cwd: ROOT_DIR }).toString().trim();
  } catch (_) {}

  const authExists = await readFile(AUTH_STATE_PATH, "utf8").then(
    () => true,
    () => false
  );
  if (!authExists) {
    throw new Error(`No saved login. Run: npm run perf -- login`);
  }

  const context = await launchPerfBrowser({
    tv: !flags["no-tv"],
    headed: Boolean(flags.headed),
    storageState: AUTH_STATE_PATH
  });
  installTiming(context);

  const cpuRate = flags.cpu ? Number(flags.cpu) : 4;
  const netProfile = typeof flags.net === "string" && flags.net !== "off" ? flags.net : null;

  const run = {
    meta: {
      timestamp: new Date().toISOString(),
      appVersion: appInfo.version,
      gitSha,
      url,
      reps,
      cpuRate,
      netProfile,
      tv: !flags["no-tv"]
    },
    steps: []
  };

  let failed = false;
  for (const stepId of steps) {
    console.log(`[perf] step ${stepId}...`);
    const { page, cdp } = await openPerfPage(context, { cpuRate, netProfile });
    try {
      await page.goto(url, { waitUntil: "domcontentloaded" });
      const result = await runStep(page, cdp, stepId, { reps, traceDir: runDir });
      run.steps.push(result);
      if (result.status === "failed") failed = true;
    } finally {
      await page.close().catch(() => {});
    }
  }

  run.steps = run.steps.map((s) => (s.status === "ok" ? { ...s, agg: aggregateStep(s) } : s));
  const display = { meta: run.meta, steps: run.steps.map((s) => s.agg || s) };
  const { jsonPath, htmlPath } = await writeRunArtifacts(runDir, run);
  console.log("\n" + renderConsoleTable(display));
  console.log(`\nrun.json:   ${jsonPath}\nreport:     ${htmlPath}`);
  await context.browser().close();
  if (server) process.kill(-server.pid, "SIGTERM");
  return failed ? 1 : 0;
}
```

`renderConsoleTable` reads `step.segments` — `aggregateStep` returns exactly that shape, so `display` maps correctly. `runner.mjs`'s `run` keeps raw reps + `agg` side by side in `run.json` (raw reps stay under `reps`).

- [ ] **Step 7: Verify CLI plumbing**

Run: `npm run perf -- --help` and `node --check scripts/perf/runner.mjs` and `node --check scripts/perf/login.mjs`
Expected: usage prints; both files parse.

- [ ] **Step 8: Commit**

```bash
git add scripts/perf/login.mjs scripts/perf/runner.mjs scripts/perf/compare.mjs scripts/perf/compare.test.mjs scripts/perf/browser.mjs
git commit -m "Perf harness: login, protocol runner and run comparison"
```

---

### Task 10: End-to-end smoke (browser-verified)

**Files:**

- No new source files. Exercises everything.

**Interfaces:**

- Consumes: full CLI.

- [ ] **Step 1: One-time login (user-assisted)**

Run: `npm run perf -- login`
Expected: headed Chromium opens the app; sign in with your account, pick profile; process prints `Saved session to .../perf-runs/.auth/state.json` and exits 0. If login already works via a prior session, reuse it.

- [ ] **Step 2: Smoke run**

Run: `npm run perf -- run --steps smoke --reps 2`
Expected: exit 0; console table shows `home_dpad_row main` with `n=2` and non-zero p50; `perf-runs/<ts>/run.json` and `report.html` exist; at least one `trace-home_dpad_row_main-*.json.gz` file exists. `eventTimingSupported: true` inside run.json reps.

- [ ] **Step 3: Verify the HTML report visually**

Open `perf-runs/<ts>/report.html` in a browser (or the harness's own Chromium via a small script). Confirm: step card renders, phase bar non-empty, "top JS functions" table lists real app functions (e.g. from `homeScreen.js`), no console errors.

- [ ] **Step 4: Sanity-check attribution numbers**

In the smoke `run.json`: `phases.script + phases.style + phases.layout + phases.paint` should be < `windowMs` and > 0; `topFunctions[0].url` should end in a real bundle file. If phases are all zero while traces exist, the mark-window slicing is broken — fix `parseTrace` before proceeding.

- [ ] **Step 5: Run the full protocol**

Run: `npm run perf -- run`
Expected: all 7 steps `ok` (or explicitly `skipped` with reason if e.g. library is empty); exit 0 unless a step genuinely failed; full table + report written.

- [ ] **Step 6: Commit any fixes discovered by the smoke run** (e.g. selector tweaks in `steps.mjs`)

```bash
git add scripts/perf/
git commit -m "Perf harness: smoke-run fixes"
```

---

### Task 11: Compare-mode smoke + docs touch

**Files:**

- Modify: `README.md` (short "Performance testing" section)

**Interfaces:**

- Consumes: two existing run dirs.

- [ ] **Step 1: Produce two runs and compare**

Run: `npm run perf -- run --steps smoke --reps 2` twice, then `npm run perf -- compare <dirA> <dirB>` with the two newest `perf-runs/<timestamp>` dirs.
Expected: per-step delta lines, `compare-<A>-<B>.html` written, exit 0.

- [ ] **Step 2: Add README section**

In `README.md`, after the development/scripts section, add:

````markdown
## Performance testing

UI performance harness (desktop Chromium emulating a Tizen TV: 1080p, Tizen UA, CPU-throttled):

```bash
npm run perf -- login            # one-time: sign in with your account
npm run perf -- run              # full protocol (7 steps × 5 reps)
npm run perf -- run --steps smoke --reps 2
npm run perf -- compare perf-runs/<runA> perf-runs/<runB>
```
````

Outputs a console table, `perf-runs/<ts>/run.json` and a self-contained `report.html`
(input→paint p50/p95 per interaction, script/style/layout/paint split, top JS stacks).
See `docs/superpowers/specs/2026-08-16-ui-perf-harness-design.md`.

````

- [ ] **Step 3: Final verification sweep**

Run: `npm test` — all perf unit tests pass alongside existing suite.
Run: `npm run build` — still green.
Run: `git status` — nothing from `perf-runs/` staged.

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -m "Document UI performance harness usage"
````

---

## Self-Review Notes (resolved during planning)

- Spec coverage: removal (T1), TV profile (T2), login (T9/T10), 7 steps (T7), 3-layer measurement (T3/T4/T5), aggregation p50/p95 + median-rep attribution (T6), console/JSON/HTML reports (T8), compare (T9/T11), Tizen CDP connect — **deferred**: the spec marks it best-effort/experimental; `chromium.connectOverCDP` can be added later without touching the core (runner's `launchPerfBrowser` is the single seam). Flagged to the user at handoff.
- Selector risk: `focusTo` bounded pressing makes target acquisition robust to layout-mode differences (classic/modern home); skipped-status paths cover missing content (empty library, no see-all card).
