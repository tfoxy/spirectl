#!/usr/bin/env node

const mode = process.env.FAKE_STS2_CASE;

if (mode === "echo-argv") {
  process.stdout.write(`${JSON.stringify({ argv: process.argv.slice(2) })}\n`);
  process.exit(0);
}

if (mode === "asset-diagnostics") {
  const argv = process.argv.slice(2);
  if (argv.includes("inspect") && argv.includes("ai-tools")) {
    process.stdout.write(
      `${JSON.stringify({
        source: "command-catalog",
        adapter: { name: "sts2-mcp", transport: "stdio" },
        tools: [
          {
            name: "assets_explain",
            summary: "Explain asset diagnostics.",
            status: "implemented",
            readOnly: true,
            mapsTo: ["assets explain"],
            inputSchema: { type: "object", properties: { query: { type: "string" }, execution: { type: "string" } }, required: ["query"], additionalProperties: false },
            outputShape: { summary: "Asset explanation." },
            limitations: [],
            recommendedUsage: [],
          },
          {
            name: "assets_extract_batch",
            summary: "Extract batch assets.",
            status: "implemented",
            readOnly: false,
            mapsTo: ["assets extract-batch"],
            inputSchema: { type: "object", properties: { manifest: { type: "string" }, execution: { type: "string" } }, required: ["manifest"], additionalProperties: false },
            outputShape: { summary: "Batch result." },
            limitations: [],
            recommendedUsage: [],
          },
        ],
      })}\n`,
    );
    process.exit(0);
  }

  if (argv.includes("extract-batch")) {
    process.stdout.write(
      `${JSON.stringify({
        command: "extract-batch",
        status: "partial",
        results: [
          {
            id: "kaiser-overlay",
            query: "encounter:kaiser_crab_boss:visual-state:rocket-charge-up:overlay:image",
            status: "ok",
            metadata: { source: "test" },
            exports: [
              {
                sourcePath: "encounter:kaiser_crab_boss:visual-state:rocket-charge-up:overlay:image",
                notes: ["Rendered encounter virtual target."],
                notices: [{ code: "selector-fallback", severity: "warning", path: "selector", message: "Used fallback selector." }],
                provenance: { sourceKind: "virtual-query" },
              },
            ],
          },
          {
            id: "uncataloged",
            query: "encounter:unknown:background:image",
            status: "failed",
            error: {
              code: "encounter_visual_package_unsupported",
              message: "Encounter visual package is not cataloged.",
              details: {
                error: {
                  details: [
                    { field: "encounterId", value: "unknown", note: "No catalog entry." },
                  ],
                },
              },
            },
          },
        ],
      })}\n`,
    );
    process.exit(0);
  }

  process.stdout.write(
    `${JSON.stringify({
      command: "explain",
      status: "ok",
      explanationKind: "encounter-scene-package",
      explanation: {
        encounterId: "kaiser_crab_boss",
        selectorDiagnostics: [
          {
            partId: "rocket",
            status: "resolved",
            candidates: [{ path: "/root/Combat/Enemies/KaiserCrab/Rocket", status: "resolved" }],
            visibleBounds: { x: 1122, y: 262, width: 390, height: 500 },
          },
        ],
        renderTargets: [
          {
            targetId: "rocket-charge-up-overlay",
            decision: { decision: "render-overlay", affectedPartIds: ["rocket"] },
          },
        ],
        notices: [{ code: "provisional-contract", severity: "info", path: "encounterVisuals", message: "Provisional." }],
      },
    })}\n`,
  );
  process.exit(0);
}

