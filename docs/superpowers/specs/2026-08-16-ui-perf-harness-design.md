# NuvioWeb UI Performance Harness — Design

Date: 2026-08-16
Status: Approved approach A (Playwright + CDP tracing); spec pending review

## Goal

An automated harness that drives NuvioWeb's UI interactions in desktop Chromium
under a TV-class profile, measures the speed of each UI change (input → next
paint), and attributes the time to JS work vs CSS/style/layout/paint work, so
bottlenecks in the app's JS/CSS handling can be identified and fixed with
evidence.

Replaces the in-flight perf tooling (removed as step 0; nothing else imports it):

- `scripts/perf-test-playwright.mjs`
- `scripts/perf-run.mjs`
- `scripts/perf-collect.mjs`
- `js/core/diagnostics/perfFlags.js`
- `js/core/diagnostics/perfMetrics.js`
- `PERF_IMPLEMENTATION_SPEC.md`
- `perf-runs/` (directory, incl. old results)

`test.sh`, `pia_benchmark.sh`, `pia_benchmark_results.csv` are unrelated PIA VPN
benchmarks and stay.

## Non-goals

- Selector-level CSS attribution (Chromium traces expose recalc-style totals,
  element counts, and initiating stacks — not which selectors matched).
- Automated runs on a physical TV; the Tizen-emulator connect mode is
  best-effort, not guaranteed.
- Player/OSD and cold-boot/login steps (user deselected these areas).
- Continuous FPS sampling; measurement is per-interaction instead.
- CI integration.

## Architecture

New code lives in `scripts/perf/`. Zero new npm dependencies beyond adding
`playwright` to `devDependencies` (it is already present in `node_modules`).
Single CLI entry, plain node ESM, no framework.

```
scripts/perf/
  cli.mjs          # arg parsing, command dispatch: login | run | compare
  browser.mjs      # context factory (TV profile / desktop / connect-over-CDP)
  login.mjs        # one-time interactive login → storageState
  steps.mjs        # step definitions: input script + success condition
  timing.mjs       # in-page instrumentation injected per page (Event Timing,
                   #   rAF probe, longtask observer, step markers)
  trace.mjs        # CDP Tracing start/stop/collect per rep
  traceParser.mjs  # trace JSON → phase split + top JS stacks + event list
  aggregate.mjs    # reps → p50/p95, median-rep selection
  report.mjs       # console table + run.json + self-contained report.html
  compare.mjs      # diff two runs → console + compare-*.html
```

npm script: `"perf": "node ./scripts/perf/cli.mjs"` → `npm run perf -- run`.

## TV profile (desktop Chromium)

Default profile, `--no-tv` opts out:

- Viewport 1920×1080, deviceScaleFactor 1, headless by default.
- UA spoofed to Tizen 5.5 (`SMART-TV; LINUX; Tizen 5.5` variant) — `index.html`
  auto-detects this and sets `__NUVIO_PLATFORM__ = "tizen"`, so the app runs its
  real Tizen code paths and `performance-constrained` styling.
- CPU throttling via CDP `Emulation.setCPUThrottlingRate`, default 4×
  (`--cpu N`, `1` = off).
- Optional network throttling `--net tv` (≈10 Mbps, 40 ms RTT) via
  `Network.emulateNetworkConditions`; default off.
- App URL default `http://127.0.0.1:4173`; if unreachable the harness runs
  `npm run build` then spawns `npm run serve` and waits for the port.
  `--url` / `--skip-build` override.

## Login (one-time, interactive)

`npm run perf -- login`:

1. Launch headed Chromium (no throttling) at the app URL.
2. User signs in however they like (QR flow or dev email login) and picks a
   profile; harness polls DOM until the authenticated home shell is present
   (past auth/profile-selection screens; 10-minute timeout).
3. Save `context.storageState()` to `perf-runs/.auth/state.json` (mode 0600).
   Auth tokens live in localStorage (`SessionStore`), which storageState
   captures.
4. Every `run` reuses this state; if missing, `run` fails fast with the
   `perf login` instruction. Token expiry → rerun `perf login`.

`perf-runs/` is added to `.gitignore` (auth state and traces must not be
committed).

## Protocol steps

Each step: fresh page load → app-ready wait (`body` populated, no
`.boot-guard-overlay`, focusables present) → 1 warmup rep discarded → N reps
(default 5, `--reps`). Focus is driven exclusively by keyboard (arrow keys,
Enter, Escape), matching the TV reality.

