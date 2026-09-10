import assert from "node:assert/strict";
import test from "node:test";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { callMcpTool } from "../src/mcp-tool-executor.js";

const testDir = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(testDir, "..", "..");
const fakeBinaryPath = resolve(testDir, "fixtures", "fake-sts2.mjs");
const missingBinaryPath = resolve(testDir, "fixtures", "missing-sts2.mjs");

function parseTextContent(result) {
  const text = result.content?.find((item) => item.type === "text")?.text;
  return text ? JSON.parse(text) : null;
}

test("callMcpTool returns cli_spawn_failed for launch problems", async () => {
  const result = await callMcpTool(
    {
      binaryPath: missingBinaryPath,
      cwd: repoRoot,
    },
    "game_info",
    {},
  );

  assert.equal(result.isError, true);
  assert.equal(result.structuredContent.code, "cli_spawn_failed");
  assert.equal(result.structuredContent.argv[0], "--json");
});

test("callMcpTool returns cli_protocol_error for invalid JSON success payloads", async () => {
  const result = await callMcpTool(
    {
      binaryPath: fakeBinaryPath,
      cwd: repoRoot,
      env: {
        ...process.env,
        FAKE_STS2_CASE: "invalid-json",
      },
    },
    "game_info",
    {},
  );

  assert.equal(result.isError, true);
  assert.equal(result.structuredContent.code, "cli_protocol_error");
  assert.match(result.structuredContent.stderr, /synthetic invalid json/i);
});

test("callMcpTool preserves load_fixture recipe reports", async () => {
  const result = await callMcpTool(
    {
      binaryPath: fakeBinaryPath,
      cwd: repoRoot,
      env: {
        ...process.env,
        FAKE_STS2_CASE: "load-fixture-result",
      },
    },
    "load_fixture",
    {
      path: "fixtures/multiplayer-ownership-local-only-degraded.sts2.fixture.yaml",
    },
  );

  assert.equal(result.isError, false);
  assert.equal(result.structuredContent.recipeReport.recipeName, "multiplayer-lobby-recipe");
  assert.equal(
    result.structuredContent.recipeReport.degradedMultiplayerFields[0].reasonCode,
    "local-only-degraded-multiplayer",
  );
  assert.equal(result.structuredContent.recipeReport.bridgeValidation.status, "passed");
});

// The `presentation` CLI command group was deleted upstream (bcf9a97e) and `inspect ai-tools` no
// longer advertises the tools, so the executor must not pretend to route them.
test("callMcpTool rejects the removed presentation tools", async () => {
  for (const toolName of ["presentation_snapshot", "presentation_catalog"]) {
    await assert.rejects(
      () =>
        callMcpTool(
          {
            binaryPath: fakeBinaryPath,
            cwd: repoRoot,
            env: { ...process.env, FAKE_STS2_CASE: "echo-argv" },
          },
          toolName,
          {},
        ),
      /Unsupported AI tool: presentation_/,
    );
  }
});

