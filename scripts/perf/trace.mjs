import { gzipSync } from "node:zlib";
import { writeFile, mkdir } from "node:fs/promises";
import path from "node:path";

export const TRACE_CATEGORIES = [
  "devtools.timeline",
  "v8.execute",
  "disabled-by-default-v8.cpu_profiler",
  "blink.user_timing"
];

export async function startTrace(cdp) {
  await cdp.send("Tracing.start", {
    transferMode: "ReturnAsStream",
    categories: TRACE_CATEGORIES.join(","),
    options: "sampling-frequency=10000"
  });
}

export async function collectTrace(cdp) {
  const chunks = [];
  const done = new Promise((resolve, reject) => {
    cdp.on("Tracing.tracingComplete", async (event) => {
      try {
        const handle = event.stream;
        for (;;) {
          const result = await cdp.send("IO.read", { handle });
          if (result.data) chunks.push(Buffer.from(result.data, "base64"));
          if (result.eof) break;
        }
        await cdp.send("IO.close", { handle });
        resolve();
      } catch (error) {
        reject(error);
      }
    });
    cdp.send("Tracing.end").catch(reject);
  });
  await done;
  return JSON.parse(Buffer.concat(chunks).toString("utf8"));
}

export async function saveTraceGz(runDir, stepId, repIndex, trace) {
  await mkdir(runDir, { recursive: true });
  const file = path.join(runDir, `trace-${stepId}-${repIndex}.json.gz`);
  await writeFile(file, gzipSync(JSON.stringify(trace)));
  return file;
}