| id | area | script | success condition per rep |
|---|---|---|---|
| `home_dpad_row` | home d-pad | ArrowRight ×10 in first row | `.focusable.focused` element identity changed |
| `home_dpad_rows` | home d-pad | ArrowDown ×5 across rows | focused element's row container changed |
| `grid_seeall` | grid scroll/pagination | open See All from home, ArrowDown through `.seeall-grid` until `loadNextPage()` fires | focus moves; on page-load reps: `.seeall-card` count increased and `.seeall-loading` gone |
| `grid_library` | grid scroll/pagination | `#library` route, arrows through grid | focus moves; appended-content reps: card count grows |
| `transition_detail` | transitions | Enter on focused home card → detail, Escape → home | detail shell visible, then home shell visible |
| `transition_settings` | transitions | sidebar → settings, Escape → home | settings container visible, then home shell visible |
| `settings_theme_toggle` | settings | in settings, focus theme/appearance toggle, Enter | root theme class/attribute changed |

Step selection: `--steps id1,id2` subset; `--steps smoke` = 1 rep of
`home_dpad_row` (used for the harness's own smoke test).

Exact selectors for See All entry, sidebar navigation, and the theme toggle are
verified against the current screens during implementation (patterns:
`data-focus-key`, `data-action`, `.focusable`, hash routes `#library`
`#settings`).

## Measurement model

Per rep, three layers:

1. **Input → paint latency (page-side).** Before dispatching each key, the
   harness sets a step marker via `page.evaluate`. An injected observer uses
   the Event Timing API (`PerformanceObserver` type `event`, entries with
   `interactionId`) to record `processingStart`, `processingEnd`, and
   `duration` (≈ input → next paint) for each keydown in the rep. Fallback
   when Event Timing is unavailable (old Tizen Chromium): rAF probe — first
   `requestAnimationFrame` timestamp after dispatch minus dispatch time.
2. **Trace attribution (CDP).** `Tracing.start` with categories
   `devtools.timeline,v8.execute,disabled-by-default-v8.cpu_profiler,blink.user_timing`,
   run the rep, `Tracing.end`, collect the trace stream. Events are sliced to
   the marker window and nested by timestamp to compute self-time per phase:
   - script (FunctionCall, EvaluateScript, TimerFire, EventDispatch, GC…)
   - style recalc (UpdateLayoutTree, incl. elements-affected counts)
   - layout (Layout; events >50 ms flagged with initiating stack when present)
   - paint/composite (Paint, PrePaint, Layerize, CompositeLayers)
   - parse (ParseHTML, ParseAuthorStyleSheet)
   CPU-profile samples are joined to script frames → top functions by
   self time (name, URL, line).
3. **Context counters.** In-page `PerformanceObserver` for `longtask` within
   the rep window; forced-sync-layout heuristic is not attempted.

Rep window = first key marker to success condition observed. Trace files are
kept per rep as `trace-<step>-<rep>.json.gz` (node `zlib`) inside the run dir.

## Aggregation and reports

- Per step: p50/p95/max input→paint across reps; phase ms from the **median**
  rep's trace; top JS functions (top 10); longest timeline events (top 10);
  longtask count/max.
- `run.json`: meta (timestamp, git HEAD sha, app version from `appinfo.json`,
  URL, profile flags, UA) + steps[] with per-rep raw data + aggregates.
- Console: one table row per step — p50/p95 latency, script/style/layout/paint
  ms, longtasks. Failures printed explicitly; exit code 1 if any step failed.
- `report.html`: self-contained (inline CSS/JS, embedded run data, no server,
  no external assets). Per-step cards with rep histogram, phase stacked bar,
  top JS functions, top events. Generated next to run.json in
  `perf-runs/<timestamp>/`.
- `npm run perf -- compare <runA> <runB>`: side-by-side deltas per step/metric
  with regression highlighting; writes `perf-runs/compare-<A>-<B>.html`.

## Tizen emulator mode (best-effort)

`--cdp ws://… | http://host:port` connects via `chromium.connectOverCDP`
(e.g. Tizen TV emulator with a forwarded DevTools port). Same protocol;
feature-detected degradation (no Event Timing → rAF probe; tracing failures →
skip attribution, keep latencies). Documented as experimental; desktop profile
is the primary target.

## Error handling

- App-ready or success-condition timeout (default 15 s) marks the rep/step
  failed; remaining steps still run.
- Trace collection/parse failure warns and degrades to latency-only for that
  rep; never aborts the run.
- `perf login` timeout (10 min) exits with instructions; never stores partial
  state.

## Verification

- `scripts/perf/traceParser.test.mjs` (node --test, runs under existing
  `npm test`): fixture trace events → expected phase split, nesting, top-stack
  extraction. Deterministic, no browser.
- Smoke: `npm run perf -- run --steps smoke --reps 1` against the served app
  with a logged-in state; asserts a green table row end-to-end (browser,
  CDP trace, parse, report).
- HTML report: generate from the smoke run and open it (visual check of
  tables/bars rendering).
- Removal check: `grep -r "perfFlags\|PerfMetrics\|perfMonitor\|perfEndpoint" js/`
  returns nothing; app still builds and serves.

## Out-of-scope future hooks (not built now)

Selector-level CSS attribution via injected style-invalidation counters;
physical-TV collector mode; CI nightly runs.
