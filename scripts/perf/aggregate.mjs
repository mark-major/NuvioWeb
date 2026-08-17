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
      network: seg.network || null,
      longtaskCount: longtasks.length,
      longtaskMax: longtasks.length ? Math.max(...longtasks.map((l) => l.duration)) : 0
    };
  }
  return { id: step.id, status: step.status, segments };
}
