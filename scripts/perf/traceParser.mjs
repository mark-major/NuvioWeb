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
  const childDur = new Map();
  const stack = [];
  for (const e of inWindow) {
    while (stack.length && stack[stack.length - 1].ts + stack[stack.length - 1].dur <= e.ts) {
      stack.pop();
    }
    if (stack.length) {
      const parent = stack[stack.length - 1];
      childDur.set(parent, (childDur.get(parent) || 0) + e.dur);
    }
    stack.push(e);
  }
  for (const e of inWindow) {
    const phase = PHASE_BY_NAME[e.name] || "other";
    phases[phase] += Math.max(0, e.dur - (childDur.get(e) || 0));
  }
  return { phases, all: inWindow };
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
  const perNode = new Map();
  let ts = baseTime;
  for (let i = 0; i < samples.length; i++) {
    ts += deltas[i] || 0;
    if (ts < start || ts >= end) continue;
    const nodeId = samples[i];
    if (!nodes.has(nodeId)) continue;
    perNode.set(nodeId, (perNode.get(nodeId) || 0) + (deltas[i] || 0) / 1000);
  }
  const merged = new Map();
  for (const [nodeId, ms] of perNode) {
    const frame = nodes.get(nodeId).callFrame || {};
    const name = frame.functionName || "(anonymous)";
    const url = frame.url || "";
    const line = Number(frame.lineNumber ?? -1);
    const key = `${name}\u0000${url}\u0000${line}`;
    const prev = merged.get(key);
    if (prev) {
      prev.ms += ms;
    } else {
      merged.set(key, { name, url, line, ms });
    }
  }
  return [...merged.values()]
    .map(({ name, url, line, ms }) => ({
      name,
      url,
      line: line + 1,
      selfMs: Math.round(ms * 10) / 10
    }))
    .sort((a, b) => b.selfMs - a.selfMs)
    .slice(0, 10);
}

export function parseTrace(
  traceJson,
  { startMark = "nuvio:step:start", endMark = "nuvio:step:end" } = {}
) {
  const raw = Array.isArray(traceJson?.traceEvents) ? traceJson.traceEvents : [];
  const events = raw.filter((e) => e && typeof e === "object");
  if (!events.length) return EMPTY_RESULT();
  const { start, end } = markWindow(events, startMark, endMark);
  let minTs = Infinity;
  let maxEnd = -Infinity;
  for (const e of events) {
    if (typeof e.ts !== "number") continue;
    if (e.ts < minTs) minTs = e.ts;
    const eventEnd = e.ts + (e.dur || 0);
    if (eventEnd > maxEnd) maxEnd = eventEnd;
  }
  const windowStart = start ?? minTs;
  const windowEnd = end ?? maxEnd;
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
