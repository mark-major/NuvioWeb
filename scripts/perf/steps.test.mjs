import { test } from "node:test";
import assert from "node:assert/strict";
import { resolveSteps, STEP_IDS } from "./steps.mjs";

test("all is the default and covers the seven protocol steps", () => {
  assert.equal(STEP_IDS.length, 7);
  assert.deepEqual(resolveSteps(undefined), STEP_IDS);
  assert.deepEqual(resolveSteps("all"), STEP_IDS);
});

test("smoke is one rep of home d-pad", () => {
  assert.deepEqual(resolveSteps("smoke"), ["home_dpad_row"]);
});

test("explicit lists are filtered in order", () => {
  assert.deepEqual(resolveSteps("transition_settings,home_dpad_row"), [
    "transition_settings",
    "home_dpad_row"
  ]);
});

test("unknown ids throw", () => {
  assert.throws(() => resolveSteps("nope"));
});