if (mode === "state") {
  const argv = process.argv.slice(2);
  if (argv.includes("inspect") && argv.includes("ai-tools")) {
    process.stdout.write(
      `${JSON.stringify({
        source: "command-catalog",
        adapter: { name: "sts2-mcp", transport: "stdio" },
        tools: [
          {
            name: "state",
            summary: "Inspect current runtime state.",
            status: "implemented",
            readOnly: true,
            mapsTo: ["state"],
            inputSchema: { type: "object", properties: {}, additionalProperties: false },
            outputShape: { summary: "State payload." },
            limitations: [],
            recommendedUsage: [],
          },
        ],
      })}\n`,
    );
    process.exit(0);
  }

  process.stdout.write(
    `${JSON.stringify({
      schemaVersion: "spirectl/v0",
      gameVersion: "unknown",
      bridgeVersion: "spirectl-bridge/0.0.0",
      transportKind: "mock",
      attachmentState: "stubbed",
      source: "stub",
      provisional: true,
      playerId: "p1",
      hostPlayerId: "p1",
      localPlayerId: "p1",
      localRole: "host",
      perspective: "local:p1",
      remoteOrchestrationCapability: "local-only-degraded",
      screen: { id: "bundle-selection", screenInstanceId: "screen:bundle-selection:1" },
      resolvedPerspective: { scope: "local", playerId: "p1", hostPlayerId: "p1", localPlayerId: "p1", localRole: "host", usesDefault: true },
      map: {
        nodes: [
          {
            id: "map-node:3:1",
            label: "Monster (3,1)",
            description: "Reachable map node.",
            playerId: "p1",
            ownerPlayerId: "p1",
            perspective: "local:p1",
            remoteOrchestrationCapability: "local-only-degraded",
            selected: false,
            provisional: false,
          },
        ],
      },
      eventRoom: {
        page: {
          eventId: "golden-idol",
          eventType: "MegaCrit.Sts2.Core.Events.GoldenIdolEvent",
          title: {
            text: "Golden Idol",
            rawText: "Golden Idol",
            locTable: "events",
            locKey: "golden-idol.title",
            source: "loc-string",
            provisional: false,
          },
          description: {
            text: "You see a [yellow]golden idol[/yellow].",
            rawText: "You see a {0}.",
            locTable: "events",
            locKey: "golden-idol.description",
            source: "loc-string",
            provisional: false,
          },
          sharedLabel: {
            text: "-- [wave]Offer[/wave] --",
            source: "mega-rich-text-label",
            provisional: true,
          },
          ancient: {
            title: {
              text: "NEOW",
              source: "loc-string",
              provisional: false,
            },
            bannerTitle: {
              text: "[ancient_banner]NEOW[/ancient_banner]",
              source: "mega-rich-text-label",
              provisional: true,
            },
            epithet: {
              text: "Madre de la resurrección (desterrada)",
              source: "loc-string",
              provisional: false,
            },
            currentDialogue: {
              text: "[sine]... Hola ... de nuevo ... [/sine]",
              source: "mega-rich-text-label",
              provisional: true,
            },
            currentDialogueIndex: 0,
            dialogueLines: [
              {
                index: 0,
                text: {
                  text: "[sine]... Hola ... de nuevo ... [/sine]",
                  source: "mega-rich-text-label",
                  provisional: true,
                },
                speaker: "Neow",
                current: true,
                visible: true,
              },
            ],
            currentSpeaker: "Neow",
            nextButtonText: null,
            textSource: "ancient-layout-label",
            provisional: false,
          },
          textSource: "event-model",
          provisional: false,
        },
        options: [
          {
            id: "event-room:gain-gold:0",
            label: "Take 75 Gold",
            description: "Gain [yellow]75[/yellow] Gold.",
            playerId: "p1",
            ownerPlayerId: "p1",
            perspective: "local:p1",
            selected: false,
            provisional: false,
            preferredAction: {
              kind: "select-event-option",
              playerId: "p1",
              ownerPlayerId: "p1",
              perspective: "local:p1",
              remoteOrchestrationCapability: "local-only-degraded",
              arguments: { eventOptionId: "event-room:gain-gold:0", playerId: "p1" },
            },
          },
        ],
      },
      treasureRoom: {
        relics: [
          {
            id: "treasure-room:relic:anchor:0",
            label: "Anchor",
            description: "Start each combat with 10 Block.",
            ownerPlayerId: "p1",
            selected: false,
            provisional: false,
            preferredAction: {
              kind: "take-relic",
              ownerPlayerId: "p1",
              arguments: { relicId: "treasure-room:relic:anchor:0", playerId: "p1" },
            },
          },
        ],
      },
      relicSelection: {
        relics: [
          {
            id: "relic-selection:anchor:0",
            label: "Anchor",
            ownerPlayerId: "p1",
            selected: false,
            provisional: false,
          },
        ],
      },
      restSite: {
        controls: [
          {
            id: "rest-site:rest",
            label: "Rest",
            enabled: true,
            ownerPlayerId: "p1",
            preferredAction: {
              kind: "rest",
              ownerPlayerId: "p1",
              arguments: { playerId: "p1" },
            },
          },
        ],
      },
      shop: {
        purchasableItems: [
          {
            id: "shop:p1:card:strike:0",
            label: "Buy Strike (50 gold)",
            ownerPlayerId: "p1",
            selected: false,
            provisional: false,
            cost: { amount: 50, currency: "gold" },
            preferredAction: {
              kind: "buy-card",
              ownerPlayerId: "p1",
              arguments: { shopItemId: "shop:p1:card:strike:0", playerId: "p1" },
            },
          },
        ],
      },
      rewards: {
        rewards: [
          {
            id: "reward:p1:0",
            label: "Jab",
            ownerPlayerId: "p1",
            selected: false,
            provisional: false,
            preferredAction: {
              kind: "claim-reward",
              ownerPlayerId: "p1",
              arguments: { rewardId: "reward:p1:0", playerId: "p1" },
            },
          },
        ],
      },
      cardSelection: {
        cards: [
          {
            id: "card-selection:card:bash:0",
            label: "Bash",
            ownerPlayerId: "p1",
            selected: false,
            provisional: false,
          },
        ],
      },
      simpleCardSelection: {
        choices: [
          {
            id: "simple-card-selection:card:strike:0",
            label: "Jab",
            ownerPlayerId: "p1",
            selected: false,
            provisional: false,
          },
        ],
      },
      deckCardSelection: {
        deckCards: [
          {
            id: "deck-card-selection:card:defend:0",
            label: "Defend",
            ownerPlayerId: "p1",
            selected: false,
            provisional: false,
          },
        ],
      },
      bundleSelection: {
        bundles: [
          {
            id: "bundle:offensive-pack",
            label: "Offensive Pack",
            description: "Three attack cards.",
            playerId: "p1",
            ownerPlayerId: "p1",
            perspective: "local:p1",
            remoteOrchestrationCapability: "local-only-degraded",
            selected: false,
            provisional: false,
            preferredAction: {
              kind: "select-bundle",
              playerId: "p1",
              ownerPlayerId: "p1",
              perspective: "local:p1",
              remoteOrchestrationCapability: "local-only-degraded",
              arguments: { bundleId: "bundle:offensive-pack", playerId: "p1" },
            },
          },
        ],
      },
      multiplayerLobby: {
        players: [
          {
            id: "p1",
            label: "Player 1",
            playerId: "p1",
            ownerPlayerId: "p1",
            isLocal: true,
            isHost: true,
            isRemote: false,
            selected: true,
            provisional: false,
            perspective: "local:p1",
          },
          {
            id: "p3",
            label: "Host-local Seat",
            playerId: "p3",
            ownerPlayerId: "p3",
            ownerRole: "host-local-seat",
            isLocal: false,
            isHost: false,
            isRemote: false,
            selected: false,
            provisional: false,
            perspective: "local:p3",
            remoteOrchestrationCapability: "host-local-seat",
          },
        ],
        actions: [
          {
            id: "action:lobby:ready:p1",
            label: "Ready",
            enabled: true,
            playerId: "p1",
            ownerPlayerId: "p1",
            intentKind: "ready",
            perspective: "local:p1",
            remoteOrchestrationCapability: "local-only-degraded",
            preferredAction: "ready",
          },
          {
            id: "action:lobby:ready:p2",
            label: "Ready remote player",
            enabled: false,
            playerId: "p2",
            ownerPlayerId: "p2",
            intentKind: "ready",
            perspective: "remote:p2",
            remoteOrchestrationCapability: "unsupported",
            disabledReason: "Remote client orchestration is not configured.",
            preferredAction: "ready",
          },
          {
            id: "action:lobby:ready:p3",
            label: "Ready host-local seat",
            enabled: true,
            playerId: "p3",
            ownerPlayerId: "p3",
            ownerRole: "host-local-seat",
            intentKind: "ready",
            perspective: "local:p3",
            remoteOrchestrationCapability: "host-local-seat",
            preferredAction: "ready",
          },
        ],
      },
      cardOverlay: {
        cards: [
          {
            id: "card-overlay:p1:rage:0",
            name: "Rage",
            cost: 0,
            ownerPlayerId: "p1",
            playable: false,
            unplayableReason: "Overlay preview only.",
            upgraded: true,
            modelId: "Red_Rage",
            description: "Whenever you play an Attack this turn, gain 5 Block.",
            type: "skill",
            rarity: "uncommon",
            targetType: "none",
            upgradeLevel: 1,
            costLabel: "0",
            assetRefs: [
              {
                kind: "card-art",
                key: "card:red:rage",
                label: "Rage card art",
                provisional: false,
              },
            ],
          },
        ],
        previewText: "Whenever you play an Attack this turn, gain 5 Block.",
        breadcrumbs: [
          {
            screenType: "bundle-selection",
            title: "Choose a bundle",
            screenInstanceId: "screen:bundle-selection:1",
            source: "card-overlay",
            rawType: "CardOverlayScreen",
            className: "Sts2.UI.CardOverlayScreen",
            ownerPlayerId: "p1",
            perspective: "local:p1",
          },
        ],
        sourceBreadcrumbs: [
          {
            screenType: "bundle-selection",
            title: "Choose a bundle",
            screenInstanceId: "screen:bundle-selection:1",
            source: "underlying-screen",
            ownerPlayerId: "p1",
            perspective: "local:p1",
          },
        ],
        blocking: true,
        passive: false,
        overlayPolicy: "blocking-overlay",
        ownerPlayerId: "p1",
        perspective: "local:p1",
        close: {
          id: "card-overlay:close",
          label: "Close",
          enabled: true,
          ownerPlayerId: "p1",
          choiceKind: "overlay-close",
          intentKind: "close-overlay",
          preferredAction: "choose",
          perspective: "local:p1",
          provisional: false,
        },
        back: {
          id: "card-overlay:back",
          label: "Back",
          enabled: false,
          ownerPlayerId: "p1",
          choiceKind: "overlay-back",
          intentKind: "back",
          preferredAction: null,
          perspective: "local:p1",
          provisional: true,
        },
        followThroughControls: [
          {
            id: "card-overlay:inspect-source",
            label: "View source bundle",
            enabled: true,
            ownerPlayerId: "p1",
            choiceKind: "overlay-follow-through",
            intentKind: "inspect-source",
            preferredAction: "choose",
            perspective: "local:p1",
            provisional: false,
          },
        ],
        notices: [
          {
            code: "card-overlay-partial",
            message: "Card overlay state is partially modeled in the fake fixture.",
            provisional: true,
            path: "cardOverlay",
            severity: "partial",
            source: "test-fixture",
            stability: "experimental",
            perspective: "local:p1",
          },
          {
            code: "card-overlay-passive-underlying-retained",
            message: "Passive overlay metadata is preserved without changing the underlying screen payload.",
            provisional: false,
            path: "cardOverlay.passive",
            severity: "info",
            source: "test-fixture",
            stability: "stable",
            perspective: "local:p1",
          },
        ],
        metadata: {
          noticeCode: "card-overlay-partial",
          severity: "partial",
          source: "test-fixture",
          stability: "experimental",
        },
        provisional: true,
      },
      choices: [
        {
          id: "card-selection:bundle:offensive-pack:0",
          kind: "card-selection-bundle",
          label: "Offensive Pack",
          playerId: "p1",
          ownerPlayerId: "p1",
          choiceKind: "bundle",
          intentKind: "choose-bundle",
          perspective: "local:p1",
          preferredAction: "select-bundle",
          preferredActionRef: {
            kind: "select-bundle",
            ownerPlayerId: "p1",
            arguments: {
              bundleId: "card-selection:bundle:offensive-pack:0",
              playerId: "p1",
            },
          },
          provisional: false,
        },
        {
          id: "card-overlay:close",
          kind: "overlay-control",
          label: "Close overlay",
          ownerPlayerId: "p1",
          choiceKind: "overlay-close",
          intentKind: "close-overlay",
          perspective: "local:p1",
          preferredAction: "choose",
          provisional: false,
          metadata: {
            overlayPolicy: "blocking-overlay",
          },
        },
      ],
      availableActions: [
        {
          id: "action:bundle-selection:choose:card-selection:bundle:offensive-pack:0",
          kind: "select-bundle",
          summary: "Choose Offensive Pack.",
          cliCommandHint: "sts2 act select-bundle --bundle card-selection:bundle:offensive-pack:0",
          provisional: false,
          arguments: {
            bundleId: "card-selection:bundle:offensive-pack:0",
            playerId: "p1",
          },
          intentKind: "choose-bundle",
          playerId: "p1",
          ownerPlayerId: "p1",
          perspective: "local:p1",
          remoteOrchestrationCapability: "local-only-degraded",
          preferredAction: "select-bundle",
        },
        {
          id: "action:card-overlay:close",
          kind: "choose",
          summary: "Close card overlay.",
          cliCommandHint: "sts2 act choose --choice card-overlay:close",
          provisional: false,
          arguments: {
            choiceId: "card-overlay:close",
            playerId: "p1",
          },
          intentKind: "close-overlay",
          ownerPlayerId: "p1",
          perspective: "local:p1",
          preferredAction: "choose",
          metadata: {
            overlayPolicy: "blocking-overlay",
          },
        },
      ],
      notices: [
        {
          code: "bundle-selection-observed",
          message: "Synthetic current state fixture.",
          provisional: false,
          path: "bundleSelection.bundles",
          severity: "info",
          source: "test-fixture",
          stability: "stable",
          perspective: "local:p1",
        },
        {
          code: "card-overlay-partial",
          message: "Synthetic card overlay state is partial.",
          provisional: true,
          path: "cardOverlay",
          severity: "partial",
          source: "test-fixture",
          stability: "experimental",
          perspective: "local:p1",
        },
        {
          code: "card-overlay-passive",
          message: "Passive overlay metadata is present for passthrough coverage.",
          provisional: false,
          path: "cardOverlay.passive",
          severity: "info",
          source: "test-fixture",
          stability: "stable",
          perspective: "local:p1",
        },
      ],
    })}\n`,
  );
  process.exit(0);
}

