import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { Sts2CliError, createSts2Client } from "../src/index.js";
import { Sts2ServiceError, createServiceClient } from "../src/service-client.js";

const testDir = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(testDir, "..", "..");
const realBinaryPath = resolve(
  repoRoot,
  "target",
  "debug",
  process.platform === "win32" ? "sts2.exe" : "sts2",
);
const mockConfigPath = resolve(repoRoot, "tests", "sts2.mock.yaml");

async function spawnService() {
  const token = "local-dev-token";
  const child = spawn(
    realBinaryPath,
    [
      "--config",
      mockConfigPath,
      "--json",
      "service",
      "serve",
      "--listen",
      "127.0.0.1:0",
      "--auth-token",
      token,
    ],
    {
      cwd: repoRoot,
      stdio: ["ignore", "pipe", "pipe"],
    },
  );

  child.stdout.setEncoding("utf8");
  child.stderr.setEncoding("utf8");

  let stdoutBuffer = "";
  let stderr = "";

  const startup = await new Promise((resolveStartup, rejectStartup) => {
    const timeout = setTimeout(() => {
      rejectStartup(new Error(`Timed out waiting for service startup metadata. stderr: ${stderr}`));
    }, 5_000);

    child.stdout.on("data", (chunk) => {
      stdoutBuffer += chunk;
      const newlineIndex = stdoutBuffer.indexOf("\n");
      if (newlineIndex === -1) {
        return;
      }

      clearTimeout(timeout);
      const line = stdoutBuffer.slice(0, newlineIndex).trim();
      resolveStartup(JSON.parse(line));
    });

    child.stderr.on("data", (chunk) => {
      stderr += chunk;
    });

    child.once("exit", (code) => {
      clearTimeout(timeout);
      rejectStartup(new Error(`sts2 service exited with code ${code ?? 1}: ${stderr}`));
    });
  });

  return {
    baseUrl: startup.baseUrl,
    token,
    child,
    async close() {
      child.kill("SIGTERM");
      await new Promise((resolveClose) => {
        child.once("exit", resolveClose);
        setTimeout(() => {
          child.kill("SIGKILL");
          resolveClose();
        }, 1_000).unref();
      });
    },
  };
}

test("createServiceClient supports metadata, tool calls, and remote test runs", async () => {
  const service = await spawnService();

  try {
    const client = createServiceClient({
      serviceUrl: service.baseUrl,
      serviceToken: service.token,
    });

    const metadata = await client.serviceInfo();
    assert.equal(metadata.service, "sts2");
    assert.equal(metadata.auth.mode, "bearer");

    const catalog = await client.inspectAiTools();
    assert.ok(catalog.tools.some((tool) => tool.name === "game_info"));

    const info = await client.callTool("game_info", {});
    assert.equal(info.repository, "spirectl");

    const summary = await client.testRun({
      scenario: {
        name: "remote-smoke",
        steps: ["game.info"],
      },
    });
    assert.equal(summary.status, "passed");
    assert.ok(Array.isArray(summary.remoteArtifacts));
    assert.ok(summary.remoteArtifacts.some((artifact) => artifact.relativePath === "summary.json"));
  } finally {
    await service.close();
  }
});

test("createServiceClient exposes debug session endpoints", async () => {
  const service = await spawnService();

  try {
    const client = createServiceClient({
      serviceUrl: service.baseUrl,
      serviceToken: service.token,
    });

    const started = await client.debugSessionStart({
      name: "m45",
      role: "observer",
      pause: true,
      leaseTimeoutMs: 9000,
    });
    assert.equal(started.started, false);

    const status = await client.debugSessionStatus("dbg:1");
    assert.equal(status.found, false);

    const waited = await client.debugWait("dbg:1", { timeoutMs: 2500 });
    assert.equal(waited.completed, false);

    const events = await client.debugEvents("dbg:1", {
      fromSequence: 42,
      limit: 50,
      follow: true,
      timeoutMs: 1000,
    });
    assert.ok(Array.isArray(events.events));
    assert.equal(events.follow, true);
    assert.equal(events.timeoutMs, 1000);

    const ended = await client.debugSessionEnd("dbg:1", { resume: true });
    assert.equal(ended.ended, false);
  } finally {
    await service.close();
  }
});