test("callMcpTool preserves service durable test_run terminal payloads", async () => {
  const originalFetch = globalThis.fetch;
  const requests = [];

  try {
    globalThis.fetch = async (url, options = {}) => {
      requests.push({ url, options });
      if (url === "http://127.0.0.1:4317/v0/test-runs" && options.method === "POST") {
        assert.deepEqual(JSON.parse(options.body), {
          inline: "name: canceled\nsteps: [game.info]\n",
          durable: true,
        });
        return new Response(JSON.stringify({ runId: "run-canceled", status: "queued" }), {
          status: 200,
          headers: {
            "Content-Type": "application/json",
          },
        });
      }

      assert.equal(url, "http://127.0.0.1:4317/v0/test-runs/run-canceled");
      return new Response(
        JSON.stringify({
          runId: "run-canceled",
          status: "canceled",
          error: {
            code: "remote_test_run_canceled",
            message: "Remote test run was canceled.",
          },
        }),
        {
          status: 200,
          headers: {
            "Content-Type": "application/json",
          },
        },
      );
    };

    const result = await callMcpTool(
      {
        binaryPath: missingBinaryPath,
        cwd: repoRoot,
        serviceUrl: "http://127.0.0.1:4317",
        serviceToken: "local-dev-token",
      },
      "test_run",
      {
        inline: "name: canceled\nsteps: [game.info]\n",
        durable: true,
      },
    );

    assert.equal(result.isError, true);
    assert.equal(result.structuredContent.exitCode, 1);
    assert.equal(result.structuredContent.payload.status, "canceled");
    assert.equal(result.structuredContent.payload.error.code, "remote_test_run_canceled");
    assert.deepEqual(parseTextContent(result), result.structuredContent);
    assert.equal(requests.length, 2);
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("callMcpTool preserves service-backed invalid action failures", async () => {
  const originalFetch = globalThis.fetch;
  const requests = [];

  try {
    globalThis.fetch = async (url, options = {}) => {
      requests.push({ url, options });
      assert.equal(url, "http://127.0.0.1:4317/v0/tools/call");
      assert.equal(options.method, "POST");
      assert.deepEqual(JSON.parse(options.body), {
        name: "act",
        arguments: {
          kind: "choose",
        },
      });
      return new Response(
        JSON.stringify({
          error: {
            code: "invalid_ai_tool_arguments",
            message: "Invalid arguments for tool act.",
            details: [
              {
                path: "choiceId",
                message: "Required for choose actions.",
              },
            ],
          },
        }),
        {
          status: 400,
          headers: {
            "Content-Type": "application/json",
          },
        },
      );
    };

    const result = await callMcpTool(
      {
        binaryPath: missingBinaryPath,
        cwd: repoRoot,
        serviceUrl: "http://127.0.0.1:4317",
        serviceToken: "local-dev-token",
      },
      "act",
      {
        kind: "choose",
      },
    );

    assert.equal(result.isError, true);
    assert.equal(result.structuredContent.exitCode, 1);
    assert.equal(result.structuredContent.payload.exitCode, 1);
    assert.equal(result.structuredContent.payload.error.code, "invalid_ai_tool_arguments");
    assert.deepEqual(parseTextContent(result), result.structuredContent);
    assert.equal(requests.length, 1);
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("callMcpTool returns service durable test_run summary with remote artifacts", async () => {
  const originalFetch = globalThis.fetch;
  const requests = [];

  try {
    globalThis.fetch = async (url, options = {}) => {
      requests.push({ url, options });
      if (url === "http://127.0.0.1:4317/v0/test-runs" && options.method === "POST") {
        assert.deepEqual(JSON.parse(options.body), {
          inline: "name: durable\nsteps: [game.info]\n",
          durable: true,
        });
        return new Response(JSON.stringify({ runId: "run-durable", status: "queued" }), {
          status: 200,
          headers: {
            "Content-Type": "application/json",
          },
        });
      }

      assert.equal(url, "http://127.0.0.1:4317/v0/test-runs/run-durable");
      return new Response(
        JSON.stringify({
          runId: "run-durable",
          status: "completed",
          summary: {
            status: "passed",
            exitCode: 0,
            remoteArtifacts: [
              {
                relativePath: "summary.json",
                downloadUrl: "http://127.0.0.1:4317/v0/test-runs/run-durable/artifacts/summary.json",
                contentType: "application/json",
              },
            ],
          },
        }),
        {
          status: 200,
          headers: {
            "Content-Type": "application/json",
          },
        },
      );
    };

    const result = await callMcpTool(
      {
        binaryPath: missingBinaryPath,
        cwd: repoRoot,
        serviceUrl: "http://127.0.0.1:4317",
        serviceToken: "local-dev-token",
      },
      "test_run",
      {
        inline: "name: durable\nsteps: [game.info]\n",
        durable: true,
      },
    );

    assert.equal(result.isError, false);
    assert.equal(result.structuredContent.status, "passed");
    assert.equal(result.structuredContent.remoteArtifacts[0].relativePath, "summary.json");
    assert.equal(requests.length, 2);
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("callMcpTool dispatches promoted lifecycle and dev tools through the wrapper client", async () => {
  const clientOptions = {
    binaryPath: fakeBinaryPath,
    cwd: repoRoot,
    env: {
      ...process.env,
      FAKE_STS2_CASE: "echo-argv",
    },
  };

  const launch = await callMcpTool(clientOptions, "game_launch", {
    timeoutMs: 30_000,
    intervalMs: 250,
    launchArgs: ["--headless", "-fastmp"],
  });
  assert.equal(launch.isError, false);
  assert.deepEqual(launch.structuredContent.argv, [
    "--json",
    "game",
    "launch",
    "--timeout-ms",
    "30000",
    "--interval-ms",
    "250",
    "--",
    "--headless",
    "-fastmp",
  ]);

  const attach = await callMcpTool(clientOptions, "game_attach", {
    timeoutMs: 15_000,
  });
  assert.equal(attach.isError, false);
  assert.deepEqual(attach.structuredContent.argv, [
    "--json",
    "game",
    "attach",
    "--timeout-ms",
    "15000",
  ]);

  const close = await callMcpTool(clientOptions, "game_close", {
    timeoutMs: 30_000,
    intervalMs: 250,
  });
  assert.equal(close.isError, false);
  assert.deepEqual(close.structuredContent.argv, [
    "--json",
    "game",
    "close",
    "--timeout-ms",
    "30000",
    "--interval-ms",
    "250",
  ]);

  const deploy = await callMcpTool(clientOptions, "game_deploy", {
    path: "./mods/MyMod",
    build: true,
    restart: true,
    verify: true,
  });
  assert.equal(deploy.isError, false);
  assert.deepEqual(deploy.structuredContent.argv, [
    "--json",
    "game",
    "deploy",
    "./mods/MyMod",
    "--build",
    "--restart",
    "--verify",
  ]);

  const loadEventRoomFixture = await callMcpTool(clientOptions, "load_fixture", {
    path: "fixtures/basic-event-room.sts2.fixture.yaml",
  });
  assert.equal(loadEventRoomFixture.isError, false);
  assert.deepEqual(loadEventRoomFixture.structuredContent.argv, [
    "--json",
    "dev",
    "fixture",
    "load",
    "--path",
    "fixtures/basic-event-room.sts2.fixture.yaml",
  ]);

  const loadBundleSelectionFixture = await callMcpTool(clientOptions, "load_fixture", {
    path: "fixtures/basic-bundle-selection.sts2.fixture.yaml",
  });
  assert.equal(loadBundleSelectionFixture.isError, false);
  assert.deepEqual(loadBundleSelectionFixture.structuredContent.argv, [
    "--json",
    "dev",
    "fixture",
    "load",
    "--path",
    "fixtures/basic-bundle-selection.sts2.fixture.yaml",
  ]);

  const scenarioExport = await callMcpTool(clientOptions, "scenario_export", {
    output: "tests/scenarios/repros/shop-anchor.sts2.scenario.yaml",
    includeExact: true,
  });
  assert.equal(scenarioExport.isError, false);
  assert.deepEqual(scenarioExport.structuredContent.argv, [
    "--json",
    "dev",
    "scenario",
    "export",
    "--output",
    "tests/scenarios/repros/shop-anchor.sts2.scenario.yaml",
    "--include-exact",
  ]);

  const scenarioLoad = await callMcpTool(clientOptions, "scenario_load", {
    path: "tests/scenarios/repros/shop-anchor.sts2.scenario.yaml",
    restart: true,
    timeoutMs: 30_000,
    intervalMs: 250,
    allowDegradedLocalMultiplayer: true,
  });
  assert.equal(scenarioLoad.isError, false);
  assert.deepEqual(scenarioLoad.structuredContent.argv, [
    "--json",
    "dev",
    "scenario",
    "load",
    "--path",
    "tests/scenarios/repros/shop-anchor.sts2.scenario.yaml",
    "--restart",
    "--timeout-ms",
    "30000",
    "--interval-ms",
    "250",
    "--allow-degraded-local-multiplayer",
  ]);

  const reload = await callMcpTool(clientOptions, "hot_reload", {
    project: "./mods/MyHotMod",
    build: true,
    wait: true,
  });
  assert.equal(reload.isError, false);
  assert.deepEqual(reload.structuredContent.argv, [
    "--json",
    "dev",
    "mod-reload",
    "--project",
    "./mods/MyHotMod",
    "--build",
    "--wait",
  ]);

  const console = await callMcpTool(clientOptions, "console", {
    command: "help",
    args: ["draw"],
  });
  assert.equal(console.isError, false);
  assert.deepEqual(console.structuredContent.argv, [
    "--json",
    "dev",
    "console",
    "help",
    "draw",
  ]);

  const debugStatus = await callMcpTool(clientOptions, "debug_status", {});
  assert.equal(debugStatus.isError, false);
  assert.deepEqual(debugStatus.structuredContent.argv, [
    "--json",
    "dev",
    "debug",
    "status",
  ]);

  const debugStep = await callMcpTool(clientOptions, "debug_step", {
    kind: "frame",
    count: 1,
  });
  assert.equal(debugStep.isError, false);
  assert.deepEqual(debugStep.structuredContent.argv, [
    "--json",
    "dev",
    "debug",
    "step",
    "--kind",
    "frame",
    "--count",
    "1",
  ]);

  const debugEvents = await callMcpTool(clientOptions, "debug_events", {
    session: "dbg:observer",
    fromSequence: 42,
    limit: 50,
    follow: true,
    timeoutMs: 1000,
  });
  assert.equal(debugEvents.isError, false);
  assert.deepEqual(debugEvents.structuredContent.argv, [
    "--json",
    "dev",
    "debug",
    "events",
    "--session",
    "dbg:observer",
    "--from-sequence",
    "42",
    "--limit",
    "50",
    "--follow",
    "--timeout-ms",
    "1000",
  ]);

  const breakpointAdd = await callMcpTool(clientOptions, "breakpoint_add", {
    path: "screen.type",
    equals: "\"main-menu\"",
    name: "menu-break",
  });
  assert.equal(breakpointAdd.isError, false);
  assert.deepEqual(breakpointAdd.structuredContent.argv, [
    "--json",
    "dev",
    "breakpoint",
    "add",
    "--path",
    "screen.type",
    "--name",
    "menu-break",
    "--equals",
    "\"main-menu\"",
  ]);

  const breakpointRemove = await callMcpTool(clientOptions, "breakpoint_remove", {
    id: "bp:1",
  });
  assert.equal(breakpointRemove.isError, false);
  assert.deepEqual(breakpointRemove.structuredContent.argv, [
    "--json",
    "dev",
    "breakpoint",
    "remove",
    "--id",
    "bp:1",
  ]);

  const screenshot = await callMcpTool(clientOptions, "screenshot", {
    output: "tmp/artifacts/runtime.png",
  });
  assert.equal(screenshot.isError, false);
  assert.deepEqual(screenshot.structuredContent.argv, [
    "--json",
    "dev",
    "screenshot",
    "--output",
    "tmp/artifacts/runtime.png",
  ]);

  const toolchainInfo = await callMcpTool(clientOptions, "toolchain_info", {});
  assert.equal(toolchainInfo.isError, false);
  assert.deepEqual(toolchainInfo.structuredContent.argv, [
    "--json",
    "toolchain",
    "info",
  ]);

  const projectProfileShow = await callMcpTool(clientOptions, "project_profile_show", {
    name: "install-bridge",
  });
  assert.equal(projectProfileShow.isError, false);
  assert.deepEqual(projectProfileShow.structuredContent.argv, [
    "--json",
    "project",
    "profile",
    "show",
    "install-bridge",
  ]);

  const diagnostics = await callMcpTool(clientOptions, "diagnostics", {
    bundleDir: "tmp/artifacts/diagnostics",
    preset: "desktop-1080p",
  });
  assert.equal(diagnostics.isError, false);
  assert.deepEqual(diagnostics.structuredContent.argv, [
    "--json",
    "dev",
    "diagnostics",
    "--preset",
    "desktop-1080p",
    "--bundle-dir",
    "tmp/artifacts/diagnostics",
  ]);

  const screenshotDiff = await callMcpTool(clientOptions, "screenshot_diff", {
    baseline: "tests/scenarios/baselines/mock-main-menu.png",
    actual: "tests/scenarios/baselines/mock-main-menu.png",
    presetCatalogs: ["tests/visual/sample-catalog.sts2.viewport-presets.yaml"],
    rpcTimeoutMs: 800,
  });
  assert.equal(screenshotDiff.isError, false);
  assert.deepEqual(screenshotDiff.structuredContent.argv, [
    "--json",
    "dev",
    "screenshot-diff",
    "--baseline",
    "tests/scenarios/baselines/mock-main-menu.png",
    "--actual",
    "tests/scenarios/baselines/mock-main-menu.png",
    "--preset-catalog",
    "tests/visual/sample-catalog.sts2.viewport-presets.yaml",
    "--rpc-timeout-ms",
    "800",
  ]);

  const snapshotExport = await callMcpTool(clientOptions, "snapshot_export", {
    spec: "tests/snapshots/main-menu.sts2.snapshot.yaml",
    output: "tmp/artifacts/snapshots/main-menu",
  });
  assert.equal(snapshotExport.isError, false);
  assert.deepEqual(snapshotExport.structuredContent.argv, [
    "--json",
    "dev",
    "snapshot",
    "export",
    "--spec",
    "tests/snapshots/main-menu.sts2.snapshot.yaml",
    "--output",
    "tmp/artifacts/snapshots/main-menu",
  ]);

  const snapshotCompare = await callMcpTool(clientOptions, "snapshot_compare", {
    spec: "tests/snapshots/main-menu.sts2.snapshot.yaml",
    baseline: "tests/snapshots/baselines/main-menu",
    bundleDir: "tmp/artifacts/snapshots/main-menu",
  });
  assert.equal(snapshotCompare.isError, false);
  assert.deepEqual(snapshotCompare.structuredContent.argv, [
    "--json",
    "dev",
    "snapshot",
    "compare",
    "--spec",
    "tests/snapshots/main-menu.sts2.snapshot.yaml",
    "--baseline",
    "tests/snapshots/baselines/main-menu",
    "--bundle-dir",
    "tmp/artifacts/snapshots/main-menu",
  ]);

  const testStress = await callMcpTool(clientOptions, "test_stress", {
    path: "tests/scenarios/regression-main-menu.sts2.yaml",
    iterations: 4,
    maxFailures: 1,
  });
  assert.equal(testStress.isError, false);
  assert.deepEqual(testStress.structuredContent.argv, [
    "--json",
    "test",
    "stress",
    "--iterations",
    "4",
    "--max-failures",
    "1",
    "tests/scenarios/regression-main-menu.sts2.yaml",
  ]);

  const viewportPresets = await callMcpTool(clientOptions, "inspect_viewport_presets", {
    presetCatalogs: ["tests/visual/sample-catalog.sts2.viewport-presets.yaml"],
  });
  assert.equal(viewportPresets.isError, false);
  assert.deepEqual(viewportPresets.structuredContent.argv, [
    "--json",
    "inspect",
    "viewport-presets",
    "--preset-catalog",
    "tests/visual/sample-catalog.sts2.viewport-presets.yaml",
  ]);

  const codeHookInfo = await callMcpTool(clientOptions, "code_hook_info", {
    query: "Namespace.Type::Method(System.String)",
  });
  assert.equal(codeHookInfo.isError, false);
  assert.deepEqual(codeHookInfo.structuredContent.argv, [
    "--json",
    "code",
    "hook-info",
    "Namespace.Type::Method(System.String)",
  ]);

  const referenceTopics = await callMcpTool(clientOptions, "inspect_reference_topics", {});
  assert.equal(referenceTopics.isError, false);
  assert.deepEqual(referenceTopics.structuredContent.argv, [
    "--json",
    "inspect",
    "reference-topics",
  ]);
});

test("callMcpTool preserves typed state payloads without remapping", async () => {
  const result = await callMcpTool(
    {
      binaryPath: fakeBinaryPath,
      cwd: repoRoot,
      env: {
        ...process.env,
        FAKE_STS2_CASE: "state",
      },
    },
    "state",
    {},
  );

  assert.equal(result.isError, false);
  assert.equal(result.structuredContent.screen.id, "bundle-selection");
  assert.equal(result.structuredContent.localPlayerId, "p1");
  assert.equal(result.structuredContent.hostPlayerId, "p1");
  assert.equal(result.structuredContent.localRole, "host");
  assert.equal(result.structuredContent.remoteOrchestrationCapability, "local-only-degraded");
  assert.equal(result.structuredContent.map.nodes[0].id, "map-node:3:1");
  assert.equal(result.structuredContent.map.nodes[0].playerId, "p1");
  assert.equal(result.structuredContent.map.nodes[0].remoteOrchestrationCapability, "local-only-degraded");
  assert.equal(result.structuredContent.eventRoom.options[0].id, "event-room:gain-gold:0");
  assert.equal(result.structuredContent.treasureRoom.relics[0].id, "treasure-room:relic:anchor:0");
  assert.equal(result.structuredContent.relicSelection.relics[0].id, "relic-selection:anchor:0");
  assert.equal(result.structuredContent.restSite.controls[0].preferredAction.kind, "rest");
  assert.equal(result.structuredContent.shop.purchasableItems[0].cost.amount, 50);
  assert.equal(result.structuredContent.shop.purchasableItems[0].preferredAction.kind, "buy-card");
  assert.equal(result.structuredContent.rewards.rewards[0].id, "reward:p1:0");
  assert.equal(result.structuredContent.rewards.rewards[0].preferredAction.kind, "claim-reward");
  assert.equal(result.structuredContent.cardSelection.cards[0].id, "card-selection:card:bash:0");
  assert.equal(result.structuredContent.simpleCardSelection.choices[0].id, "simple-card-selection:card:strike:0");
  assert.equal(result.structuredContent.deckCardSelection.deckCards[0].id, "deck-card-selection:card:defend:0");
  assert.notEqual(result.structuredContent.bundleSelection, null);
  assert.equal(result.structuredContent.bundleSelection.bundles[0].id, "bundle:offensive-pack");
  assert.equal(result.structuredContent.bundleSelection.bundles[0].playerId, "p1");
  assert.equal(result.structuredContent.bundleSelection.bundles[0].remoteOrchestrationCapability, "local-only-degraded");
  assert.equal(result.structuredContent.multiplayerLobby.players[0].perspective, "local:p1");
  assert.equal(result.structuredContent.multiplayerLobby.players[0].isHost, true);
  assert.equal(
    result.structuredContent.multiplayerLobby.players.find((player) => player.id === "p3").ownerRole,
    "host-local-seat",
  );
  assert.equal(
    result.structuredContent.multiplayerLobby.players.find((player) => player.id === "p3").remoteOrchestrationCapability,
    "host-local-seat",
  );
  assert.equal(result.structuredContent.multiplayerLobby.actions[0].intentKind, "ready");
  assert.equal(result.structuredContent.multiplayerLobby.actions[1].remoteOrchestrationCapability, "unsupported");
  assert.equal(
    result.structuredContent.multiplayerLobby.actions.find((action) => action.ownerPlayerId === "p3").ownerRole,
    "host-local-seat",
  );
  assert.equal(
    result.structuredContent.multiplayerLobby.actions.find((action) => action.ownerPlayerId === "p3").remoteOrchestrationCapability,
    "host-local-seat",
  );
  assert.equal(result.structuredContent.cardOverlay.cards[0].id, "card-overlay:p1:rage:0");
  assert.equal(result.structuredContent.cardOverlay.cards[0].assetRefs[0].key, "card:red:rage");
  assert.equal(result.structuredContent.cardOverlay.previewText, "Whenever you play an Attack this turn, gain 5 Block.");
  assert.equal(result.structuredContent.cardOverlay.breadcrumbs[0].screenType, "bundle-selection");
  assert.equal(result.structuredContent.cardOverlay.sourceBreadcrumbs[0].source, "underlying-screen");
  assert.equal(result.structuredContent.cardOverlay.close.intentKind, "close-overlay");
  assert.equal(result.structuredContent.cardOverlay.back.enabled, false);
  assert.equal(result.structuredContent.cardOverlay.followThroughControls[0].preferredAction, "choose");
  assert.equal(result.structuredContent.cardOverlay.notices[0].severity, "partial");
  assert.equal(result.structuredContent.cardOverlay.metadata.noticeCode, "card-overlay-partial");
  assert.equal(result.structuredContent.choices[0].choiceKind, "bundle");
  assert.equal(result.structuredContent.choices[0].intentKind, "choose-bundle");
  assert.equal(result.structuredContent.choices[0].preferredAction, "select-bundle");
  assert.equal(result.structuredContent.choices[0].preferredActionRef.kind, "select-bundle");
  assert.equal(result.structuredContent.choices[1].choiceKind, "overlay-close");
  assert.equal(result.structuredContent.choices[1].metadata.overlayPolicy, "blocking-overlay");
  assert.equal(result.structuredContent.availableActions[0].ownerPlayerId, "p1");
  assert.equal(result.structuredContent.availableActions[0].perspective, "local:p1");
  assert.equal(result.structuredContent.availableActions[0].preferredAction, "select-bundle");
  assert.equal(result.structuredContent.availableActions[1].intentKind, "close-overlay");
  assert.equal(result.structuredContent.availableActions[1].metadata.overlayPolicy, "blocking-overlay");
  assert.equal(result.structuredContent.notices[0].source, "test-fixture");
  assert.equal(result.structuredContent.notices[0].stability, "stable");
  assert.equal(result.structuredContent.notices[1].severity, "partial");
  assert.equal(result.structuredContent.notices[2].code, "card-overlay-passive");
});

test("callMcpTool preserves structured CLI failures for promoted mutating tools", async () => {
  const result = await callMcpTool(
    {
      binaryPath: fakeBinaryPath,
      cwd: repoRoot,
      env: {
        ...process.env,
        FAKE_STS2_CASE: "error-json",
      },
    },
    "game_deploy",
    {
      path: "./mods/MyMod",
      build: true,
    },
  );

  assert.equal(result.isError, true);
  assert.equal(result.structuredContent.exitCode, 3);
  assert.deepEqual(result.structuredContent.argv, [
    "--json",
    "game",
    "deploy",
    "./mods/MyMod",
    "--build",
  ]);
  assert.equal(result.structuredContent.payload.error.code, "synthetic_failure");
});

test("callMcpTool preserves structured action legality failures", async () => {
  const result = await callMcpTool(
    {
      binaryPath: fakeBinaryPath,
      cwd: repoRoot,
      env: {
        ...process.env,
        FAKE_STS2_CASE: "action-error-json",
      },
    },
    "act",
    {
      kind: "claim-reward",
      rewardId: "reward:p1:0",
    },
  );

  assert.equal(result.isError, true);
  assert.equal(result.structuredContent.exitCode, 3);
  assert.equal(result.structuredContent.payload.error.code, "action_rejected");
  assert.equal(result.structuredContent.payload.error.actionFailure.reasonCode, "wrong-player");
  assert.equal(result.structuredContent.payload.error.actionFailure.action, "claim-reward");
  assert.equal(result.structuredContent.payload.error.actionFailure.playerId, "p2");
  assert.equal(result.structuredContent.payload.error.actionFailure.requestedPlayerId, "p2");
  assert.equal(result.structuredContent.payload.error.actionFailure.resolvedOwnerPlayerId, "p1");
  assert.equal(result.structuredContent.payload.error.actionFailure.localPlayerId, "p1");
  assert.equal(result.structuredContent.payload.error.actionFailure.localRole, "host");
  assert.deepEqual(result.structuredContent.payload.error.actionFailure.checkedHookPaths, [
    "RewardsScreen.Confirm",
  ]);
  assert.equal(result.structuredContent.payload.error.details[0].actionReasonCode, "wrong-player");
  assert.equal(result.structuredContent.payload.error.details[0].requestedPlayerId, "p2");
  assert.deepEqual(parseTextContent(result), result.structuredContent);
});

test("callMcpTool executes current preferredAction references", async () => {
  const result = await callMcpTool(
    {
      binaryPath: fakeBinaryPath,
      cwd: repoRoot,
      env: {
        ...process.env,
        FAKE_STS2_CASE: "echo-argv",
      },
    },
    "act",
    {
      preferredAction: {
        kind: "select-map-node",
        ownerPlayerId: "p1",
        arguments: {
          mapNodeId: "map-node:3:1",
        },
      },
    },
  );

  assert.equal(result.isError, false);
  assert.deepEqual(result.structuredContent.argv, [
    "--json",
    "act",
    "select-map-node",
    "--node",
    "map-node:3:1",
    "--player-id",
    "p1",
  ]);
});

test("callMcpTool executes S86 preferredAction references", async () => {
  const result = await callMcpTool(
    {
      binaryPath: fakeBinaryPath,
      cwd: repoRoot,
      env: {
        ...process.env,
        FAKE_STS2_CASE: "echo-argv",
      },
    },
    "act",
    {
      preferredActionRef: {
        kind: "select-event-option",
        ownerPlayerId: "p1",
        arguments: {
          eventOptionId: "event-room:gain-gold:0",
        },
      },
    },
  );

  assert.equal(result.isError, false);
  assert.deepEqual(result.structuredContent.argv, [
    "--json",
    "act",
    "select-event-option",
    "--event-option",
    "event-room:gain-gold:0",
    "--player-id",
    "p1",
  ]);
});

test("callMcpTool preserves explicit playerId over preferredAction owner metadata", async () => {
  const result = await callMcpTool(
    {
      binaryPath: fakeBinaryPath,
      cwd: repoRoot,
      env: {
        ...process.env,
        FAKE_STS2_CASE: "echo-argv",
      },
    },
    "act",
    {
      playerId: "p2",
      preferredActionRef: {
        kind: "select-map-node",
        ownerPlayerId: "p1",
        arguments: {
          mapNodeId: "map-node:3:1",
          playerId: "p1",
        },
      },
    },
  );

  assert.equal(result.isError, false);
  assert.deepEqual(result.structuredContent.argv, [
    "--json",
    "act",
    "select-map-node",
    "--node",
    "map-node:3:1",
    "--player-id",
    "p2",
  ]);
});

test("callMcpTool preserves structured CLI failures for promoted repo-local hook tools", async () => {
  const result = await callMcpTool(
    {
      binaryPath: fakeBinaryPath,
      cwd: repoRoot,
      env: {
        ...process.env,
        FAKE_STS2_CASE: "error-json",
      },
    },
    "project_hook_run",
    {
      name: "repo-check",
      input: { kind: "smoke" },
    },
  );

  assert.equal(result.isError, true);
  assert.equal(result.structuredContent.exitCode, 3);
  assert.deepEqual(result.structuredContent.argv, [
    "--json",
    "project",
    "hook",
    "run",
    "repo-check",
    "--input",
    '{"kind":"smoke"}',
  ]);
  assert.equal(result.structuredContent.payload.error.code, "synthetic_failure");
});
