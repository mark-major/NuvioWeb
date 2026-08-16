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
  const child = spawn("npm", ["run", "serve"], {
    cwd: ROOT_DIR,
    stdio: "ignore",
    detached: true
  });
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

  let state = null;
  try {
    state = JSON.parse(await readFile(AUTH_STATE_PATH, "utf8"));
  } catch (_) {}
  const savedToken = state?.origins?.[0]?.localStorage?.find(
    (entry) => entry.name === "access_token" && entry.value
  );
  if (!state || !savedToken) {
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

  run.steps = run.steps.map((s) => (s.status === "ok" ? { ...s, ...aggregateStep(s) } : s));
  const display = run;
  const { jsonPath, htmlPath } = await writeRunArtifacts(runDir, run);
  console.log("\n" + renderConsoleTable(display));
  console.log(`\nrun.json:   ${jsonPath}\nreport:     ${htmlPath}`);
  await context.browser().close();
  if (server) process.kill(-server.pid, "SIGTERM");
  return failed ? 1 : 0;
}
