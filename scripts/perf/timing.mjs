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

export function summarizeNetwork(entries) {
  if (!Array.isArray(entries) || !entries.length) {
    return { count: 0, totalMs: 0, p50Ms: 0, maxMs: 0, bytes: 0, slowest: null };
  }
  const durations = entries.map((e) => Number(e.duration) || 0).sort((a, b) => a - b);
  const bytes = entries.reduce(
    (a, e) => a + (Number(e.transferSize) || Number(e.encodedBodySize) || 0),
    0
  );
  const slowest = entries.reduce(
    (a, e) => ((Number(e.duration) || 0) > (Number(a.duration) || 0) ? e : a),
    entries[0]
  );
  return {
    count: entries.length,
    totalMs: Math.round(entries.reduce((a, e) => a + (Number(e.duration) || 0), 0)),
    p50Ms: Math.round(percentile(durations, 50)),
    maxMs: Math.round(Number(slowest.duration) || 0),
    bytes,
    slowest: slowest.name || null
  };
}

export function buildInitScript() {
  return `(() => {
  if (window.__nuvioPerf) return;
  const state = { stepId: null, stepStart: 0, events: [], longtasks: [], raf: [], resources: [], supported: false };
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
    }).observe({ type: "event", buffered: false, durationThreshold: 16 });
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
  try {
    new PerformanceObserver((list) => {
      for (const e of list.getEntries()) {
        state.resources.push({
          name: e.name,
          initiatorType: e.initiatorType || "",
          startTime: e.startTime,
          responseEnd: e.responseEnd,
          duration: e.duration,
          transferSize: e.transferSize || 0,
          encodedBodySize: e.encodedBodySize || 0,
          decodedBodySize: e.decodedBodySize || 0,
          nextHopProtocol: e.nextHopProtocol || ""
        });
      }
    }).observe({ type: "resource", buffered: true });
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
      state.resources = [];
    },
    async finishStep() {
      try { performance.mark("nuvio:step:end"); } catch (_) {}
      await new Promise((resolve) =>
        requestAnimationFrame(() => requestAnimationFrame(resolve))
      );
      const stepEnd = performance.now();
      return {
        id: state.stepId,
        stepStart: state.stepStart,
        eventTimingSupported: state.supported,
        eventTimings: state.events.slice(),
        longtasks: state.longtasks.slice(),
        rafLatencies: state.raf.slice(),
        resources: state.resources.filter(
          (r) => r.responseEnd >= state.stepStart && r.startTime <= stepEnd
        )
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
