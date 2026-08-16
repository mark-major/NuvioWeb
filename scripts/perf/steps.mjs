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

const FOCUSED_EXPR = `(() => {
  const candidates = Array.from(document.querySelectorAll(".focusable.focused"))
    .filter((el) => el.offsetParent !== null || el === document.activeElement);
  return candidates[0] || document.activeElement || null;
})()`;

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

export async function dismissOverlays(page, maxPresses = 6) {
  for (let i = 0; i < maxPresses; i++) {
    const state = await page.evaluate(
      `(() => {
        const focused = ${FOCUSED_EXPR};
        const inSidebar = Boolean(focused?.closest(".home-sidebar, .modern-sidebar-panel"));
        const expanded = Boolean(
          document.querySelector(".home-sidebar.expanded, .modern-sidebar-shell.expanded")
        );
        return inSidebar || expanded;
      })()`
    );
    if (!state) return true;
    await press(page, "Escape");
    await sleep(400);
  }
  return false;
}

async function press(page, key) {
  await page.keyboard.press(key);
  await sleep(KEY_GAP_MS);
}

export async function focusTo(page, selector, { maxPresses = 60, escape = true } = {}) {
  for (let i = 0; i < maxPresses; i++) {
    const status = await page.evaluate(
      `(() => {
        const focused = ${FOCUSED_EXPR};
        if (!focused || !focused.matches(${JSON.stringify(selector)})) return "no";
        const row = focused.closest("[data-nav-row], .home-track, .home-row");
        if (!row) return "ok";
        const cards = Array.from(row.querySelectorAll(".focusable")).filter((n) =>
          n.matches(${JSON.stringify(selector)})
        );
        if (cards.length <= 1) return "ok";
        const idx = cards.indexOf(focused);
        return idx >= 1 && idx <= 5 ? "ok" : "late";
      })()`
    );
    if (status === "ok") return true;
    await press(
      page,
      escape
        ? ["ArrowRight", "ArrowDown", "ArrowLeft", "ArrowDown"][i % 4]
        : i % 2 === 0
          ? "ArrowRight"
          : "ArrowDown"
    );
  }
  return false;
}

export async function focusSidebarItem(page, action) {
  const atPoster = await page.evaluate(
    `(() => {
      const el = ${FOCUSED_EXPR};
      return Boolean(
        el?.matches(".home-poster-card, .home-continue-card") &&
          Number(el.dataset?.navRow || 0) >= 1
      );
    })()`
  );
  if (!atPoster) {
    await focusTo(page, ".home-poster-card, .home-continue-card");
  }
  await press(page, "ArrowLeft");
  for (let i = 0; i < 12; i++) {
    const state = await page.evaluate(
      `(() => {
        const focused = ${FOCUSED_EXPR};
        const inSidebar = Boolean(focused?.closest(".home-sidebar, .modern-sidebar-panel"));
        const match = Boolean(inSidebar && focused?.dataset?.action === ${JSON.stringify(action)});
        const idx = Number(focused?.dataset?.navIndex || 0);
        const target = document.querySelector(
          ".home-sidebar .focusable[data-action=" +
            ${JSON.stringify(action)} +
            "], .modern-sidebar-panel .focusable[data-action=" +
            ${JSON.stringify(action)} +
            "]"
        );
        const targetIdx = Number(target?.dataset?.navIndex || 0);
        return { match, idx, targetIdx };
      })()`
    );
    if (state.match) return true;
    if (state.idx < state.targetIdx) {
      await press(page, "ArrowDown");
    } else if (state.idx > state.targetIdx) {
      await press(page, "ArrowUp");
    } else if (!state.match) {
      await press(page, "ArrowUp");
    }
  }
  return false;
}

async function enterContentFromSidebar(page) {
  for (let i = 0; i < 4; i++) {
    const inSidebar = await page.evaluate(
      `(() => {
        const el = ${FOCUSED_EXPR};
        return Boolean(el?.closest(".home-sidebar, .modern-sidebar-panel"));
      })()`
    );
    if (!inSidebar) return true;
    await press(page, "ArrowRight");
    await sleep(400);
  }
  return false;
}

async function focusedKey(page) {
  return page.evaluate(
    `(() => {
      const el = ${FOCUSED_EXPR};
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
      return identity + "@" + (row?.getAttribute("data-row-key") || "");
    })()`
  );
}

async function focusPosition(page) {
  return page.evaluate(
    `(() => {
      const el = ${FOCUSED_EXPR};
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
      return (
        identity +
        "@r" +
        (row?.getAttribute("data-nav-row") || "") +
        "c" +
        (col?.getAttribute("data-nav-col") || "")
      );
    })()`
  );
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
    await dismissOverlays(page);
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
    await dismissOverlays(page);
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
    await dismissOverlays(page);
    let parked = await focusTo(page, ".home-seeall-card", { escape: false });
    if (!parked) {
      await dismissOverlays(page);
      parked = await focusTo(page, ".home-seeall-card", { escape: false });
    }
    if (!parked) {
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
    await dismissOverlays(page);
    if (!(await focusSidebarItem(page, "gotoLibrary"))) {
      return { id: "grid_library", status: "skipped", reps: [], error: "sidebar unreachable" };
    }
    await press(page, "Enter");
    const empty = await page
      .waitForSelector(".library-grid, .library-empty-state", { timeout: 20000 })
      .then(() => page.$(".library-empty-state"));
    if (empty) {
      return { id: "grid_library", status: "skipped", reps: [], error: "library empty" };
    }
    await enterContentFromSidebar(page);
    await sleep(2000);
    if (!(await focusTo(page, ".library-grid-card"))) {
      for (let k = 0; k < 12; k++) await press(page, "ArrowDown");
    }
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
    await dismissOverlays(page);
    await focusTo(page, '[data-action="openDetail"].home-poster-card, .home-continue-card');
    return runReps(page, cdp, "transition_detail", reps, async (i) => {
      await focusTo(page, '[data-action="openDetail"].home-poster-card, .home-continue-card');
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
    await dismissOverlays(page);
    return runReps(page, cdp, "transition_settings", reps, async (i) => {
      const segments = {};
      segments.forward = await buildSegment(
        page,
        cdp,
        "forward",
        async () => {
          if (!(await focusSidebarItem(page, "gotoSettings"))) {
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
    await dismissOverlays(page);
    if (!(await focusSidebarItem(page, "gotoSettings"))) {
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
    await enterContentFromSidebar(page);
    if (!(await focusTo(page, '[data-focus-key^="appearance:theme:"]'))) {
      return {
        id: "settings_theme_toggle",
        status: "skipped",
        reps: [],
        error: "theme card unreachable"
      };
    }
    return runReps(page, cdp, "settings_theme_toggle", reps, async (i, warmup) => {
      await enterContentFromSidebar(page);
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
