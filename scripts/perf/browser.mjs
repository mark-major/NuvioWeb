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
