#!/usr/bin/env node
/**
 * convert-strings.mjs
 *
 * Converts the Android res/values*  /strings.xml sources (webapp format:
 * `<resources><string name="key">text</string></resources>`) into one flat
 * JSON message catalog per locale:
 *
 *   native/src/NuvioTV.Tizen/Resources/i18n/{locale}.json
 *
 * Locale is derived from the directory name: `res/values` -> `en`,
 * `res/values-es-419` -> `es-419`, etc. Output JSON is UTF-8, keys sorted,
 * flat `{ "key": "string" }`, written with a trailing newline.
 *
 * Zero dependencies: plain Node, regex parsing (mirrors the hand-rolled
 * parsing the webapp boot guard uses — no DOM parser available in Node).
 *
 * Escape decoding mirrors the webapp loader (js/i18n/index.js and
 * boot-guard.js), applied in the same order as the loader:
 *
 *   1. XML entity decode (what DOMParser#textContent produces):
 *      &amp; &lt; &gt; &quot; &apos; &#39;
 *   2. \uXXXX unicode escapes — the loader's decodeUnicodeEscapes regex
 *      `/\\u([0-9a-fA-F]{4})/g -> String.fromCharCode`. Note this runs on
 *      the raw text BEFORE any backslash-escape decoding, so a source
 *      `\\u2026` decodes to `\` + U+2026, exactly as the webapp renders it.
 *   3. Backslash escapes — `\'` -> `'` and `\"` -> `"` (decoded by the
 *      loader's interpolate, after parse-time unicode decoding), plus
 *      `\n` -> newline, `\t` -> tab, `\\` -> backslash. `\\` is decoded
 *      last so `\\n` stays a literal `\n`.
 *
 * Interpolation placeholders (%1$s, %1$d, %s, {{name}}) are NOT decoded
 * here — they survive verbatim for the runtime I18n layer to substitute.
 * `<string>` entries without a `name` attribute are skipped; entries with
 * an empty body yield an empty string value.
 */

import { readdirSync, readFileSync, mkdirSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const RES_DIR = join(REPO_ROOT, "res");
const OUT_DIR = join(REPO_ROOT, "native/src/NuvioTV.Tizen/Resources/i18n");

/** One <string ...>…</string> element; text may span lines ([\s\S]). */
const STRING_TAG_RE = /<string\b([^>]*)>([\s\S]*?)<\/string>/g;
/** name attribute, double- or single-quoted. */
const NAME_ATTR_RE = /\bname\s*=\s*(?:"([^"]*)"|'([^']*)')/;

const ENTITY_MAP = {
  amp: "&",
  lt: "<",
  gt: ">",
  quot: '"',
  apos: "'",
  "#39": "'",
};

/** Single-pass entity decode — never re-scans replacement text (matches
 *  DOMParser#textContent, which also decodes entities in one pass). */
function decodeXmlEntities(value) {
  return value.replace(/&(amp|lt|gt|quot|apos|#39);/g, (match, entity) => {
    const replacement = ENTITY_MAP[entity];
    return replacement === undefined ? match : replacement;
  });
}

/** Byte-for-byte the webapp's decodeUnicodeEscapes (js/i18n/index.js). */
function decodeUnicodeEscapes(value) {
  return value.replace(/\\u([0-9a-fA-F]{4})/g, (_, hex) =>
    String.fromCharCode(parseInt(hex, 16))
  );
}


function decodeString(text) {
  return decodeUnicodeEscapes(decodeXmlEntities(text));
}

/** Parse one strings.xml into a flat key -> decoded-string map. */
function parseStringsXml(source) {
  const messages = {};
  for (const match of source.matchAll(STRING_TAG_RE)) {
    const attrs = match[1];
    const body = match[2];
    const nameMatch = NAME_ATTR_RE.exec(attrs);
    if (!nameMatch) {
      continue; // <string> without a name attribute — skip
    }
    const name = (nameMatch[1] ?? nameMatch[2] ?? "").trim();
    if (!name) {
      continue;
    }
    messages[name] = decodeString(body);
  }
  return messages;
}

/** `values` -> "en"; `values-es-419` -> "es-419"; null for unrelated dirs. */
function localeFromDirName(dirName) {
  if (dirName === "values") {
    return "en";
  }
  if (dirName.startsWith("values-")) {
    const locale = dirName.slice("values-".length);
    return locale.length > 0 ? locale : null;
  }
  return null;
}

function main() {
  const dirs = readdirSync(RES_DIR)
    .filter((dirName) => localeFromDirName(dirName) !== null)
    .sort();

  mkdirSync(OUT_DIR, { recursive: true });

  const written = [];
  for (const dirName of dirs) {
    const locale = localeFromDirName(dirName);
    const xmlPath = join(RES_DIR, dirName, "strings.xml");
    const source = readFileSync(xmlPath, "utf8");
    const messages = parseStringsXml(source);
    const sorted = Object.fromEntries(
      Object.keys(messages)
        .sort()
        .map((key) => [key, messages[key]])
    );
    const outPath = join(OUT_DIR, `${locale}.json`);
    writeFileSync(outPath, JSON.stringify(sorted, null, 2) + "\n", "utf8");
    written.push([locale, Object.keys(sorted).length, outPath]);
  }

  if (written.length === 0) {
    throw new Error(`No res/values*/strings.xml found under ${RES_DIR}`);
  }

  for (const [locale, count, outPath] of written) {
    process.stdout.write(`${locale}: ${count} keys -> ${outPath}\n`);
  }
  process.stdout.write(`\nWrote ${written.length} locale files to ${OUT_DIR}\n`);
}

main();
