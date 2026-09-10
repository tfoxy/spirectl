import assert from "node:assert/strict";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import test from "node:test";
import { dirname, resolve, join } from "node:path";
import { fileURLToPath } from "node:url";

import { Sts2CliError, createSts2Client } from "../src/index.js";
import { createServiceClient } from "../src/service-client.js";

const testDir = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(testDir, "..", "..");
const fakeBinaryPath = resolve(testDir, "fixtures", "fake-sts2.mjs");
const realBinaryPath = resolve(repoRoot, "target", "debug", process.platform === "win32" ? "sts2.exe" : "sts2");
const mockConfigPath = resolve(repoRoot, "tests", "sts2.mock.yaml");
process.env.STS2_BINARY_PATH = realBinaryPath;

function writeConfig(contents) {
  const dir = mkdtempSync(join(tmpdir(), "sts2-wrapper-"));
  const configPath = join(dir, "sts2.config.yaml");
  writeFileSync(configPath, contents);
  return {
    configPath,
    cleanup() {
      rmSync(dir, { recursive: true, force: true });
    },
  };
}

test("gameInfo returns parsed JSON from the real CLI", async () => {
  const client = createSts2Client({ cwd: repoRoot, configPath: mockConfigPath });
  const info = await client.gameInfo();

  assert.equal(info.repository, "spirectl");
  assert.equal(info.executable, "sts2");
  assert.equal(info.transport.effectiveKind, "mock");
});

// The canonical envelope is `{ schemaVersion, language, rootScene, characterSelect, run }` — the current
// envelope's top-level `screen`/`scene`/`combat`/`actions`/`transportKind` were deleted by design
// (e2aa8028). Transport identity now lives on `game info`; the live action surface on `state actions`.
test("state returns parsed JSON from the real CLI", async () => {
  const client = createSts2Client({ cwd: repoRoot, configPath: mockConfigPath });
  const state = await client.state();

  assert.equal(state.schemaVersion, "spirectl.state/v0");
  assert.equal(state.rootScene, "screens/main_menu");
  assert.equal(state.characterSelect, null);
  assert.equal(state.run, null);
});

// The combat mock scenario projects a real canonical run. Presentation text (card descriptions,
// intent labels, relic counter labels, asset keys) is NOT part of the canonical state — it lives in
// the model/presentation catalog — so this asserts the runtime projection and its stable ids, which
// are the same ids `act play-card` / `act use-potion` accept.
test("state exposes the canonical combat run projection from the real CLI", async () => {
  const temp = writeConfig("transport:\n  kind: mock\n  mockScenario: combat\n");

  try {
    const client = createSts2Client({ cwd: repoRoot, configPath: temp.configPath });
    const state = await client.state();

    assert.equal(state.schemaVersion, "spirectl.state/v0");
    assert.equal(state.rootScene, "run");

    const player = state.run.players[0];
    assert.equal(player.id, "p1");
    assert.equal(player.combat.hand.cards[0].id, "c_1");
    assert.equal(player.combat.hand.cards[0].modelId, "JAB");
    assert.equal(player.combat.energy, 3);
    assert.equal(player.potions[0].modelId, "fire-potion");
    assert.equal(player.potions[0].passesUsabilityCheck, true);
    assert.equal(player.relics[0].modelId, "BURNING_BLOOD");
    assert.equal(player.relics[0].hasCounter, true);
    assert.equal(player.deck.cards[0].modelId, "JAB");

    const enemy = state.run.currentRoom.combat.combatState.enemies[0];
    assert.equal(enemy.id, "e_1");
    assert.equal(enemy.modelId, "JAW_WORM");
    assert.equal(enemy.nextMove.id, "GNASH");
    assert.equal(enemy.nextMove.intents[0].type, "Attack");

    assert.equal(state.run.notices[0].severity, "partial");
    assert.equal(state.run.notices[0].path, "run");
  } finally {
    temp.cleanup();
  }
});

// The live action surface derived from that same state: state ids join straight to executable
// actions (`c_1` -> play-card, potion slot 0 -> use-potion against `e_1`).
test("stateActions derives the live action surface from the combat mock scenario", async () => {
  const temp = writeConfig("transport:\n  kind: mock\n  mockScenario: combat\n");

  try {
    const client = createSts2Client({ cwd: repoRoot, configPath: temp.configPath });
    const { actions } = await client.stateActions();

    const playCard = actions.find((action) => action.kind === "play-card");
    assert.equal(playCard.args.cardId, "c_1");
    assert.equal(playCard.ownerPlayerId, "p1");
    assert.ok(actions.some((action) => action.kind === "end-turn"));
    assert.ok(
      actions.some(
        (action) => action.kind === "use-potion" && action.args.targetId === "e_1",
      ),
    );
  } finally {
    temp.cleanup();
  }
});

test("logs returns parsed JSON from the real CLI", async () => {
  const client = createSts2Client({ cwd: repoRoot, configPath: mockConfigPath });
  const logs = await client.logs({ tail: 2 });

  assert.equal(logs.nextCursor, 3);
  assert.equal(logs.entries.length, 2);
  assert.equal(logs.entries[0].cursor, 2);
  assert.equal(logs.entries[0].target, "bridge.transport");
  assert.equal(logs.entries[1].cursor, 3);
  assert.equal(logs.entries[1].target, "bridge.state");
});

test("logs supports afterCursor passthrough", async () => {
  const client = createSts2Client({ cwd: repoRoot, configPath: mockConfigPath });
  const logs = await client.logs({ afterCursor: 2, limit: 20 });

  assert.equal(logs.nextCursor, 3);
  assert.equal(logs.entries.length, 1);
  assert.equal(logs.entries[0].cursor, 3);
  assert.equal(logs.entries[0].target, "bridge.state");
});

test("logHealth returns parsed JSON from the real CLI", async () => {
  const client = createSts2Client({ cwd: repoRoot, configPath: mockConfigPath });
  const result = await client.logHealth({ tail: 20 });

  assert.equal(result.status, "healthy");
  assert.equal(result.unhealthyEntryCount, 0);
});

test("diagnostics returns parsed JSON and writes a bundle through the real CLI", async () => {
  const bundleRoot = mkdtempSync(join(tmpdir(), "sts2-diagnostics-"));
  const temp = writeConfig("transport:\n  kind: mock\n  mockScenario: combat\n");

  try {
    const client = createSts2Client({
      cwd: repoRoot,
      configPath: temp.configPath,
    });
    const result = await client.diagnostics({ bundleDir: bundleRoot });

    assert.equal(result.status, "partial");
    assert.equal(result.bundle.dir, bundleRoot);
    assert.equal(result.captures.state.status, "captured");
    assert.equal(result.captures.sceneTree.status, "error");
    assert.equal(
      result.bundle.files.diagnostics,
      join(bundleRoot, "diagnostics.json"),
    );
  } finally {
    temp.cleanup();
    rmSync(bundleRoot, { recursive: true, force: true });
  }
});

test("snapshotExport and snapshotCompare work through the real CLI", async () => {
  const baselineRoot = mkdtempSync(join(tmpdir(), "sts2-snapshot-baseline-"));
  const bundleRoot = mkdtempSync(join(tmpdir(), "sts2-snapshot-bundle-"));

  try {
    const client = createSts2Client({ cwd: repoRoot, configPath: mockConfigPath });
    const exported = await client.snapshotExport({
      spec: "tests/snapshots/main-menu.sts2.snapshot.yaml",
      output: baselineRoot,
    });
    assert.equal(exported.status, "exported");
    assert.equal(exported.outputDir, baselineRoot);

    const compared = await client.snapshotCompare({
      spec: "tests/snapshots/main-menu.sts2.snapshot.yaml",
      baseline: baselineRoot,
      bundleDir: bundleRoot,
    });
    assert.equal(compared.matched, true);
    assert.equal(compared.bundle.dir, bundleRoot);
  } finally {
    rmSync(baselineRoot, { recursive: true, force: true });
    rmSync(bundleRoot, { recursive: true, force: true });
  }
});

