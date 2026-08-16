import { test } from "node:test";
import assert from "node:assert/strict";
import { gzipSync } from "node:zlib";
import { decodeTraceBuffer } from "./trace.mjs";

test("decodeTraceBuffer gunzips gzip-compressed trace streams", () => {
  const trace = { traceEvents: [{ name: "RunTask", ph: "X" }] };
  const gz = gzipSync(Buffer.from(JSON.stringify(trace)));
  const decoded = decodeTraceBuffer(gz);
  assert.deepEqual(JSON.parse(decoded.toString("utf8")), trace);
});

test("decodeTraceBuffer passes plain JSON buffers through unchanged", () => {
  const buf = Buffer.from(JSON.stringify({ traceEvents: [] }));
  assert.equal(decodeTraceBuffer(buf), buf);
});
