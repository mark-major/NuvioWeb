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
      Boolean(
        document.querySelector(".home-poster-card, .home-continue-card") ||
        document.querySelector(".library-empty-state") ||
        document.querySelector(".seeall-grid") ||
        document.querySelector(".settings-sidebar") ||
        document.querySelector(".series-detail-shell, .movie-detail-shell")
      ),
    { timeout: timeoutMs }
  );
  await sleep(2500);
}

async function press(page, key) {
  await page.keyboard.press(key);
  await sleep(KEY_GAP_MS);
}

async function isFocused(page, selector) {
  return page.evaluate((sel) => {
    const el = document.querySelector(sel);
    if (!el) return false;
    if (el.classList.contains("focused")) return true;
    return Array.from(document.querySelectorAll(sel)).some((node) =>
      node.classList.contains("focused")
    );
  }, selector);
}

export async function focusTo(page, selector, { maxPresses = 60 } = {}) {
  if (await isFocused(page, selector)) return true;
  for (let i = 0; i < maxPresses; i++) {
    await press(page, i % 2 === 0 ? "ArrowRight" : "ArrowDown");
    const anchored = await page.evaluate((sel) => {
      const focused = document.querySelector(".focusable.focused");
      if (!focused || !focused.matches(sel)) return false;
      const row = focused.closest("[data-nav-row], .home-track, .home-row");
      if (!row) return false;
      const cards = Array.from(row.querySelectorAll(".focusable")).filter((n) => n.matches(sel));
      const idx = cards.indexOf(focused);
      return idx >= 1 && idx <= 5;
    }, selector);
    if (anchored) return true;
  }
  return false;
}

async function focusedKey(page) {
  return page.evaluate(() => {
    const el = document.querySelector(".focusable.focused");
    if (!el) return null;
    const row = el.closest("[data-row-key]");
    const cards = Array.from(
      document.querySelectorAll(".home-poster-card, .home-continue-card, .home-hero-card")
    );
    const identity =
      el.getAttribute("data-item-id") ||
      el.getAttribute("data-focus-key") ||
      el.getAttribute("data-see-all-id") ||
      "idx:" + cards.indexOf(el) + ":" + el.className;
    return `${identity}@${row?.getAttribute("data-row-key") || ""}`;
  });
}

async function focusPosition(page) {
  return page.evaluate(() => {
    const el = document.querySelector(".focusable.focused");
    if (!el) return null;
    const row = el.closest("[data-nav-row]");
    const col = el.closest("[data-nav-col]");
    const cards = Array.from(
      document.querySelectorAll(".home-poster-card, .home-continue-card, .home-hero-card")
    );
    const identity =
      el.getAttribute("data-item-id") ||
      el.getAttribute("data-focus-key") ||
      el.getAttribute("data-see-all-id") ||
      "idx:" + cards.indexOf(el) + ":" + el.className;
    return `${identity}@r${row?.getAttribute("data-nav-row") || ""}c${col?.getAttribute("data-nav-col") || ""}`;
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
}

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
    await waitForAppReady(page);
    await focusTo(page, ".home-poster-card, .home-continue-card");
    return runReps(page, cdp, "home_dpad_row", reps, async (i, warmup) => {
      await focusTo(page, ".home-poster-card, .home-continue-card");
      const segments = {};
      segments.main = await buildSegment(
        page,
        cdp,
        "main",
        async () => {
          const before = await focusPosition(page);
          for (let k = 0; k < 10; k++) await press(page, "ArrowRight");
          const after = await focusPosition(page);
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
    await waitForAppReady(page);
    await focusTo(page, ".home-poster-card, .home-continue-card");
    return runReps(page, cdp, "home_dpad_rows", reps, async (i, warmup) => {
      await focusTo(page, ".home-poster-card, .home-continue-card");
      const segments = {};
      segments.main = await buildSegment(
        page,
        cdp,
        "main",
        async () => {
          const before = await focusPosition(page);
          for (let k = 0; k < 5; k++) await press(page, "ArrowDown");
          const after = await focusPosition(page);
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
      await focusTo(page, ".library-grid-card");
      const segments = {};
      segments.main = await buildSegment(
        page,
        cdp,
        "main",
        async () => {
          const before = await focusPosition(page);
          for (let k = 0; k < 10; k++) await press(page, "ArrowDown");
          for (let k = 0; k < 4; k++) await press(page, "ArrowRight");
          const after = await focusPosition(page);
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
    await waitForAppReady(page);
    await focusTo(page, ".home-poster-card, .home-continue-card");
    return runReps(page, cdp, "transition_detail", reps, async (i) => {
      await focusTo(page, ".home-poster-card, .home-continue-card");
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
    await waitForAppReady(page);
    await press(page, "ArrowLeft");
    if (!(await focusTo(page, '[data-action="gotoSettings"]'))) {
      return {
        id: "settings_theme_toggle",
        status: "skipped",
        reps: [],
        error: "settings unreachable"
      };
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

export async function runStep(page, cdp, stepId, { reps = 5, traceDir = null } = {}) {
  const fn = STEPS[stepId];
  if (!fn) throw new Error(`Unknown step: ${stepId}`);
  try {
    return await fn(page, cdp, { reps, traceDir });
  } catch (error) {
    return { id: stepId, status: "failed", reps: [], error: String(error?.message || error) };
  }
}
