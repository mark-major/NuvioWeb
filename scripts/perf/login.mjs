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