if (mode === "error-json") {
  process.stdout.write(
    `${JSON.stringify({ error: { code: "synthetic_failure", message: "Synthetic failure from fake sts2." } })}\n`,
  );
  process.stderr.write("synthetic stderr from fake sts2\n");
  process.exit(3);
}

if (mode === "load-fixture-result") {
  process.stdout.write(
    `${JSON.stringify({
      requestedPath: "fixtures/multiplayer-ownership-local-only-degraded.sts2.fixture.yaml",
      resolvedPath: "/repo/fixtures/multiplayer-ownership-local-only-degraded.sts2.fixture.yaml",
      fixture: {
        schemaVersion: "spirectl.fixture/v0",
        name: "multiplayer-ownership-local-only-degraded",
        screen: "multiplayer-lobby",
      },
      source: "stub",
      provisional: true,
      recipeReport: {
        recipeName: "multiplayer-lobby-recipe",
        appliedFields: [
          {
            fieldPath: "screen",
            valueSummary: "multiplayer-lobby",
            reasonCode: "applied_authored_field",
            message: "Applied authored fixture screen family.",
          },
        ],
        inferredFields: [],
        omittedFields: [],
        unsupportedFields: [],
        degradedMultiplayerFields: [
          {
            fieldPath: "multiplayerLobby.players[p:200]",
            valueSummary: "remote client omitted",
            reasonCode: "local-only-degraded-multiplayer",
            message: "Local fixture loading cannot create a remote client.",
          },
        ],
        bridgeValidation: {
          status: "passed",
          details: [],
        },
      },
      loaded: {
        screen: { id: "multiplayer-lobby", screenInstanceId: "screen:multiplayer-lobby:mock-fixture" },
        resolvedPerspective: { scope: "local", playerId: "p:100", usesDefault: false },
        notices: [],
      },
    })}\n`,
  );
  process.exit(0);
}

