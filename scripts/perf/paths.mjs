import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
export const ROOT_DIR = path.resolve(__dirname, "..", "..");
export const RUNS_DIR = path.join(ROOT_DIR, "perf-runs");
export const AUTH_STATE_PATH = path.join(RUNS_DIR, ".auth", "state.json");

export function runDirFor(timestamp) {
  return path.join(RUNS_DIR, timestamp);
}
