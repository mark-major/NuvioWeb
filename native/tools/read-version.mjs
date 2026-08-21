#!/usr/bin/env node
/**
 * read-version.mjs — package.json version → tizen-manifest.xml placeholder
 * replacement (Task 19.1). Run before `dotnet build` (npm run native:build).
 */
import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const pkg = JSON.parse(await readFile(path.join(repoRoot, "package.json"), "utf8"));
const version = String(pkg.version || "1.0.0");

const manifestPath = path.join(
  repoRoot,
  "native/src/NuvioTV.Tizen/tizen-manifest.xml"
);
let manifest = await readFile(manifestPath, "utf8");
if (!manifest.includes("__NATIVE_VERSION__") && !manifest.includes(`version="${version}"`)) {
  // Replace an already-substituted older version too.
  manifest = manifest.replace(/manifest xmlns="([^"]+)"\s+version="[^"]*"/,
    `manifest xmlns="$1" version="${version}"`);
} else {
  manifest = manifest.replace("__NATIVE_VERSION__", version);
}
await writeFile(manifestPath, manifest);
console.log(`[read-version] tizen-manifest.xml version -> ${version}`);