if (mode === "action-error-json") {
  process.stdout.write(
    `${JSON.stringify({
      error: {
        code: "action_rejected",
        message: "Action is not legal on the current screen.",
        actionFailure: {
          reasonCode: "wrong-player",
          screen: "rewards",
          action: "claim-reward",
          playerId: "p2",
          requestedPlayerId: "p2",
          resolvedOwnerPlayerId: "p1",
          localPlayerId: "p1",
          localRole: "host",
          perspective: "local:p1",
          checkedHookPaths: ["RewardsScreen.Confirm"],
          details: {
            choiceId: "reward:p1:0",
            rewardId: "reward:p1:0",
            remoteOrchestrationCapability: "unsupported",
          },
        },
        details: [
          {
            field: "choiceId",
            value: "reward:p1:0",
            actionReasonCode: "wrong-player",
            reasonCode: "wrong-player",
            checkedHookPaths: ["RewardsScreen.Confirm"],
            screen: "rewards",
            action: "claim-reward",
            playerId: "p2",
            requestedPlayerId: "p2",
            resolvedOwnerPlayerId: "p1",
            localPlayerId: "p1",
            localRole: "host",
            perspective: "local:p1",
          },
        ],
      },
    })}\n`,
  );
  process.stderr.write("synthetic action stderr from fake sts2\n");
  process.exit(3);
}

if (mode === "invalid-json") {
  process.stdout.write("{ this is not valid json }\n");
  process.stderr.write("synthetic invalid json from fake sts2\n");
  process.exit(0);
}

process.stderr.write(`unknown FAKE_STS2_CASE: ${mode ?? "<unset>"}\n`);
process.exit(64);