test("inspectAiTools returns parsed JSON from the real CLI", async () => {
  const client = createSts2Client({ cwd: repoRoot, configPath: mockConfigPath });
  const catalog = await client.inspectAiTools();

  assert.equal(catalog.adapter.name, "sts2-mcp");
  assert.ok(catalog.tools.some((tool) => tool.name === "act"));
  assert.ok(catalog.tools.some((tool) => tool.name === "game_detect"));
  assert.ok(catalog.tools.some((tool) => tool.name === "assets_extract"));
  assert.ok(catalog.tools.some((tool) => tool.name === "skill_install"));
  assert.ok(catalog.tools.some((tool) => tool.name === "toolchain_info"));
  assert.ok(catalog.tools.some((tool) => tool.name === "project_profile_list"));
  assert.ok(catalog.tools.some((tool) => tool.name === "project_hook_run"));
  assert.ok(catalog.tools.some((tool) => tool.name === "inspect_viewport_presets"));
  assert.ok(catalog.tools.some((tool) => tool.name === "inspect_reference_topics"));
  assert.ok(catalog.tools.some((tool) => tool.name === "code_hooks"));
  assert.ok(catalog.tools.some((tool) => tool.name === "code_hook_info"));
  assert.ok(catalog.tools.some((tool) => tool.name === "snapshot_export"));
  assert.ok(catalog.tools.some((tool) => tool.name === "snapshot_compare"));
  assert.ok(catalog.tools.some((tool) => tool.name === "test_stress"));
});

test("testRun returns parsed JSON from the real CLI", async () => {
  const client = createSts2Client({ cwd: repoRoot, configPath: mockConfigPath });
  const result = await client.testRun({
    path: "tests/scenarios/smoke-main-menu.sts2.yaml",
  });

  assert.equal(result.status, "passed");
  assert.equal(result.scenarioCount, 1);
});

test("testStress returns parsed JSON from the real CLI", async () => {
  const client = createSts2Client({ cwd: repoRoot, configPath: mockConfigPath });
  const result = await client.testStress({
    path: "tests/scenarios/smoke-main-menu.sts2.yaml",
    iterations: 2,
  });

  assert.equal(result.status, "passed");
  assert.equal(result.completedIterations, 2);
});

test("gameDetect returns parsed JSON from the real CLI", async () => {
  const client = createSts2Client({ cwd: repoRoot, configPath: mockConfigPath });
  const result = await client.gameDetect();

  assert.ok(["configured", "detected", "missing", "not-detected"].includes(result.status));
  assert.equal(typeof result.notes?.length, "number");
});

test("loadFixture preserves structured recipe report fields", async () => {
  const client = createSts2Client({
    binaryPath: fakeBinaryPath,
    cwd: repoRoot,
    env: {
      ...process.env,
      FAKE_STS2_CASE: "load-fixture-result",
    },
  });

  const result = await client.loadFixture({
    path: "fixtures/multiplayer-ownership-local-only-degraded.sts2.fixture.yaml",
  });

  assert.equal(result.recipeReport.recipeName, "multiplayer-lobby-recipe");
  assert.equal(result.recipeReport.appliedFields[0].fieldPath, "screen");
  assert.equal(
    result.recipeReport.degradedMultiplayerFields[0].reasonCode,
    "local-only-degraded-multiplayer",
  );
  assert.equal(result.recipeReport.bridgeValidation.status, "passed");
  assert.equal(result.loaded.screen.id, "multiplayer-lobby");
});

test("assetsExtract forwards query and root options", async () => {
  const client = createSts2Client({
    binaryPath: fakeBinaryPath,
    cwd: repoRoot,
    env: {
      ...process.env,
      FAKE_STS2_CASE: "echo-argv",
    },
  });
  const result = await client.assetsExtract({
    query: "hand",
    execution: "offline",
    resourcesDir: "/tmp/resources",
  });

  assert.deepEqual(result.argv, [
    "--json",
    "assets",
    "extract",
    "hand",
    "--execution",
    "offline",
    "--resources-dir",
    "/tmp/resources",
  ]);
});

test("asset helpers preserve diagnostic CLI payload fields", async () => {
  const client = createSts2Client({
    binaryPath: fakeBinaryPath,
    cwd: repoRoot,
    env: {
      ...process.env,
      FAKE_STS2_CASE: "asset-diagnostics",
    },
  });

  const explain = await client.assetsExplain({
    query: "encounter:kaiser_crab_boss:scene-package",
    execution: "live",
  });
  assert.equal(explain.explanation.selectorDiagnostics[0].status, "resolved");
  assert.equal(
    explain.explanation.renderTargets[0].decision.affectedPartIds[0],
    "rocket",
  );
  assert.equal(explain.explanation.notices[0].code, "provisional-contract");

  const batch = await client.assetsExtractBatch({
    manifest: "/tmp/asset-manifest.json",
    execution: "live",
  });
  assert.equal(batch.results[0].metadata.source, "test");
  assert.equal(
    batch.results[0].exports[0].notes[0],
    "Rendered encounter virtual target.",
  );
  assert.equal(batch.results[0].exports[0].notices[0].code, "selector-fallback");
  assert.equal(batch.results[1].error.code, "encounter_visual_package_unsupported");
  assert.equal(batch.results[1].error.details.error.details[0].field, "encounterId");
});

test("skillInstall writes the checked-in skill into an explicit directory", async () => {
  const targetDir = mkdtempSync(join(tmpdir(), "sts2-skill-install-"));

  try {
    const client = createSts2Client({ cwd: repoRoot, configPath: mockConfigPath });
    const result = await client.skillInstall({ path: targetDir });

    assert.equal(result.name, "spirectl");
    assert.equal(result.pathSource, "explicit");
    assert.equal(result.skillDir, resolve(targetDir, "spirectl"));
  } finally {
    rmSync(targetDir, { recursive: true, force: true });
  }
});

test("testRun accepts inline scenario objects against the real CLI", async () => {
  const client = createSts2Client({ cwd: repoRoot, configPath: mockConfigPath });
  const result = await client.testRun({
    scenario: {
      name: "smoke-inline-object",
      steps: ["game.info"],
    },
  });

  assert.equal(result.status, "passed");
  assert.equal(result.scenarioCount, 1);
});

test("act supports ready in the lobby mock scenario", async () => {
  const temp = writeConfig("transport:\n  kind: mock\n  mockScenario: lobby\n");

  try {
    const client = createSts2Client({ cwd: repoRoot, configPath: temp.configPath });
    const result = await client.act({ kind: "ready" });

    assert.equal(result.kind, "ready");
    assert.equal(result.accepted, true);
  } finally {
    temp.cleanup();
  }
});

test("act supports unready in the ready lobby mock scenario", async () => {
  const temp = writeConfig("transport:\n  kind: mock\n  mockScenario: lobby-ready\n");

  try {
    const client = createSts2Client({ cwd: repoRoot, configPath: temp.configPath });
    const result = await client.act({ kind: "unready" });

    assert.equal(result.kind, "unready");
    assert.equal(result.accepted, true);
  } finally {
    temp.cleanup();
  }
});

test("act supports select-character in the lobby mock scenario", async () => {
  const temp = writeConfig("transport:\n  kind: mock\n  mockScenario: lobby\n");

  try {
    const client = createSts2Client({ cwd: repoRoot, configPath: temp.configPath });
    const result = await client.act({ kind: "select-character", characterId: "silent" });

    assert.equal(result.kind, "select-character");
    assert.equal(result.accepted, true);
  } finally {
    temp.cleanup();
  }
});

test("act supports select-map-node in the map mock scenario", async () => {
  const temp = writeConfig("transport:\n  kind: mock\n  mockScenario: map\n");

  try {
    const client = createSts2Client({ cwd: repoRoot, configPath: temp.configPath });
    const result = await client.act({
      kind: "select-map-node",
      mapNodeId: "map-node:3:1",
    });

    assert.equal(result.kind, "select-map-node");
    assert.equal(result.accepted, true);
  } finally {
    temp.cleanup();
  }
});

