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