test("createServiceClient exposes remote test-run artifact helpers", async () => {
  const service = await spawnService();

  try {
    const client = createServiceClient({
      serviceUrl: service.baseUrl,
      serviceToken: service.token,
    });

    const submitted = await client.submitTestRun({
      scenario: {
        name: "remote-smoke-artifacts",
        steps: ["game.info"],
      },
    });
    assert.equal(submitted.status, "queued");
    assert.match(submitted.runId, /^run-\d+$/);

    const completed = await client.waitForTestRun(submitted.runId);
    assert.equal(completed.status, "completed");
    assert.equal(completed.summary.status, "passed");

    const manifest = await client.listTestRunArtifacts(submitted.runId);
    assert.equal(manifest.runId, submitted.runId);
    assert.ok(manifest.artifacts.some((artifact) => artifact.relativePath === "summary.json"));

    const summary = await client.downloadTestRunArtifact(submitted.runId, "summary.json");
    assert.equal(summary.contentType, "application/json");
    assert.equal(summary.body.status, "passed");
  } finally {
    await service.close();
  }
});

test("createServiceClient passes durable test-run options to the service", async () => {
  const originalFetch = globalThis.fetch;

  try {
    globalThis.fetch = async (url, options = {}) => {
      assert.equal(url, "http://127.0.0.1:4317/v0/test-runs");
      assert.equal(options.method, "POST");
      assert.deepEqual(JSON.parse(options.body), {
        inline: "name: durable-smoke\nsteps: [game.info]\n",
        tags: ["durable"],
        artifactsDir: "tmp/artifacts",
        failureArtifacts: "never",
        durable: true,
      });
      return new Response(JSON.stringify({ runId: "run-9", status: "queued" }), {
        status: 200,
        headers: {
          "Content-Type": "application/json",
        },
      });
    };

    const client = createServiceClient({
      serviceUrl: "http://127.0.0.1:4317",
      serviceToken: "local-dev-token",
    });

    const submitted = await client.submitTestRun({
      inline: "name: durable-smoke\nsteps: [game.info]\n",
      tags: ["durable"],
      artifactsDir: "tmp/artifacts",
      failureArtifacts: "never",
      durable: true,
    });
    assert.equal(submitted.runId, "run-9");
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("createServiceClient keeps scenario job and artifact helper requests aligned with test-run endpoints", async () => {
  const originalFetch = globalThis.fetch;
  const calls = [];

  try {
    globalThis.fetch = async (url, options = {}) => {
      calls.push({ url, options });
      if (url === "http://127.0.0.1:4317/v0/test-runs") {
        assert.equal(options.method, "POST");
        assert.deepEqual(JSON.parse(options.body), {
          inline: '{"name":"remote-inline-object","steps":["game.info"]}',
          tags: ["fast", "mcp"],
          artifactsDir: "tmp/remote-artifacts",
          failureArtifacts: "on-failure",
          durable: true,
        });
        return new Response(JSON.stringify({ runId: "run-23", status: "queued" }), {
          status: 200,
          headers: {
            "Content-Type": "application/json",
          },
        });
      }

      if (url === "http://127.0.0.1:4317/v0/test-runs/run-23") {
        assert.equal(options.method, "GET");
        return new Response(JSON.stringify({ runId: "run-23", status: "completed", summary: { status: "passed" } }), {
          status: 200,
          headers: {
            "Content-Type": "application/json",
          },
        });
      }

      if (url === "http://127.0.0.1:4317/v0/test-runs/run-23/artifacts") {
        assert.equal(options.method, "GET");
        return new Response(JSON.stringify({ runId: "run-23", artifacts: [] }), {
          status: 200,
          headers: {
            "Content-Type": "application/json",
          },
        });
      }

      if (url === "http://127.0.0.1:4317/v0/test-runs/run-23/artifacts/nested/result.json") {
        assert.equal(options.method, "GET");
        return new Response(JSON.stringify({ status: "passed" }), {
          status: 200,
          headers: {
            "Content-Type": "application/json",
          },
        });
      }

      throw new Error(`unexpected fetch URL: ${url}`);
    };

    const client = createServiceClient({
      serviceUrl: "http://127.0.0.1:4317/",
      serviceToken: "local-dev-token",
    });

    const submitted = await client.submitTestRun({
      scenario: {
        name: "remote-inline-object",
        steps: ["game.info"],
      },
      tags: ["fast", "mcp"],
      artifactsDir: "tmp/remote-artifacts",
      failureArtifacts: "on-failure",
      durable: true,
    });
    assert.equal(submitted.runId, "run-23");
    assert.equal((await client.waitForTestRun("run-23")).status, "completed");
    assert.equal((await client.listTestRunArtifacts("run-23")).runId, "run-23");
    assert.equal((await client.downloadTestRunArtifact("run-23", "nested/result.json")).body.status, "passed");
    assert.deepEqual(calls.map((call) => call.options.headers.Authorization), [
      "Bearer local-dev-token",
      "Bearer local-dev-token",
      "Bearer local-dev-token",
      "Bearer local-dev-token",
    ]);
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("createServiceClient preserves recovered terminal test-run status payloads", async () => {
  const terminalStatuses = [
    ["orphaned", "remote_test_run_orphaned"],
    ["unknown", "durable_job_metadata_unreadable"],
    ["canceled", "remote_test_run_canceled"],
  ];

  for (const [status, code] of terminalStatuses) {
    const originalFetch = globalThis.fetch;

    try {
      globalThis.fetch = async (url) => {
        assert.equal(url, `http://127.0.0.1:4317/v0/test-runs/run-${status}`);
        return new Response(
          JSON.stringify({
            runId: `run-${status}`,
            status,
            error: {
              code,
              message: `${status} durable job`,
            },
            recovery: {
              previousStatus: "running",
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

      const client = createServiceClient({
        serviceUrl: "http://127.0.0.1:4317",
        serviceToken: "local-dev-token",
      });

      await assert.rejects(
        () => client.waitForTestRun(`run-${status}`),
        (error) => {
          assert.ok(error instanceof Sts2ServiceError);
          assert.equal(error.payload.runId, `run-${status}`);
          assert.equal(error.payload.status, status);
          assert.equal(error.payload.error.code, code);
          assert.equal(error.payload.recovery.previousStatus, "running");
          return true;
        },
      );
    } finally {
      globalThis.fetch = originalFetch;
    }
  }
});

test("createServiceClient percent-encodes reserved artifact path characters", async () => {
  const originalFetch = globalThis.fetch;

  try {
    globalThis.fetch = async (url) => {
      assert.equal(
        url,
        "http://127.0.0.1:4317/v0/test-runs/run-7/artifacts/folder%20with%20spaces/summary%20%231%25.json",
      );
      return new Response('{"status":"passed"}', {
        status: 200,
        headers: {
          "Content-Type": "application/json",
        },
      });
    };

    const client = createServiceClient({
      serviceUrl: "http://127.0.0.1:4317",
      serviceToken: "local-dev-token",
    });

    const artifact = await client.downloadTestRunArtifact(
      "run-7",
      "folder with spaces/summary #1%.json",
    );
    assert.equal(artifact.contentType, "application/json");
    assert.equal(artifact.body.status, "passed");
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("createServiceClient preserves typed state tool payloads", async () => {
  const originalFetch = globalThis.fetch;

  try {
    globalThis.fetch = async (url, options = {}) => {
      assert.equal(url, "http://127.0.0.1:4317/v0/tools/call");
      assert.equal(options.method, "POST");
      assert.deepEqual(JSON.parse(options.body), {
        name: "state",
        arguments: {},
      });
      return new Response(
        JSON.stringify({
          schemaVersion: "spirectl/v0",
          screen: { id: "bundle-selection" },
          localPlayerId: "p1",
          hostPlayerId: "p1",
          localRole: "host",
          remoteOrchestrationCapability: "local-only-degraded",
          map: { nodes: [{ id: "map-node:3:1", playerId: "p1", remoteOrchestrationCapability: "local-only-degraded" }] },
          eventRoom: { options: [{ id: "event-room:gain-gold:0" }] },
          treasureRoom: { relics: [{ id: "treasure-room:relic:anchor:0" }] },
          relicSelection: { relics: [{ id: "relic-selection:anchor:0" }] },
          restSite: { controls: [{ id: "rest-site:rest", preferredAction: { kind: "rest" } }] },
          shop: {
            purchasableItems: [
              {
                id: "shop:p1:card:strike:0",
                cost: { amount: 50 },
                preferredAction: { kind: "buy-card", arguments: { shopItemId: "shop:p1:card:strike:0" } },
              },
            ],
          },
          rewards: {
            rewards: [
              {
                id: "reward:p1:0",
                preferredAction: { kind: "claim-reward", arguments: { rewardId: "reward:p1:0" } },
              },
            ],
          },
          cardSelection: { cards: [{ id: "card-selection:card:bash:0" }] },
          simpleCardSelection: { choices: [{ id: "simple-card-selection:card:strike:0" }] },
          deckCardSelection: { deckCards: [{ id: "deck-card-selection:card:defend:0" }] },
          bundleSelection: { bundles: [{ id: "bundle:offensive-pack", playerId: "p1", ownerPlayerId: "p1", remoteOrchestrationCapability: "local-only-degraded" }] },
          multiplayerLobby: {
            players: [
              { id: "p1", perspective: "local:p1", isLocal: true, isHost: true, isRemote: false },
              {
                id: "p3",
                perspective: "local:p3",
                ownerPlayerId: "p3",
                ownerRole: "host-local-seat",
                remoteOrchestrationCapability: "host-local-seat",
              },
            ],
            actions: [
              { id: "action:lobby:ready:p1", intentKind: "ready" },
              { id: "action:lobby:ready:p2", ownerPlayerId: "p2", remoteOrchestrationCapability: "unsupported" },
              {
                id: "action:lobby:ready:p3",
                ownerPlayerId: "p3",
                ownerRole: "host-local-seat",
                remoteOrchestrationCapability: "host-local-seat",
              },
            ],
          },
          cardOverlay: {
            cards: [{ id: "card-overlay:p1:rage:0", name: "Rage", assetRefs: [{ key: "card:red:rage" }] }],
            previewText: "Whenever you play an Attack this turn, gain 5 Block.",
            breadcrumbs: [{ screenType: "bundle-selection", ownerPlayerId: "p1" }],
            sourceBreadcrumbs: [{ screenType: "bundle-selection", source: "underlying-screen" }],
            close: { id: "card-overlay:close", intentKind: "close-overlay", preferredAction: "choose" },
            back: { id: "card-overlay:back", enabled: false },
            followThroughControls: [{ id: "card-overlay:inspect-source", preferredAction: "choose" }],
            notices: [{ code: "card-overlay-partial", severity: "partial", path: "cardOverlay" }],
            metadata: { noticeCode: "card-overlay-partial" },
          },
          choices: [
            {
              id: "choice:bundle",
              choiceKind: "bundle",
              preferredAction: "select-bundle",
              preferredActionRef: { kind: "select-bundle", arguments: { bundleId: "choice:bundle" } },
            },
            {
              id: "card-overlay:close",
              choiceKind: "overlay-close",
              metadata: { overlayPolicy: "blocking-overlay" },
            },
          ],
          availableActions: [
            {
              id: "action:bundle",
              ownerPlayerId: "p1",
              perspective: "local:p1",
              preferredAction: "select-bundle",
            },
            {
              id: "action:card-overlay:close",
              intentKind: "close-overlay",
              metadata: { overlayPolicy: "blocking-overlay" },
            },
          ],
          notices: [
            { path: "bundleSelection.bundles", source: "test-fixture", stability: "stable" },
            { path: "cardOverlay", severity: "partial", source: "test-fixture" },
            { code: "card-overlay-passive", path: "cardOverlay.passive" },
          ],
        }),
        {
          status: 200,
          headers: {
            "Content-Type": "application/json",
          },
        },
      );
    };

    const client = createServiceClient({
      serviceUrl: "http://127.0.0.1:4317",
      serviceToken: "local-dev-token",
    });
    const state = await client.callTool("state", {});

    assert.equal(state.localPlayerId, "p1");
    assert.equal(state.hostPlayerId, "p1");
    assert.equal(state.localRole, "host");
    assert.equal(state.remoteOrchestrationCapability, "local-only-degraded");
    assert.equal(state.map.nodes[0].id, "map-node:3:1");
    assert.equal(state.map.nodes[0].playerId, "p1");
    assert.equal(state.map.nodes[0].remoteOrchestrationCapability, "local-only-degraded");
    assert.equal(state.eventRoom.options[0].id, "event-room:gain-gold:0");
    assert.equal(state.treasureRoom.relics[0].id, "treasure-room:relic:anchor:0");
    assert.equal(state.relicSelection.relics[0].id, "relic-selection:anchor:0");
    assert.equal(state.restSite.controls[0].preferredAction.kind, "rest");
    assert.equal(state.shop.purchasableItems[0].cost.amount, 50);
    assert.equal(state.shop.purchasableItems[0].preferredAction.kind, "buy-card");
    assert.equal(state.rewards.rewards[0].id, "reward:p1:0");
    assert.equal(state.rewards.rewards[0].preferredAction.kind, "claim-reward");
    assert.equal(state.cardSelection.cards[0].id, "card-selection:card:bash:0");
    assert.equal(state.simpleCardSelection.choices[0].id, "simple-card-selection:card:strike:0");
    assert.equal(state.deckCardSelection.deckCards[0].id, "deck-card-selection:card:defend:0");
    assert.equal(state.bundleSelection.bundles[0].id, "bundle:offensive-pack");
    assert.equal(state.bundleSelection.bundles[0].ownerPlayerId, "p1");
    assert.equal(state.bundleSelection.bundles[0].remoteOrchestrationCapability, "local-only-degraded");
    assert.equal(state.multiplayerLobby.players[0].perspective, "local:p1");
    assert.equal(state.multiplayerLobby.players[0].isHost, true);
    assert.equal(
      state.multiplayerLobby.players.find((player) => player.id === "p3").ownerRole,
      "host-local-seat",
    );
    assert.equal(
      state.multiplayerLobby.players.find((player) => player.id === "p3").remoteOrchestrationCapability,
      "host-local-seat",
    );
    assert.equal(state.multiplayerLobby.actions[0].intentKind, "ready");
    assert.equal(state.multiplayerLobby.actions[1].remoteOrchestrationCapability, "unsupported");
    assert.equal(
      state.multiplayerLobby.actions.find((action) => action.ownerPlayerId === "p3").ownerRole,
      "host-local-seat",
    );
    assert.equal(
      state.multiplayerLobby.actions.find((action) => action.ownerPlayerId === "p3").remoteOrchestrationCapability,
      "host-local-seat",
    );
    assert.equal(state.cardOverlay.cards[0].id, "card-overlay:p1:rage:0");
    assert.equal(state.cardOverlay.cards[0].assetRefs[0].key, "card:red:rage");
    assert.equal(state.cardOverlay.sourceBreadcrumbs[0].source, "underlying-screen");
    assert.equal(state.cardOverlay.close.intentKind, "close-overlay");
    assert.equal(state.cardOverlay.followThroughControls[0].preferredAction, "choose");
    assert.equal(state.cardOverlay.notices[0].severity, "partial");
    assert.equal(state.cardOverlay.metadata.noticeCode, "card-overlay-partial");
    assert.equal(state.choices[0].choiceKind, "bundle");
    assert.equal(state.choices[0].preferredAction, "select-bundle");
    assert.equal(state.choices[0].preferredActionRef.kind, "select-bundle");
    assert.equal(state.choices[1].choiceKind, "overlay-close");
    assert.equal(state.choices[1].metadata.overlayPolicy, "blocking-overlay");
    assert.equal(state.availableActions[0].perspective, "local:p1");
    assert.equal(state.availableActions[0].preferredAction, "select-bundle");
    assert.equal(state.availableActions[1].intentKind, "close-overlay");
    assert.equal(state.availableActions[1].metadata.overlayPolicy, "blocking-overlay");
    assert.equal(state.notices[0].stability, "stable");
    assert.equal(state.notices[1].severity, "partial");
    assert.equal(state.notices[2].code, "card-overlay-passive");
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("createSts2Client forwards state perspective and playerId to the automation service", async () => {
  const originalFetch = globalThis.fetch;

  try {
    globalThis.fetch = async (url, options = {}) => {
      assert.equal(url, "http://127.0.0.1:4317/v0/tools/call");
      assert.equal(options.method, "POST");
      assert.deepEqual(JSON.parse(options.body), {
        name: "state",
        arguments: {
          perspective: "remote",
          playerId: "p2",
        },
      });
      return new Response(
        JSON.stringify({
          schemaVersion: "spirectl/v0",
          screen: { id: "multiplayer-lobby" },
          localPlayerId: "p1",
          hostPlayerId: "p1",
          perspective: "remote:p2",
          remoteOrchestrationCapability: "unsupported",
          choices: [],
          availableActions: [],
          notices: [],
        }),
        {
          status: 200,
          headers: {
            "Content-Type": "application/json",
          },
        },
      );
    };

    const client = createSts2Client({
      serviceUrl: "http://127.0.0.1:4317",
      serviceToken: "local-dev-token",
    });
    const state = await client.state({ perspective: "remote", playerId: "p2" });

    assert.equal(state.perspective, "remote:p2");
    assert.equal(state.remoteOrchestrationCapability, "unsupported");
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("createServiceClient preserves the CLI error envelope for tool-call failures", async () => {
  const service = await spawnService();

  try {
    const client = createServiceClient({
      serviceUrl: service.baseUrl,
      serviceToken: service.token,
    });

    await assert.rejects(
      () =>
        client.callTool("act", {
          kind: "choose",
        }),
      (error) => {
        assert.ok(error instanceof Sts2ServiceError);
        assert.equal(error.statusCode, 400);
        assert.equal(error.payload.exitCode, 2);
        assert.equal(error.payload.error.code, "invalid_ai_tool_arguments");
        assert.match(error.payload.error.message, /choiceId/);
        return true;
      },
    );
  } finally {
    await service.close();
  }
});

test("createSts2Client sends normalized current action arguments to the automation service", async () => {
  const originalFetch = globalThis.fetch;

  try {
    globalThis.fetch = async (url, options = {}) => {
      assert.equal(url, "http://127.0.0.1:4317/v0/tools/call");
      assert.equal(options.method, "POST");
      assert.deepEqual(JSON.parse(options.body), {
        name: "act",
        arguments: {
          kind: "play-card",
          cardId: "card:p1:strike:0",
          targetId: "enemy:jaw-worm:0",
          playerId: "p1",
        },
      });
      return new Response(
        JSON.stringify({
          requestId: "req-test",
          actionInstanceId: "action-test",
          kind: "play-card",
          accepted: true,
          provisional: false,
          message: "accepted",
        }),
        {
          status: 200,
          headers: {
            "Content-Type": "application/json",
          },
        },
      );
    };

    const client = createSts2Client({
      serviceUrl: "http://127.0.0.1:4317",
      serviceToken: "local-dev-token",
    });
    const action = await client.act({
      kind: "play-card",
      arguments: {
        cardId: "card:p1:strike:0",
        targetId: "enemy:jaw-worm:0",
        playerId: "p1",
      },
    });

    assert.equal(action.kind, "play-card");
    assert.equal(action.accepted, true);
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("createSts2Client sends normalized S86 action arguments to the automation service", async () => {
  const originalFetch = globalThis.fetch;

  try {
    globalThis.fetch = async (url, options = {}) => {
      assert.equal(url, "http://127.0.0.1:4317/v0/tools/call");
      assert.equal(options.method, "POST");
      assert.deepEqual(JSON.parse(options.body), {
        name: "act",
        arguments: {
          kind: "buy-card",
          ownerPlayerId: "p1",
          shopItemId: "shop:p1:card:strike:0",
          playerId: "p1",
        },
      });
      return new Response(
        JSON.stringify({
          requestId: "req-test",
          actionInstanceId: "action-test",
          kind: "buy-card",
          accepted: true,
          provisional: false,
          message: "accepted",
        }),
        {
          status: 200,
          headers: {
            "Content-Type": "application/json",
          },
        },
      );
    };

    const client = createSts2Client({
      serviceUrl: "http://127.0.0.1:4317",
      serviceToken: "local-dev-token",
    });
    const action = await client.act({
      preferredActionRef: {
        kind: "buy-card",
        ownerPlayerId: "p1",
        arguments: {
          shopItemId: "shop:p1:card:strike:0",
        },
      },
    });

    assert.equal(action.kind, "buy-card");
    assert.equal(action.accepted, true);
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("createServiceClient rejects test_run on the generic tool-call endpoint", async () => {
  const service = await spawnService();

  try {
    const client = createServiceClient({
      serviceUrl: service.baseUrl,
      serviceToken: service.token,
    });

    await assert.rejects(
      () =>
        client.callTool("test_run", {
          inline: "{name: remote-smoke, steps: [game.info]}",
        }),
      (error) => {
        assert.ok(error instanceof Sts2ServiceError);
        assert.equal(error.statusCode, 400);
        assert.equal(error.payload.error.code, "unsupported_service_tool");
        assert.match(error.payload.error.message, /\/v0\/test-runs/);
        return true;
      },
    );
  } finally {
    await service.close();
  }
});

test("createSts2Client prefers the configured automation service for supported calls", async () => {
  const service = await spawnService();

  try {
    const client = createSts2Client({
      cwd: repoRoot,
      serviceUrl: service.baseUrl,
      serviceToken: service.token,
    });

    const catalog = await client.inspectAiTools();
    assert.ok(catalog.tools.some((tool) => tool.name === "state"));

    const toolchain = await client.toolchainInfo();
    assert.ok(Array.isArray(toolchain.tools));

    const info = await client.gameInfo();
    assert.equal(info.repository, "spirectl");

    const state = await client.state();
    assert.equal(state.schemaVersion, "spirectl.state/v0");
    assert.equal(state.rootScene, "screens/main_menu");

    const action = await client.act({ kind: "choose", choiceId: "menu:start-run" });
    assert.equal(action.kind, "choose");
    assert.equal(action.accepted, true);

    const console = await client.devConsole({ command: "help", args: ["draw"] });
    assert.equal(console.line, "help draw");
    assert.equal(console.success, true);

    const summary = await client.testRun({
      scenario: {
        name: "remote-smoke-wrapper",
        steps: ["game.info"],
      },
    });
    assert.equal(summary.status, "passed");
  } finally {
    await service.close();
  }
});

test("createSts2Client can satisfy catalog-backed calls through the automation service without a local binary", async () => {
  const service = await spawnService();

  try {
    const client = createSts2Client({
      binaryPath: resolve(repoRoot, "target", "debug", "missing-sts2"),
      cwd: repoRoot,
      serviceUrl: service.baseUrl,
      serviceToken: service.token,
    });

    const toolchain = await client.toolchainInfo();
    assert.ok(Array.isArray(toolchain.tools));
  } finally {
    await service.close();
  }
});

test("createSts2Client selects the automation service through env without a local binary", async () => {
  const originalFetch = globalThis.fetch;

  try {
    globalThis.fetch = async (url, options = {}) => {
      assert.equal(url, "http://127.0.0.1:4317/v0/tools/call");
      assert.equal(options.method, "POST");
      assert.equal(options.headers.Authorization, "Bearer env-token");
      assert.deepEqual(JSON.parse(options.body), {
        name: "game_info",
        arguments: {},
      });
      return new Response(
        JSON.stringify({
          repository: "spirectl",
          executable: "sts2",
        }),
        {
          status: 200,
          headers: {
            "Content-Type": "application/json",
          },
        },
      );
    };

    const client = createSts2Client({
      binaryPath: resolve(repoRoot, "target", "debug", "missing-sts2"),
      cwd: repoRoot,
      env: {
        ...process.env,
        STS2_SERVICE_URL: "http://127.0.0.1:4317",
        STS2_SERVICE_TOKEN: "env-token",
      },
    });

    const info = await client.gameInfo();
    assert.equal(info.repository, "spirectl");
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("createSts2Client does not fall back to the local CLI for durable service job failures", async () => {
  const originalFetch = globalThis.fetch;
  const seenUrls = [];

  try {
    globalThis.fetch = async (url, options = {}) => {
      seenUrls.push(url);
      if (url === "http://127.0.0.1:4317/v0/test-runs") {
        assert.equal(options.method, "POST");
        assert.deepEqual(JSON.parse(options.body), {
          inline: "name: remote-failing-assert\nsteps: [game.info]\n",
          durable: true,
        });
        return new Response(JSON.stringify({ runId: "run-17", status: "queued" }), {
          status: 200,
          headers: {
            "Content-Type": "application/json",
          },
        });
      }

      if (url === "http://127.0.0.1:4317/v0/test-runs/run-17") {
        return new Response(
          JSON.stringify({
            runId: "run-17",
            status: "completed",
            summary: {
              status: "failed",
              exitCode: 3,
              error: {
                code: "assertion_failed",
                message: "screen.type did not match",
              },
            },
          }),
          {
            status: 200,
            headers: {
              "Content-Type": "application/json",
            },
          },
        );
      }

      throw new Error(`unexpected fetch URL: ${url}`);
    };

    const client = createSts2Client({
      binaryPath: resolve(repoRoot, "target", "debug", "missing-sts2"),
      cwd: repoRoot,
      serviceUrl: "http://127.0.0.1:4317",
      serviceToken: "local-dev-token",
    });

    await assert.rejects(
      () =>
        client.testRun({
          inline: "name: remote-failing-assert\nsteps: [game.info]\n",
          durable: true,
        }),
      (error) => {
        assert.ok(error instanceof Sts2CliError);
        assert.equal(error.exitCode, 3);
        assert.equal(error.payload.status, "failed");
        assert.equal(error.payload.error.code, "assertion_failed");
        return true;
      },
    );
    assert.deepEqual(seenUrls, [
      "http://127.0.0.1:4317/v0/test-runs",
      "http://127.0.0.1:4317/v0/test-runs/run-17",
    ]);
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("createSts2Client preserves the public Sts2CliError contract for service-backed failures", async () => {
  const service = await spawnService();

  try {
    const client = createSts2Client({
      cwd: repoRoot,
      serviceUrl: service.baseUrl,
      serviceToken: service.token,
    });

    await assert.rejects(
      () =>
        client.act({
          kind: "choose",
        }),
      (error) => {
        assert.ok(error instanceof Sts2CliError);
        assert.equal(error.exitCode, 2);
        assert.deepEqual(error.argv, []);
        assert.equal(error.payload?.exitCode, 2);
        assert.equal(error.payload?.error?.code, "invalid_ai_tool_arguments");
        assert.match(error.stdout, /invalid_ai_tool_arguments/);
        assert.equal(error.stderr, "");
        return true;
      },
    );
  } finally {
    await service.close();
  }
});

test("createSts2Client preserves hotReloadStatus service-backed CLI errors", async () => {
  const service = await spawnService();

  try {
    const client = createSts2Client({
      cwd: repoRoot,
      serviceUrl: service.baseUrl,
      serviceToken: service.token,
    });

    await assert.rejects(
      () => client.hotReloadStatus({ project: "./missing-hot-reload-project" }),
      (error) => {
        assert.ok(error instanceof Sts2CliError);
        assert.equal(error.payload?.error?.code, "hot_reload_project_not_found");
        return true;
      },
    );
  } finally {
    await service.close();
  }
});

test("createSts2Client preserves failed testRun error parity when backed by the automation service", async () => {
  const service = await spawnService();

  try {
    const client = createSts2Client({
      cwd: repoRoot,
      serviceUrl: service.baseUrl,
      serviceToken: service.token,
    });

    await assert.rejects(
      () =>
        client.testRun({
          scenario: {
            name: "remote-failing-assert",
            steps: [
              {
                "dev.assert": {
                  path: "screen.type",
                  equals: "combat",
                },
              },
            ],
          },
        }),
      (error) => {
        assert.ok(error instanceof Sts2CliError);
        assert.equal(error.exitCode, 3);
        assert.equal(error.payload?.status, "failed");
        assert.equal(error.payload?.failedScenarioCount, 1);
        assert.equal(error.payload?.scenarios?.[0]?.steps?.[0]?.error?.code, "assertion_failed");
        assert.match(error.stdout, /"status":"failed"/);
        assert.equal(error.stderr, "");
        return true;
      },
    );
  } finally {
    await service.close();
  }
});