test("act accepts current preferredAction references for CLI execution", async () => {
  const client = createSts2Client({
    binaryPath: fakeBinaryPath,
    cwd: repoRoot,
    env: {
      ...process.env,
      FAKE_STS2_CASE: "echo-argv",
    },
  });
  const result = await client.act({
    preferredAction: {
      kind: "select-map-node",
      ownerPlayerId: "p1",
      arguments: {
        mapNodeId: "map-node:3:1",
      },
    },
  });

  assert.deepEqual(result.argv, [
    "--json",
    "act",
    "select-map-node",
    "--node",
    "map-node:3:1",
    "--player-id",
    "p1",
  ]);
});

test("act preserves explicit playerId over preferredAction owner metadata", async () => {
  const client = createSts2Client({
    binaryPath: fakeBinaryPath,
    cwd: repoRoot,
    env: {
      ...process.env,
      FAKE_STS2_CASE: "echo-argv",
    },
  });
  const result = await client.act({
    playerId: "p2",
    preferredActionRef: {
      kind: "select-map-node",
      ownerPlayerId: "p1",
      arguments: {
        mapNodeId: "map-node:3:1",
        playerId: "p1",
      },
    },
  });

  assert.deepEqual(result.argv, [
    "--json",
    "act",
    "select-map-node",
    "--node",
    "map-node:3:1",
    "--player-id",
    "p2",
  ]);
});

test("act accepts non-combat preferredAction references for CLI execution", async () => {
  const client = createSts2Client({
    binaryPath: fakeBinaryPath,
    cwd: repoRoot,
    env: {
      ...process.env,
      FAKE_STS2_CASE: "echo-argv",
    },
  });
  const result = await client.act({
    preferredActionRef: {
      kind: "buy-card",
      ownerPlayerId: "p1",
      arguments: {
        shopItemId: "shop:p1:card:strike:0",
      },
    },
  });

  assert.deepEqual(result.argv, [
    "--json",
    "act",
    "buy-card",
    "--shop-item",
    "shop:p1:card:strike:0",
    "--player-id",
    "p1",
  ]);
});

test("act accepts typed arguments for CLI execution", async () => {
  const client = createSts2Client({
    binaryPath: fakeBinaryPath,
    cwd: repoRoot,
    env: {
      ...process.env,
      FAKE_STS2_CASE: "echo-argv",
    },
  });
  const result = await client.act({
    kind: "play-card",
    arguments: {
      cardId: "card:p1:strike:0",
      targetId: "enemy:jaw-worm:0",
      playerId: "p1",
    },
  });

  assert.deepEqual(result.argv, [
    "--json",
    "act",
    "play-card",
    "--card",
    "card:p1:strike:0",
    "--target",
    "enemy:jaw-worm:0",
    "--player-id",
    "p1",
  ]);
});

test("act forwards S86 action arguments for CLI execution", async () => {
  const client = createSts2Client({
    binaryPath: fakeBinaryPath,
    cwd: repoRoot,
    env: {
      ...process.env,
      FAKE_STS2_CASE: "echo-argv",
    },
  });

  assert.deepEqual(
    (await client.act({ kind: "claim-reward", rewardId: "reward:p1:0", playerId: "p1" })).argv,
    ["--json", "act", "claim-reward", "--reward", "reward:p1:0", "--player-id", "p1"],
  );
  assert.deepEqual(
    (await client.act({ kind: "select-bundle", bundleId: "bundle:offensive-pack" })).argv,
    ["--json", "act", "select-bundle", "--bundle", "bundle:offensive-pack"],
  );
  assert.deepEqual(
    (await client.act({ kind: "use-rest-site-option", restOptionId: "heal" })).argv,
    ["--json", "act", "use-rest-site-option", "--rest-option", "heal"],
  );
  assert.deepEqual(
    (await client.act({ kind: "select-event-option", eventOptionId: "event-room:gain-gold:0" })).argv,
    ["--json", "act", "select-event-option", "--event-option", "event-room:gain-gold:0"],
  );
  assert.deepEqual(
    (await client.act({ kind: "use-crystal-sphere-control", controlId: "crystal-sphere:tool:big" })).argv,
    ["--json", "act", "use-crystal-sphere-control", "--control", "crystal-sphere:tool:big"],
  );
});

test("act supports claim-reward in the rewards mock scenario", async () => {
  const temp = writeConfig("transport:\n  kind: mock\n  mockScenario: rewards\n");

  try {
    const client = createSts2Client({ cwd: repoRoot, configPath: temp.configPath });
    const result = await client.act({ kind: "claim-reward", rewardId: "reward:p1:0" });

    assert.equal(result.kind, "claim-reward");
    assert.equal(result.accepted, true);
  } finally {
    temp.cleanup();
  }
});

test("act supports confirm-selection in the card-selection mock scenario", async () => {
  const temp = writeConfig("transport:\n  kind: mock\n  mockScenario: card-selection\n");

  try {
    const client = createSts2Client({ cwd: repoRoot, configPath: temp.configPath });
    const result = await client.act({ kind: "confirm-selection" });

    assert.equal(result.kind, "confirm-selection");
    assert.equal(result.accepted, true);
  } finally {
    temp.cleanup();
  }
});

test("act supports cancel-selection in the card-selection mock scenario", async () => {
  const temp = writeConfig("transport:\n  kind: mock\n  mockScenario: card-selection\n");

  try {
    const client = createSts2Client({ cwd: repoRoot, configPath: temp.configPath });
    const result = await client.act({ kind: "cancel-selection" });

    assert.equal(result.kind, "cancel-selection");
    assert.equal(result.accepted, true);
  } finally {
    temp.cleanup();
  }
});

test("act supports use-potion in the combat mock scenario", async () => {
  const temp = writeConfig("transport:\n  kind: mock\n  mockScenario: combat\n");

  try {
    const client = createSts2Client({ cwd: repoRoot, configPath: temp.configPath });
    const result = await client.act({
      kind: "use-potion",
      potionId: "potion:p1:0:fire-potion",
      targetId: "e_1",
    });

    assert.equal(result.kind, "use-potion");
    assert.equal(result.accepted, true);
  } finally {
    temp.cleanup();
  }
});

test("state exposes M35 ownership and multiplayer role metadata in the combat mock scenario", async () => {
  const temp = writeConfig("transport:\n  kind: mock\n  mockScenario: combat\n");

  try {
    const client = createSts2Client({ cwd: repoRoot, configPath: temp.configPath });
    const state = await client.state();

    // Canonically, ownership is structural: a potion/card/relic sits under the player that owns it.
    const [local, remote] = state.run.players;
    assert.equal(local.id, "p1");
    assert.equal(local.isLocal, true);
    assert.equal(local.isHost, true);
    assert.equal(local.isRemote, false);
    assert.equal(remote.isLocal, false);
    assert.equal(remote.isHost, false);
    assert.equal(remote.isRemote, true);
    assert.equal(local.potions[0].modelId, "fire-potion");

    // Derived actions still carry an explicit ownerPlayerId (the M35 contract).
    const { actions } = await client.stateActions();
    assert.equal(actions.find((action) => action.kind === "play-card").ownerPlayerId, "p1");
  } finally {
    temp.cleanup();
  }
});

