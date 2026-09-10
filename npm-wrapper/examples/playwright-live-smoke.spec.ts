import { expect, test as base } from "@playwright/test";
import { resolve } from "node:path";

import { Sts2CliError } from "@spirectl/sts2";
import { withSts2 } from "@spirectl/sts2/playwright";

const test = withSts2(base, {
  client: {
    cwd: process.cwd(),
    configPath: resolve(process.cwd(), "sts2.config.yaml"),
    mode: "normal",
  },
  failureArtifacts: {
    enabled: true,
    logs: {
      tail: 50,
    },
  },
});

test("generic phone UI interaction updates the live STS2 runtime", async ({ page, sts2 }) => {
  // Assume the current session already completed:
  //   scripts/live-bridge.sh deploy --game-path <path>
  //   # restart STS2 manually
  //   scripts/live-bridge.sh verify --config sts2.config.yaml
  await page.goto("http://127.0.0.1:3000");

  // The repo does not yet ship this UI. Replace these selectors with the app's
  // real controls when wiring the example into an actual frontend project.
  await page.getByTestId("phone-start-run").click();
  await page.getByTestId("phone-end-turn").click();

  await sts2.waitFor({
    path: "screen.type",
    equals: "combat",
    timeoutMs: 10_000,
    intervalMs: 100,
  });

  const state = await sts2.state();
  expect(state.screen?.type).toBe("combat");

  try {
    await sts2.assert({
      path: "screen.type",
      equals: "combat",
    });
  } catch (error) {
    if (error instanceof Sts2CliError) {
      const logs = await sts2.logs({
        limit: 20,
        level: "warn",
      });

      console.error("sts2 assertion failed", error.payload);
      console.error("recent bridge logs", logs.entries);
    }

    throw error;
  }
});