test("state preserves typed non-combat fields from CLI JSON", async () => {
  const client = createSts2Client({
    binaryPath: resolve(testDir, "fixtures", "fake-sts2.mjs"),
    cwd: repoRoot,
    env: {
      ...process.env,
      FAKE_STS2_CASE: "state",
    },
  });
  const state = await client.state();

  assert.equal(state.screen?.id, "bundle-selection");
  assert.equal(state.localPlayerId, "p1");
  assert.equal(state.hostPlayerId, "p1");
  assert.equal(state.localRole, "host");
  assert.equal(state.remoteOrchestrationCapability, "local-only-degraded");
  assert.equal(state.map.nodes[0].id, "map-node:3:1");
  assert.equal(state.map.nodes[0].playerId, "p1");
  assert.equal(state.map.nodes[0].remoteOrchestrationCapability, "local-only-degraded");
  assert.equal(state.eventRoom.page.description.text, "You see a [yellow]golden idol[/yellow].");
  assert.equal(state.eventRoom.page.description.rawText, "You see a {0}.");
  assert.equal(state.eventRoom.page.sharedLabel.text, "-- [wave]Offer[/wave] --");
  assert.equal(state.eventRoom.page.ancient.bannerTitle.text, "[ancient_banner]NEOW[/ancient_banner]");
  assert.equal(state.eventRoom.page.ancient.epithet.text, "Madre de la resurrección (desterrada)");
  assert.equal(state.eventRoom.page.ancient.currentDialogue.text, "[sine]... Hola ... de nuevo ... [/sine]");
  assert.equal(state.eventRoom.page.ancient.dialogueLines[0].speaker, "Neow");
  assert.equal(state.eventRoom.options[0].id, "event-room:gain-gold:0");
  assert.equal(state.eventRoom.options[0].description, "Gain [yellow]75[/yellow] Gold.");
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
  assert.notEqual(state.bundleSelection, null);
  assert.equal(state.bundleSelection.bundles[0].id, "bundle:offensive-pack");
  assert.equal(state.bundleSelection.bundles[0].ownerPlayerId, "p1");
  assert.equal(state.bundleSelection.bundles[0].playerId, "p1");
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
  assert.equal(state.multiplayerLobby.actions[1].ownerPlayerId, "p2");
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
  assert.equal(state.cardOverlay.previewText, "Whenever you play an Attack this turn, gain 5 Block.");
  assert.equal(state.cardOverlay.breadcrumbs[0].screenType, "bundle-selection");
  assert.equal(state.cardOverlay.breadcrumbs[0].ownerPlayerId, "p1");
  assert.equal(state.cardOverlay.sourceBreadcrumbs[0].source, "underlying-screen");
  assert.equal(state.cardOverlay.close.intentKind, "close-overlay");
  assert.equal(state.cardOverlay.back.enabled, false);
  assert.equal(state.cardOverlay.followThroughControls[0].preferredAction, "choose");
  assert.equal(state.cardOverlay.notices[0].severity, "partial");
  assert.equal(state.cardOverlay.metadata.noticeCode, "card-overlay-partial");
  assert.equal(state.choices[0].choiceKind, "bundle");
  assert.equal(state.choices[0].intentKind, "choose-bundle");
  assert.equal(state.choices[0].preferredAction, "select-bundle");
  assert.equal(state.choices[0].preferredActionRef.kind, "select-bundle");
  assert.equal(state.choices[1].choiceKind, "overlay-close");
  assert.equal(state.choices[1].metadata.overlayPolicy, "blocking-overlay");
  assert.equal(state.availableActions[0].ownerPlayerId, "p1");
  assert.equal(state.availableActions[0].perspective, "local:p1");
  assert.equal(state.availableActions[0].preferredAction, "select-bundle");
  assert.equal(state.availableActions[1].intentKind, "close-overlay");
  assert.equal(state.availableActions[1].metadata.overlayPolicy, "blocking-overlay");
  assert.equal(state.notices[0].path, "bundleSelection.bundles");
  assert.equal(state.notices[0].stability, "stable");
  assert.equal(state.notices[1].path, "cardOverlay");
  assert.equal(state.notices[1].severity, "partial");
  assert.equal(state.notices[2].code, "card-overlay-passive");
});

test("act supports bundle-selection select-bundle in the mock scenario", async () => {
  const temp = writeConfig("transport:\n  kind: mock\n  mockScenario: bundle-selection\n");

  try {
    const client = createSts2Client({ cwd: repoRoot, configPath: temp.configPath });
    const state = await client.state();

    assert.equal(state.schemaVersion, "spirectl.state/v0");
    // Canonically a bundle selection is an OVERLAY on the local player, not a top-level `scene`.
    const overlay = state.run.players[0].overlays[0];
    assert.equal(overlay.screenId, "Screens.CardSelection.NChooseABundleSelectionScreen");
    assert.equal(overlay.screenType, "CardSelection");
    assert.equal(overlay.bundleSelection.bundles[0].id, "card-selection:bundle:offensive-pack:0");
    assert.ok(overlay.bundleSelection.bundles[0].cards.length > 0);
    assert.equal(Array.isArray(state.run.notices), true);
    assert.ok(["undefined", "string"].includes(typeof state.run.notices[0]?.severity));
    assert.ok(["undefined", "string"].includes(typeof state.run.notices[0]?.source));

    const { actions } = await client.stateActions();
    const selectBundle = actions.find(
      (action) => action.args.bundleId === "card-selection:bundle:offensive-pack:0",
    );
    assert.equal(selectBundle.kind, "select-bundle");
    assert.equal(selectBundle.ownerPlayerId, "p1");
    assert.ok(selectBundle.sourcePath.includes("bundleSelection.bundles[0]"));

    const result = await client.act({
      kind: "select-bundle",
      bundleId: "card-selection:bundle:offensive-pack:0",
    });

    assert.equal(result.kind, "select-bundle");
    assert.equal(result.accepted, true);
  } finally {
    temp.cleanup();
  }
});

test("act supports skip-rewards in the rewards mock scenario", async () => {
  const temp = writeConfig("transport:\n  kind: mock\n  mockScenario: rewards\n");

  try {
    const client = createSts2Client({ cwd: repoRoot, configPath: temp.configPath });
    const result = await client.act({ kind: "skip-rewards" });

    assert.equal(result.kind, "skip-rewards");
    assert.equal(result.accepted, true);
  } finally {
    temp.cleanup();
  }
});

test("act supports buy-card in the shop mock scenario", async () => {
  const temp = writeConfig("transport:\n  kind: mock\n  mockScenario: shop\n");

  try {
    const client = createSts2Client({ cwd: repoRoot, configPath: temp.configPath });
    const result = await client.act({ kind: "buy-card", shopItemId: "shop:p1:card:strike:0" });

    assert.equal(result.kind, "buy-card");
    assert.equal(result.accepted, true);
  } finally {
    temp.cleanup();
  }
});

test("act supports dangerous mouse-click in the main-menu mock scenario", async () => {
  const temp = writeConfig("transport:\n  kind: mock\n  mockScenario: main-menu\n");

  try {
    const client = createSts2Client({
      cwd: repoRoot,
      configPath: temp.configPath,
      mode: "dangerous",
    });
    const result = await client.act({
      kind: "mouse-click",
      x: 100,
      y: 200,
      button: "left",
    });

    assert.equal(result.kind, "mouse-click");
    assert.equal(result.accepted, true);
    assert.equal(result.provisional, true);
  } finally {
    temp.cleanup();
  }
});

test("assert rejects with a structured CLI error when the predicate fails", async () => {
  const client = createSts2Client({ cwd: repoRoot, configPath: mockConfigPath });

  await assert.rejects(
    () =>
      client.assert({
        path: "screen.type",
        contains: "combat",
      }),
    (error) => {
      assert.ok(error instanceof Sts2CliError);
      assert.equal(error.exitCode, 3);
      assert.equal(error.payload?.error?.code, "assertion_failed");
      assert.equal(error.payload?.error?.path, "screen.type");
      assert.match(error.stdout, /assertion_failed/);
      return true;
    },
  );
});

test("waitFor rejects with a structured CLI error when the wait times out", async () => {
  const client = createSts2Client({ cwd: repoRoot, configPath: mockConfigPath });

  await assert.rejects(
    () =>
      client.waitFor({
        path: "screen.type",
        equals: "combat",
        timeoutMs: 5,
        intervalMs: 1,
      }),
    (error) => {
      assert.ok(error instanceof Sts2CliError);
      assert.equal(error.exitCode, 3);
      assert.equal(error.payload?.error?.code, "wait_timeout");
      assert.equal(error.payload?.error?.path, "screen.type");
      assert.match(error.stdout, /wait_timeout/);
      return true;
    },
  );
});

test("the wrapper forces --json and returns parsed payloads for fake executables", async () => {
  const client = createSts2Client({
    binaryPath: fakeBinaryPath,
    cwd: repoRoot,
    env: {
      ...process.env,
      FAKE_STS2_CASE: "echo-argv",
    },
  });

  const payload = await client.gameInfo();

  assert.deepEqual(payload.argv, ["--json", "game", "info"]);
});

test("hotReload helpers shape CLI argv", async () => {
  const client = createSts2Client({
    binaryPath: fakeBinaryPath,
    cwd: repoRoot,
    env: {
      ...process.env,
      FAKE_STS2_CASE: "echo-argv",
    },
  });

  const status = await client.hotReloadStatus({ project: "./mods/MyHotMod" });
  assert.deepEqual(status.argv, [
    "--json",
    "dev",
    "mod-reload",
    "status",
    "--project",
    "./mods/MyHotMod",
  ]);

  const reload = await client.hotReload({
    project: "./mods/MyHotMod",
    build: true,
    wait: true,
    timeoutMs: 30_000,
    intervalMs: 250,
  });
  assert.deepEqual(reload.argv, [
    "--json",
    "dev",
    "mod-reload",
    "--project",
    "./mods/MyHotMod",
    "--build",
    "--wait",
    "--timeout-ms",
    "30000",
    "--interval-ms",
    "250",
  ]);
});

test("devConsole shapes CLI argv and validates mode", async () => {
  const client = createSts2Client({
    binaryPath: fakeBinaryPath,
    cwd: repoRoot,
    env: {
      ...process.env,
      FAKE_STS2_CASE: "echo-argv",
    },
  });

  const help = await client.devConsole({ command: "help", args: ["draw"] });
  assert.deepEqual(help.argv, [
    "--json",
    "dev",
    "console",
    "help",
    "draw",
  ]);

  const achievement = await client.devConsole({
    command: "achievement",
    mode: "dangerous",
  });
  assert.deepEqual(achievement.argv, [
    "--json",
    "--mode",
    "dangerous",
    "dev",
    "console",
    "achievement",
  ]);

  await assert.rejects(
    () => client.devConsole({ command: "help", mode: "normal" }),
    /devConsole mode must be 'dangerous' when supplied/,
  );
});

test("the wrapper assembles inspectAiTools and testRun argv for fake executables", async () => {
  const client = createSts2Client({
    binaryPath: fakeBinaryPath,
    cwd: repoRoot,
    env: {
      ...process.env,
      FAKE_STS2_CASE: "echo-argv",
    },
  });

  const inspectPayload = await client.inspectAiTools();
  assert.deepEqual(inspectPayload.argv, ["--json", "inspect", "ai-tools"]);

  const detectPayload = await client.gameDetect();
  assert.deepEqual(detectPayload.argv, ["--json", "game", "detect"]);

  const stateActionsPayload = await client.stateActions({ perspective: "local", playerId: "p2" });
  assert.deepEqual(stateActionsPayload.argv, [
    "--json",
    "state",
    "actions",
    "--perspective",
    "local",
    "--player-id",
    "p2",
  ]);

  const runPayload = await client.testRun({
    path: "tests/scenarios/smoke-main-menu.sts2.yaml",
    profile: "mock",
    tags: ["smoke"],
    failureArtifacts: "never",
  });
  assert.deepEqual(runPayload.argv, [
    "--json",
    "test",
    "run",
    "--failure-artifacts",
    "never",
    "--profile",
    "mock",
    "--tag",
    "smoke",
    "tests/scenarios/smoke-main-menu.sts2.yaml",
  ]);

  const profilePayload = await client.testRun({
    profile: "mock",
  });
  assert.deepEqual(profilePayload.argv, ["--json", "test", "run", "--profile", "mock"]);

  const inlinePayload = await client.testRun({
    scenario: {
      name: "fake-inline",
      steps: ["game.info"],
    },
    artifactsDir: "tmp/artifacts",
  });
  assert.deepEqual(inlinePayload.argv, [
    "--json",
    "test",
    "run",
    "--artifacts-dir",
    "tmp/artifacts",
    "--inline",
    '{"name":"fake-inline","steps":["game.info"]}',
  ]);

  const deployInlinePayload = await client.testRun({
    scenario: {
      name: "deploy-inline",
      steps: [
        {
          "game.deploy": {
            path: "./mods/MyMod",
            build: true,
            restart: true,
            verify: true,
          },
        },
      ],
    },
  });
  assert.deepEqual(deployInlinePayload.argv, [
    "--json",
    "test",
    "run",
    "--inline",
    '{"name":"deploy-inline","steps":[{"game.deploy":{"path":"./mods/MyMod","build":true,"restart":true,"verify":true}}]}',
  ]);

  const decompilePayload = await client.codeDecompile({
    subject: "type",
    query: "MegaCrit.Sts2.CardState",
    full: true,
  });
  assert.deepEqual(decompilePayload.argv, [
    "--json",
    "code",
    "decompile",
    "type",
    "MegaCrit.Sts2.CardState",
    "--full",
  ]);

  const loadEventRoomFixturePayload = await client.loadFixture({
    path: "fixtures/basic-event-room.sts2.fixture.yaml",
  });
  assert.deepEqual(loadEventRoomFixturePayload.argv, [
    "--json",
    "dev",
    "fixture",
    "load",
    "--path",
    "fixtures/basic-event-room.sts2.fixture.yaml",
  ]);

  const loadBundleSelectionFixturePayload = await client.loadFixture({
    path: "fixtures/basic-bundle-selection.sts2.fixture.yaml",
  });
  assert.deepEqual(loadBundleSelectionFixturePayload.argv, [
    "--json",
    "dev",
    "fixture",
    "load",
    "--path",
    "fixtures/basic-bundle-selection.sts2.fixture.yaml",
  ]);

  const scenarioExportPayload = await client.scenarioExport({
    output: "tests/scenarios/repros/shop-anchor.sts2.scenario.yaml",
    includeExact: true,
  });
  assert.deepEqual(scenarioExportPayload.argv, [
    "--json",
    "dev",
    "scenario",
    "export",
    "--output",
    "tests/scenarios/repros/shop-anchor.sts2.scenario.yaml",
    "--include-exact",
  ]);

  const scenarioLoadPayload = await client.scenarioLoad({
    path: "tests/scenarios/repros/shop-anchor.sts2.scenario.yaml",
    restart: true,
    timeoutMs: 30_000,
    intervalMs: 250,
    allowDegradedLocalMultiplayer: true,
  });
  assert.deepEqual(scenarioLoadPayload.argv, [
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

  const debugStatusPayload = await client.debugStatus();
  assert.deepEqual(debugStatusPayload.argv, [
    "--json",
    "dev",
    "debug",
    "status",
  ]);

  const debugSessionStartPayload = await client.debugSessionStart({
    name: "m45",
    role: "observer",
    pause: true,
    leaseTimeoutMs: 9000,
  });
  assert.deepEqual(debugSessionStartPayload.argv, [
    "--json",
    "dev",
    "debug",
    "session",
    "start",
    "--name",
    "m45",
    "--role",
    "observer",
    "--pause",
    "--lease-timeout-ms",
    "9000",
  ]);

  const debugEventsPayload = await client.debugEvents({
    sessionId: "dbg:observer",
    fromSequence: 42,
    limit: 50,
    follow: true,
    timeoutMs: 1000,
  });
  assert.deepEqual(debugEventsPayload.argv, [
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

  const debugPausePayload = await client.debugPause();
  assert.deepEqual(debugPausePayload.argv, [
    "--json",
    "dev",
    "debug",
    "pause",
  ]);

  const debugStepPayload = await client.debugStep({
    kind: "action",
    count: 2,
  });
  assert.deepEqual(debugStepPayload.argv, [
    "--json",
    "dev",
    "debug",
    "step",
    "--kind",
    "action",
    "--count",
    "2",
  ]);

  const breakpointAddPayload = await client.breakpointAdd({
    path: "screen.type",
    equals: "\"main-menu\"",
    name: "menu-break",
  });
  assert.deepEqual(breakpointAddPayload.argv, [
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

  const breakpointListPayload = await client.breakpointList();
  assert.deepEqual(breakpointListPayload.argv, [
    "--json",
    "dev",
    "breakpoint",
    "list",
  ]);

  const breakpointRemovePayload = await client.breakpointRemove({ id: "bp:1" });
  assert.deepEqual(breakpointRemovePayload.argv, [
    "--json",
    "dev",
    "breakpoint",
    "remove",
    "--id",
    "bp:1",
  ]);

  const changeBreakpointPayload = await client.breakpointAdd({
    session: "dbg:1",
    path: "screen.type",
    kind: "change",
    minHitCount: 2,
    autoRemoveOnHit: true,
  });
  assert.deepEqual(changeBreakpointPayload.argv, [
    "--json",
    "dev",
    "breakpoint",
    "add",
    "--path",
    "screen.type",
    "--session",
    "dbg:1",
    "--kind",
    "change",
    "--min-hit-count",
    "2",
    "--auto-remove-on-hit",
  ]);

  const httpPayload = await client.http({
    url: "http://127.0.0.1:3000/health",
    expectStatus: 200,
    query: "json.status",
    equals: "ok",
  });
  assert.deepEqual(httpPayload.argv, [
    "--json",
    "dev",
    "http",
    "--url",
    "http://127.0.0.1:3000/health",
    "--expect-status",
    "200",
    "--query",
    "json.status",
    "--equals",
    "ok",
  ]);

  const httpWaitPayload = await client.httpWait({
    url: "http://127.0.0.1:3000/health",
    expectStatus: 200,
    timeoutMs: 10000,
    intervalMs: 250,
  });
  assert.deepEqual(httpWaitPayload.argv, [
    "--json",
    "dev",
    "http-wait",
    "--url",
    "http://127.0.0.1:3000/health",
    "--timeout-ms",
    "10000",
    "--expect-status",
    "200",
    "--interval-ms",
    "250",
  ]);

  const fetchPayload = await client.fetch({
    source: "./fixtures/health.json",
    output: "./.sts2/artifacts/health.json",
    query: "json.status",
    equals: "ok",
  });
  assert.deepEqual(fetchPayload.argv, [
    "--json",
    "dev",
    "fetch",
    "--source",
    "./fixtures/health.json",
    "--output",
    "./.sts2/artifacts/health.json",
    "--query",
    "json.status",
    "--equals",
    "ok",
  ]);

  const websocketPayload = await client.websocket({
    url: "ws://127.0.0.1:3001/events",
    sendText: ["ping"],
    expectText: ["pong"],
    timeoutMs: 5000,
  });
  assert.deepEqual(websocketPayload.argv, [
    "--json",
    "dev",
    "websocket",
    "--url",
    "ws://127.0.0.1:3001/events",
    "--send-text",
    "ping",
    "--expect-text",
    "pong",
    "--timeout-ms",
    "5000",
  ]);

  const hookListPayload = await client.projectHookList();
  assert.deepEqual(hookListPayload.argv, ["--json", "project", "hook", "list"]);

  const hookShowPayload = await client.projectHookShow({ name: "repo-check" });
  assert.deepEqual(hookShowPayload.argv, [
    "--json",
    "project",
    "hook",
    "show",
    "repo-check",
  ]);

  const hookRunPayload = await client.projectHookRun({
    name: "repo-check",
    input: { kind: "smoke" },
  });
  assert.deepEqual(hookRunPayload.argv, [
    "--json",
    "project",
    "hook",
    "run",
    "repo-check",
    "--input",
    '{"kind":"smoke"}',
  ]);

  const toolchainInfoPayload = await client.toolchainInfo();
  assert.deepEqual(toolchainInfoPayload.argv, ["--json", "toolchain", "info"]);

  const profileListPayload = await client.projectProfileList();
  assert.deepEqual(profileListPayload.argv, [
    "--json",
    "project",
    "profile",
    "list",
  ]);

  const profileShowPayload = await client.projectProfileShow({ name: "install-bridge" });
  assert.deepEqual(profileShowPayload.argv, [
    "--json",
    "project",
    "profile",
    "show",
    "install-bridge",
  ]);

  const profileRunPayload = await client.projectProfileRun({ name: "install-bridge" });
  assert.deepEqual(profileRunPayload.argv, [
    "--json",
    "project",
    "profile",
    "run",
    "install-bridge",
  ]);

  const viewportPresetPayload = await client.inspectViewportPresets({
    presetCatalogs: ["tests/visual/sample-catalog.sts2.viewport-presets.yaml"],
  });
  assert.deepEqual(viewportPresetPayload.argv, [
    "--json",
    "inspect",
    "viewport-presets",
    "--preset-catalog",
    "tests/visual/sample-catalog.sts2.viewport-presets.yaml",
  ]);

  const referenceTopicsPayload = await client.inspectReferenceTopics();
  assert.deepEqual(referenceTopicsPayload.argv, [
    "--json",
    "inspect",
    "reference-topics",
  ]);

  const hooksPayload = await client.codeHooks({
    query: "OnDeckChanged",
    limit: 6,
    offset: 2,
    source: "game",
    form: ["managed-prefix"],
    hasScript: true,
    sort: "reference-count",
    assembliesDir: "/tmp/assemblies",
    resourcesDir: "/tmp/resources",
  });
  assert.deepEqual(hooksPayload.argv, [
    "--json",
    "code",
    "hooks",
    "OnDeckChanged",
    "--limit",
    "6",
    "--offset",
    "2",
    "--source",
    "game",
    "--form",
    "managed-prefix",
    "--has-script",
    "--sort",
    "reference-count",
    "--assemblies-dir",
    "/tmp/assemblies",
    "--resources-dir",
    "/tmp/resources",
  ]);

  const hookInfoPayload = await client.codeHookInfo({
    query: "Namespace.Type::Method(System.String)",
    assembliesDir: "/tmp/assemblies",
  });
  assert.deepEqual(hookInfoPayload.argv, [
    "--json",
    "code",
    "hook-info",
    "Namespace.Type::Method(System.String)",
    "--assemblies-dir",
    "/tmp/assemblies",
  ]);

  const screenshotPayload = await client.screenshot({
    preset: "desktop-1080p",
    presetCatalogs: ["tests/visual/sample-catalog.sts2.viewport-presets.yaml"],
    rpcTimeoutMs: 750,
    output: "tmp/artifacts/runtime.png",
  });
  assert.deepEqual(screenshotPayload.argv, [
    "--json",
    "dev",
    "screenshot",
    "--preset",
    "desktop-1080p",
    "--preset-catalog",
    "tests/visual/sample-catalog.sts2.viewport-presets.yaml",
    "--rpc-timeout-ms",
    "750",
    "--output",
    "tmp/artifacts/runtime.png",
  ]);

  const screenshotDefaultPayload = await client.screenshot();
  assert.deepEqual(screenshotDefaultPayload.argv, ["--json", "dev", "screenshot"]);

  const screenshotDiffPayload = await client.screenshotDiff({
    baseline: "tests/scenarios/baselines/mock-main-menu.png",
    actual: "tests/scenarios/baselines/mock-main-menu.png",
    bundleDir: "tmp/artifacts/visual-main-menu",
    presetCatalogs: ["tests/visual/sample-catalog.sts2.viewport-presets.yaml"],
    rpcTimeoutMs: 800,
  });
  assert.deepEqual(screenshotDiffPayload.argv, [
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
    "--bundle-dir",
    "tmp/artifacts/visual-main-menu",
  ]);

  const snapshotExportPayload = await client.snapshotExport({
    spec: "tests/snapshots/main-menu.sts2.snapshot.yaml",
    output: "tmp/artifacts/snapshots/main-menu",
    presetCatalogs: ["tests/visual/sample-catalog.sts2.viewport-presets.yaml"],
  });
  assert.deepEqual(snapshotExportPayload.argv, [
    "--json",
    "dev",
    "snapshot",
    "export",
    "--spec",
    "tests/snapshots/main-menu.sts2.snapshot.yaml",
    "--output",
    "tmp/artifacts/snapshots/main-menu",
    "--preset-catalog",
    "tests/visual/sample-catalog.sts2.viewport-presets.yaml",
  ]);

  const snapshotComparePayload = await client.snapshotCompare({
    spec: "tests/snapshots/main-menu.sts2.snapshot.yaml",
    baseline: "tests/snapshots/baselines/main-menu",
    bundleDir: "tmp/artifacts/snapshots/main-menu",
    presetCatalogs: ["tests/visual/sample-catalog.sts2.viewport-presets.yaml"],
  });
  assert.deepEqual(snapshotComparePayload.argv, [
    "--json",
    "dev",
    "snapshot",
    "compare",
    "--spec",
    "tests/snapshots/main-menu.sts2.snapshot.yaml",
    "--baseline",
    "tests/snapshots/baselines/main-menu",
    "--preset-catalog",
    "tests/visual/sample-catalog.sts2.viewport-presets.yaml",
    "--bundle-dir",
    "tmp/artifacts/snapshots/main-menu",
  ]);

  const testStressPayload = await client.testStress({
    path: "tests/scenarios/regression-main-menu.sts2.yaml",
    iterations: 4,
    maxFailures: 1,
    cooldownMs: 25,
  });
  assert.deepEqual(testStressPayload.argv, [
    "--json",
    "test",
    "stress",
    "--iterations",
    "4",
    "--max-failures",
    "1",
    "--cooldown-ms",
    "25",
    "tests/scenarios/regression-main-menu.sts2.yaml",
  ]);

  const assetsExtractPayload = await client.assetsExtract({
    query: "hand",
    execution: "offline",
    format: "auto",
    resourcesDir: "/tmp/resources",
    includeMods: true,
  });
  assert.deepEqual(assetsExtractPayload.argv, [
    "--json",
    "assets",
    "extract",
    "hand",
    "--execution",
    "offline",
    "--format",
    "auto",
    "--resources-dir",
    "/tmp/resources",
    "--include-mods",
  ]);

  const encounterAssetsExtractPayload = await client.assetsExtract({
    query: "encounter:kaiser_crab_boss:visual-part:rocket:state:rocket-charge-up:image",
    execution: "live",
    format: "png",
  });
  assert.deepEqual(encounterAssetsExtractPayload.argv, [
    "--json",
    "assets",
    "extract",
    "encounter:kaiser_crab_boss:visual-part:rocket:state:rocket-charge-up:image",
    "--execution",
    "live",
    "--format",
    "png",
  ]);

  const assetsExplainPayload = await client.assetsExplain({
    query: "combat-background:overgrowth:image",
    execution: "live",
    resourcesDir: "/tmp/resources",
    includeMods: true,
  });
  assert.deepEqual(assetsExplainPayload.argv, [
    "--json",
    "assets",
    "explain",
    "combat-background:overgrowth:image",
    "--execution",
    "live",
    "--resources-dir",
    "/tmp/resources",
    "--include-mods",
  ]);

  const encounterAssetsExplainPayload = await client.assetsExplain({
    query: "encounter:kaiser_crab_boss:scene-package",
    execution: "live",
  });
  assert.deepEqual(encounterAssetsExplainPayload.argv, [
    "--json",
    "assets",
    "explain",
    "encounter:kaiser_crab_boss:scene-package",
    "--execution",
    "live",
  ]);

  const assetsExtractBatchPayload = await client.assetsExtractBatch({
    manifest: "/tmp/asset-manifest.json",
    output: "/tmp/assets",
    execution: "offline",
    format: "auto",
    resourcesDir: "/tmp/resources",
    includeMods: true,
    failFast: true,
  });
  assert.deepEqual(assetsExtractBatchPayload.argv, [
    "--json",
    "assets",
    "extract-batch",
    "--manifest",
    "/tmp/asset-manifest.json",
    "--output",
    "/tmp/assets",
    "--execution",
    "offline",
    "--format",
    "auto",
    "--fail-fast",
    "--resources-dir",
    "/tmp/resources",
    "--include-mods",
  ]);

  const skillInstallPayload = await client.skillInstall({
    path: "/tmp/skills",
  });
  assert.deepEqual(skillInstallPayload.argv, [
    "--json",
    "skill",
    "install",
    "--path",
    "/tmp/skills",
  ]);
});

test("testRun rejects missing or conflicting runner input sources", async () => {
  const client = createSts2Client({ cwd: repoRoot });

  await assert.rejects(
    () => client.testRun({}),
    /testRun requires exactly one input source: path, inline, scenario, or profile\./,
  );

  await assert.rejects(
    () =>
      client.testRun({
        path: "tests/scenarios/smoke-main-menu.sts2.yaml",
        inline: "name: bad\nsteps:\n  - game.info\n",
      }),
    /testRun requires exactly one input source: path, inline, scenario, or profile\./,
  );
});

test("testRun keeps local argv and service bodies aligned across input sources", async () => {
  const originalFetch = globalThis.fetch;
  const requests = [];
  const local = createSts2Client({
    binaryPath: fakeBinaryPath,
    cwd: repoRoot,
    env: {
      ...process.env,
      FAKE_STS2_CASE: "echo-argv",
    },
  });
  const service = createServiceClient({
    serviceUrl: "http://127.0.0.1:4317",
    serviceToken: "local-dev-token",
  });

  try {
    globalThis.fetch = async (url, options = {}) => {
      assert.equal(url, "http://127.0.0.1:4317/v0/test-runs");
      assert.equal(options.method, "POST");
      requests.push(JSON.parse(options.body));
      return new Response(JSON.stringify({ runId: `run-${requests.length}`, status: "queued" }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      });
    };

    const cases = [
      {
        options: {
          path: "tests/scenarios/smoke-main-menu.sts2.yaml",
          profile: "mock",
          tags: ["fast", "shared"],
          tag: "shared",
          durable: true,
        },
        argv: [
          "--json",
          "test",
          "run",
          "--profile",
          "mock",
          "--tag",
          "fast",
          "--tag",
          "shared",
          "--tag",
          "shared",
          "tests/scenarios/smoke-main-menu.sts2.yaml",
        ],
        body: {
          path: "tests/scenarios/smoke-main-menu.sts2.yaml",
          profile: "mock",
          tags: ["fast", "shared", "shared"],
          durable: true,
        },
      },
      {
        options: { inline: "name: inline\nsteps: [game.info]\n" },
        argv: ["--json", "test", "run", "--inline", "name: inline\nsteps: [game.info]\n"],
        body: { inline: "name: inline\nsteps: [game.info]\n" },
      },
      {
        options: { scenario: { name: "object", steps: ["game.info"] } },
        argv: ["--json", "test", "run", "--inline", '{"name":"object","steps":["game.info"]}'],
        body: { inline: '{"name":"object","steps":["game.info"]}' },
      },
      {
        options: { profile: "mock", tags: [] },
        argv: ["--json", "test", "run", "--profile", "mock"],
        body: { profile: "mock", tags: [] },
      },
    ];

    for (const testCase of cases) {
      assert.deepEqual((await local.testRun(testCase.options)).argv, testCase.argv);
      await service.submitTestRun(testCase.options);
      assert.deepEqual(requests.at(-1), testCase.body);
    }

    for (const invalid of [
      {},
      { path: "fixture.yaml", inline: "name: duplicate" },
      { path: "" },
      { scenario: "not-an-object" },
    ]) {
      await assert.rejects(
        () => local.testRun(invalid),
        /testRun requires (exactly one input source|path|scenario)/,
      );
      await assert.rejects(
        () => service.submitTestRun(invalid),
        /testRun requires (exactly one input source|path|scenario)/,
      );
    }
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("testRun preserves durable service terminal errors without local fallback", async () => {
  const originalFetch = globalThis.fetch;
  const requests = [];

  try {
    globalThis.fetch = async (url, options = {}) => {
      requests.push({ url, options });
      if (url === "http://127.0.0.1:4317/v0/test-runs" && options.method === "POST") {
        assert.deepEqual(JSON.parse(options.body), {
          inline: "name: orphaned\nsteps: [game.info]\n",
          durable: true,
        });
        return new Response(JSON.stringify({ runId: "run-orphaned", status: "queued" }), {
          status: 200,
          headers: {
            "Content-Type": "application/json",
          },
        });
      }

      assert.equal(url, "http://127.0.0.1:4317/v0/test-runs/run-orphaned");
      return new Response(
        JSON.stringify({
          runId: "run-orphaned",
          status: "orphaned",
          error: {
            code: "remote_test_run_orphaned",
            message: "Remote test run was orphaned during recovery.",
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

    const client = createSts2Client({
      binaryPath: resolve(repoRoot, "target", "debug", "missing-sts2"),
      cwd: repoRoot,
      serviceUrl: "http://127.0.0.1:4317",
      serviceToken: "local-dev-token",
    });

    await assert.rejects(
      () =>
        client.testRun({
          inline: "name: orphaned\nsteps: [game.info]\n",
          durable: true,
        }),
      (error) => {
        assert.ok(error instanceof Sts2CliError);
        assert.equal(error.exitCode, 1);
        assert.equal(error.payload.status, "orphaned");
        assert.equal(error.payload.error.code, "remote_test_run_orphaned");
        assert.equal(error.payload.recovery.previousStatus, "running");
        assert.match(error.stdout, /remote_test_run_orphaned/);
        return true;
      },
    );
    assert.equal(requests.length, 2);
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("loadFixture requires a non-empty fixture path", async () => {
  const client = createSts2Client({ cwd: repoRoot });

  await assert.rejects(() => client.loadFixture({}), /loadFixture requires path\./);
});

test("scenario helpers validate required paths", async () => {
  const client = createSts2Client({ cwd: repoRoot });

  await assert.rejects(() => client.scenarioExport({}), /scenarioExport requires output\./);
  await assert.rejects(() => client.scenarioLoad({}), /scenarioLoad requires path\./);
});

test("debug helpers validate required arguments", async () => {
  const client = createSts2Client({ cwd: repoRoot });

  await assert.rejects(() => client.debugStep({}), /debugStep requires kind\./);
  await assert.rejects(() => client.debugSessionStatus({}), /debugSessionStatus requires sessionId\./);
  await assert.rejects(() => client.debugSessionEnd({}), /debugSessionEnd requires sessionId\./);
  await assert.rejects(() => client.debugEvents({}), /debugEvents requires sessionId\./);
  await assert.rejects(() => client.debugWait({}), /debugWait requires sessionId\./);
  await assert.rejects(
    () => client.breakpointAdd({ path: "screen.type" }),
    /breakpointAdd requires exactly one predicate option/,
  );
  await assert.rejects(() => client.breakpointRemove({}), /breakpointRemove requires id\./);
});

test("probe and project-hook helpers validate required arguments", async () => {
  const client = createSts2Client({ cwd: repoRoot });

  await assert.rejects(() => client.http({}), /http requires url\./);
  await assert.rejects(() => client.httpWait({}), /httpWait requires url\./);
  await assert.rejects(() => client.fetch({}), /fetch requires source\./);
  await assert.rejects(() => client.websocket({}), /websocket requires url\./);
  await assert.rejects(() => client.projectProfileShow({}), /projectProfileShow requires name\./);
  await assert.rejects(() => client.projectProfileRun({}), /projectProfileRun requires name\./);
  await assert.rejects(() => client.projectHookShow({}), /projectHookShow requires name\./);
  await assert.rejects(() => client.projectHookRun({}), /projectHookRun requires name\./);
  await assert.rejects(() => client.codeHookInfo({}), /codeHookInfo requires query\./);
});

test("lifecycle helpers translate camelCase options to the expected CLI argv", async () => {
  const client = createSts2Client({
    binaryPath: fakeBinaryPath,
    cwd: repoRoot,
    env: {
      ...process.env,
      FAKE_STS2_CASE: "echo-argv",
    },
  });

  const launchPayload = await client.gameLaunch({
    timeoutMs: 30_000,
    intervalMs: 250,
    launchArgs: ["--headless", "-fastmp"],
  });
  assert.deepEqual(launchPayload.argv, [
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

  const attachPayload = await client.gameAttach({
    timeoutMs: 15_000,
  });
  assert.deepEqual(attachPayload.argv, [
    "--json",
    "game",
    "attach",
    "--timeout-ms",
    "15000",
  ]);

  const closePayload = await client.gameClose({
    timeoutMs: 30_000,
    intervalMs: 250,
  });
  assert.deepEqual(closePayload.argv, [
    "--json",
    "game",
    "close",
    "--timeout-ms",
    "30000",
    "--interval-ms",
    "250",
  ]);

  const killPayload = await client.gameKill({
    timeoutMs: 10_000,
    intervalMs: 100,
  });
  assert.deepEqual(killPayload.argv, [
    "--json",
    "game",
    "kill",
    "--timeout-ms",
    "10000",
    "--interval-ms",
    "100",
  ]);

  const deployPayload = await client.gameDeploy({
    path: "./mods/MyMod",
    build: true,
    restart: true,
    verify: true,
    timeoutMs: 45_000,
    intervalMs: 500,
  });
  assert.deepEqual(deployPayload.argv, [
    "--json",
    "game",
    "deploy",
    "./mods/MyMod",
    "--build",
    "--restart",
    "--verify",
    "--timeout-ms",
    "45000",
    "--interval-ms",
    "500",
  ]);
});

test("gameDeploy requires a non-empty project path", async () => {
  const client = createSts2Client({ cwd: repoRoot });

  await assert.rejects(() => client.gameDeploy({}), /gameDeploy requires path\./);
});

test("assetsExtract requires a non-empty query", async () => {
  const client = createSts2Client({ cwd: repoRoot });

  await assert.rejects(() => client.assetsExtract({}), /assetsExtract requires query\./);
});

test("assetsExplain requires a non-empty query", async () => {
  const client = createSts2Client({ cwd: repoRoot });

  await assert.rejects(() => client.assetsExplain({}), /assetsExplain requires query\./);
});

test("assetsExtractBatch requires a non-empty manifest", async () => {
  const client = createSts2Client({ cwd: repoRoot });

  await assert.rejects(() => client.assetsExtractBatch({}), /assetsExtractBatch requires manifest\./);
});

test("skillInstall preserves structured CLI failures", async () => {
  const targetDir = mkdtempSync(join(tmpdir(), "sts2-skill-install-error-"));
  const invalidPath = join(targetDir, "not-a-dir");
  writeFileSync(invalidPath, "x");

  try {
    const client = createSts2Client({ cwd: repoRoot, configPath: mockConfigPath });

    await assert.rejects(
      () => client.skillInstall({ path: invalidPath }),
      (error) => {
        assert.ok(error instanceof Sts2CliError);
        assert.equal(error.exitCode, 2);
        assert.equal(error.payload?.error?.code, "skill_install_failed");
        assert.match(error.stdout, /skill_install_failed/);
        return true;
      },
    );
  } finally {
    rmSync(targetDir, { recursive: true, force: true });
  }
});

test("the wrapper preserves stdout, stderr, argv, and parsed payload for non-zero exits", async () => {
  const client = createSts2Client({
    binaryPath: fakeBinaryPath,
    cwd: repoRoot,
    env: {
      ...process.env,
      FAKE_STS2_CASE: "error-json",
    },
  });

  await assert.rejects(
    () => client.gameInfo(),
    (error) => {
      assert.ok(error instanceof Sts2CliError);
      assert.equal(error.exitCode, 3);
      assert.deepEqual(error.argv, ["--json", "game", "info"]);
      assert.equal(error.payload?.error?.code, "synthetic_failure");
      assert.match(error.stdout, /synthetic_failure/);
      assert.match(error.stderr, /synthetic stderr/);
      return true;
    },
  );
});
