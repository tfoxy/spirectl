using Spirectl.Sts2.Core.Restore;
using Spirectl.Sts2.Core.Scenarios;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live;


internal static class Sts2RestoreFidelity
{
    public static IReadOnlyList<RestoreFieldReportSnapshot> FieldReports(GameStateSnapshot snapshot)
    {
        if (Sts2SupportedScreenIds.IsLobbyScreenType(snapshot.ScreenType))
        {
            var lobbyReports = MultiplayerLobbyReports(snapshot);
            var lobbyMultiplayer = Sts2MultiplayerRestore.BuildMetadata(snapshot);
            return lobbyMultiplayer is null ? lobbyReports : [.. lobbyReports, .. MultiplayerReports(lobbyMultiplayer)];
        }

        IReadOnlyList<RestoreFieldReportSnapshot> reports;
        if (string.Equals(snapshot.ScreenType, "combat", StringComparison.Ordinal))
        {
            reports = CombatReports(snapshot);
        }
        else if (Sts2SupportedScreenIds.IsShopScreenType(snapshot.ScreenType))
        {
            reports = ChoiceBackedReports(snapshot, "shop.purchasableItems[]", "shop purchasable items and controls");
        }
        else if (Sts2SupportedScreenIds.IsEventRoomScreenType(snapshot.ScreenType))
        {
            reports = ChoiceBackedReports(snapshot, "eventRoom.options[]", "event-room options", RestoreFieldFidelity.Unsupported);
        }
        else if (Sts2SupportedScreenIds.IsTreasureRoomScreenType(snapshot.ScreenType))
        {
            reports = ChoiceBackedReports(snapshot, "treasureRoom.relics[]", "treasure-room relics", RestoreFieldFidelity.Unsupported);
        }
        else if (Sts2SupportedScreenIds.IsRelicSelectionScreenType(snapshot.ScreenType))
        {
            reports = ChoiceBackedReports(snapshot, "relicSelection.relics[]", "relic-selection relics", RestoreFieldFidelity.Unsupported);
        }
        else if (Sts2SupportedScreenIds.IsRestSiteScreenType(snapshot.ScreenType))
        {
            reports = ChoiceBackedReports(snapshot, "restSite.controls[]", "rest-site controls");
        }
        else if (Sts2SupportedScreenIds.IsRewardsScreenType(snapshot.ScreenType))
        {
            reports = ChoiceBackedReports(snapshot, "rewards.rewards[]", "reward rows");
        }
        else if (Sts2SupportedScreenIds.IsMapScreenType(snapshot.ScreenType))
        {
            reports = ChoiceBackedReports(snapshot, "map.nodes[]", "map nodes");
        }
        else if (Sts2SupportedScreenIds.IsSimpleCardSelectionScreenType(snapshot.ScreenType))
        {
            reports = SelectionReports(snapshot, "simpleCardSelection.choices[]", "simple card selection choices");
        }
        else if (Sts2SupportedScreenIds.IsDeckCardSelectionScreenType(snapshot.ScreenType))
        {
            reports = SelectionReports(snapshot, "deckCardSelection.deckCards[]", "deck card selection choices");
        }
        else if (Sts2SupportedScreenIds.IsBundleSelectionScreenType(snapshot.ScreenType))
        {
            reports = ChoiceBackedReports(snapshot, "bundleSelection.bundles[]", "bundle-selection bundles", RestoreFieldFidelity.Unsupported);
        }
        else if (Sts2SupportedScreenIds.IsCardSelectionFamily(snapshot.ScreenType))
        {
            reports = ChoiceBackedReports(snapshot, "cardSelection.cards[]", "card-selection cards", RestoreFieldFidelity.Unsupported);
        }
        else
        {
            reports = [.. CommonReports(snapshot), Report("screen.id", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Unsupported, validationKey: true, "screen-unsupported", $"Screen '{snapshot.ScreenType}' is outside the S83-S87 restore support matrix.")];
        }
        var multiplayer = Sts2MultiplayerRestore.BuildMetadata(snapshot);
        return multiplayer is null ? reports : [.. reports, .. MultiplayerReports(multiplayer)];
    }

    public static ScenarioRestoreQuality CoarseScenarioQuality(IReadOnlyList<RestoreFieldReportSnapshot> reports)
        => CoarseQuality(reports) switch
        {
            "exact" => ScenarioRestoreQuality.Exact,
            "unsupported" => ScenarioRestoreQuality.Unsupported,
            "degraded" => ScenarioRestoreQuality.Degraded,
            _ => ScenarioRestoreQuality.Partial,
        };

    private static string CoarseQuality(IReadOnlyList<RestoreFieldReportSnapshot> reports)
    {
        if (reports.Count == 0)
        {
            return "unsupported";
        }

        var validationReports = reports.Where(report => report.ValidationKey).ToArray();
        if (validationReports.Length > 0 && validationReports.All(report => report.Restore is RestoreFieldFidelity.Exact or RestoreFieldFidelity.Inferred))
        {
            return "exact";
        }

        return reports.Any(report => report.Restore == RestoreFieldFidelity.Exact || report.Restore == RestoreFieldFidelity.Partial || report.Restore == RestoreFieldFidelity.Inferred || report.Restore == RestoreFieldFidelity.DegradedLocalMultiplayer)
            ? "partial"
            : "unsupported";
    }

    private static IReadOnlyList<RestoreFieldReportSnapshot> CombatReports(GameStateSnapshot snapshot)
    {
        List<RestoreFieldReportSnapshot> reports = [.. CommonReports(snapshot)];
        reports.AddRange([
            Report("combat.turn", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Exact, validationKey: true, "combat-turn", "Combat turn is captured from public combat state and verified."),
            Report("combat.activePlayerId", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Exact, validationKey: true, "combat-active-player", "Active player id is captured from public combat state and verified."),
            Report("combat.isPlayerTurn", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Partial, validationKey: true, "combat-player-turn", "Player-turn state is captured, but sparse fixtures can only approximate current turn flow."),
            Report("combat.players[].id", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Exact, validationKey: true, "combat-player-ids", "Combat player ids are stable public ownership keys."),
            Report("combat.players[].hp", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Exact, validationKey: true, "combat-player-hp", "Combat player HP is captured and verified for visible players."),
            Report("combat.players[].maxHp", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Exact, validationKey: true, "combat-player-max-hp", "Combat player max HP is captured and verified for visible players."),
            Report("combat.players[].block", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Exact, validationKey: true, "combat-player-block", "Combat player block is captured from public state and verified."),
            Report("combat.players[].energy", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Exact, validationKey: true, "combat-player-energy", "Combat player energy is captured from public state and verified."),
            Report("combat.players[].hand[]", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Partial, validationKey: true, "combat-hand-observable", "Visible hand cards are captured by stable card ids; sparse restore recreates the visible hand without hidden draw sequencing."),
            Report("combat.hand[].ownerPlayerId", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Partial, validationKey: true, "combat-hand-ownership", "Hand card ownership is captured and used for local-player fixture replay."),
            Report("combat.potions[]", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Partial, validationKey: true, "combat-potions-observable", "Visible potion slots are captured; sparse restore can recreate local observable potion state."),
            Report("combat.enemies[]", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Partial, validationKey: true, "combat-enemies-observable", "Visible enemies are captured from public state; sparse restore does not promise byte-perfect encounter internals."),
            Report("combat.enemies[].intents[]", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Partial, validationKey: false, "combat-enemy-intent-observable", "Enemy intent labels and targets are observable but derived from live presentation state."),
            Report("combat.drawPile", RestoreFieldFidelity.Partial, RestoreFieldFidelity.Unsupported, validationKey: false, "combat-pile-observable-subset", "Pile count and exposed card ids are public when available, but exact pile order continuation is unsupported."),
            Report("combat.discardPile", RestoreFieldFidelity.Partial, RestoreFieldFidelity.Unsupported, validationKey: false, "combat-pile-observable-subset", "Discard pile count and exposed card ids are public when available, but exact continuation is unsupported."),
            Report("combat.exhaustPile", RestoreFieldFidelity.Partial, RestoreFieldFidelity.Unsupported, validationKey: false, "combat-pile-observable-subset", "Exhaust pile count and exposed card ids are public when available, but exact continuation is unsupported."),
            Report("combat.encounterVisuals", RestoreFieldFidelity.Partial, RestoreFieldFidelity.Unsupported, validationKey: false, "combat-encounter-visuals-observable", "Cataloged encounter visual parts and recent events are observational diagnostics, not fixture restore inputs."),
            Report("combat.hiddenQueues", RestoreFieldFidelity.Omitted, RestoreFieldFidelity.Unsupported, validationKey: false, "hidden-runtime-queues-omitted", "Hidden runtime queues are not part of public state and are intentionally omitted."),
            Report("combat.enemyAiHistory", RestoreFieldFidelity.Omitted, RestoreFieldFidelity.Unsupported, validationKey: false, "enemy-ai-history-unsupported", "Enemy AI history is private runtime state and is unsupported for sparse restore."),
            Report("combat.transientAnimationState", RestoreFieldFidelity.Omitted, RestoreFieldFidelity.Unsupported, validationKey: false, "transient-animation-state-omitted", "Animation frame and tween state are transient UI internals and are omitted."),
            Report("combat.rngContinuation", RestoreFieldFidelity.Omitted, RestoreFieldFidelity.Unsupported, validationKey: false, "rng-continuation-omitted", "RNG continuation is not exposed through a supported public save/runtime path."),
        ]);
        return reports;
    }

    private static IReadOnlyList<RestoreFieldReportSnapshot> SelectionReports(GameStateSnapshot snapshot, string sectionPath, string description)
    {
        List<RestoreFieldReportSnapshot> reports = [.. ChoiceBackedReports(snapshot, sectionPath, description, RestoreFieldFidelity.Unsupported)];
        reports.Add(Report("availableActions[].kind", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Unsupported, validationKey: true, "selection-actions-observable", $"{description} action kinds are captured from public state; exact staged-selection restore is unsupported."));
        return reports;
    }

    private static IReadOnlyList<RestoreFieldReportSnapshot> ChoiceBackedReports(
        GameStateSnapshot snapshot,
        string sectionPath,
        string description,
        RestoreFieldFidelity restore = RestoreFieldFidelity.Inferred)
    {
        List<RestoreFieldReportSnapshot> reports = [.. CommonReports(snapshot)];
        reports.AddRange(TypedChoiceReports(sectionPath, description, restore));
        return reports;
    }

    private static IReadOnlyList<RestoreFieldReportSnapshot> MultiplayerLobbyReports(GameStateSnapshot snapshot)
    {
        List<RestoreFieldReportSnapshot> reports = [.. ChoiceBackedReports(snapshot, "multiplayerLobby.players[]", "multiplayer lobby players and local controls")];
        reports.AddRange([
            Report("multiplayerLobby.actions[]", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Inferred, validationKey: true, "CharacterSelect-actions", "Lobby actions are captured from public action metadata and replayed through lobby fixture recipes."),
            Report("multiplayerLobby.hostPlayerId", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Exact, validationKey: true, "CharacterSelect-host-player", "Lobby host ownership is captured from public state and verified."),
            Report("multiplayerLobby.localRole", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Exact, validationKey: true, "CharacterSelect-local-role", "Local lobby role is captured from public state and verified."),
            Report("multiplayerLobby.remoteOrchestration", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Partial, validationKey: false, "remote-orchestration-capability", "Remote orchestration capability is diagnostic metadata; sparse replay remains local-bridge bounded."),
        ]);
        return reports;
    }

    private static IReadOnlyList<RestoreFieldReportSnapshot> TypedChoiceReports(string sectionPath, string description, RestoreFieldFidelity restore)
        => [
            Report(sectionPath, RestoreFieldFidelity.Exact, restore, validationKey: true, ReasonCode(sectionPath, "typed-section"), restore == RestoreFieldFidelity.Unsupported
                ? $"{description} are captured as public typed state, but exact sparse restore for this screen is unsupported."
                : $"{description} are captured as public typed state and replayed through fixture recipes."),
            Report($"{sectionPath}.id", RestoreFieldFidelity.Exact, restore, validationKey: true, ReasonCode(sectionPath, "typed-id"), "Stable public ids are captured for typed screen items and controls."),
            Report($"{sectionPath}.ownerPlayerId", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Partial, validationKey: true, ReasonCode(sectionPath, "typed-owner"), "Owner player metadata is captured for player-scoped visible items; sparse restore uses it for local ownership only."),
            Report($"{sectionPath}.intentKind", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Inferred, validationKey: false, ReasonCode(sectionPath, "typed-intent"), "Intent metadata is public action routing data and is inferred during sparse replay."),
            Report($"{sectionPath}.preferredAction", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Inferred, validationKey: false, ReasonCode(sectionPath, "typed-preferred-action"), "Preferred action metadata is captured from public state and re-derived from live legality after restore."),
            Report("choices[].id", RestoreFieldFidelity.Exact, restore, validationKey: true, "choices-v0-id", "Compatibility choice ids are captured for fallback and migration workflows."),
            Report("choices[].choiceKind", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Inferred, validationKey: false, "choices-v0-kind", "Choice kind is public compatibility metadata and is re-derived from the typed screen section."),
            Report("choices[].ownerPlayerId", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Partial, validationKey: true, "choices-v0-owner", "Choice ownership is captured; remote-owned controls are not silently replayed as local controls."),
            Report("choices[].preferredActionRef", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Inferred, validationKey: false, "choices-v0-preferred-action", "Preferred action references are captured and re-derived from live action legality."),
            Report("availableActions[].kind", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Inferred, validationKey: true, "available-actions-v0-kind", "Available action kinds are captured from public state and re-derived from live legality after restore."),
            Report("availableActions[].arguments", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Inferred, validationKey: true, "available-actions-v0-arguments", "Action arguments are captured as stable public ids and re-derived when controls are legal."),
            Report("availableActions[].ownerPlayerId", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Partial, validationKey: true, "available-actions-v0-owner", "Action ownership metadata is captured; one local bridge cannot claim unconfigured remote ownership."),
        ];

    private static IReadOnlyList<RestoreFieldReportSnapshot> CommonReports(GameStateSnapshot snapshot)
    {
        List<RestoreFieldReportSnapshot> reports = [
            Report("schemaVersion", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Exact, validationKey: true, "runtime-schema-version", "Runtime schema version is captured from the public state envelope."),
            Report("gameVersion", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Exact, validationKey: true, "runtime-game-version", "Game version is captured as source metadata for compatibility diagnostics."),
            Report("bridgeVersion", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Exact, validationKey: true, "runtime-bridge-version", "Bridge version is captured as source metadata for compatibility diagnostics."),
            Report("screen.id", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Exact, validationKey: true, "screen-id", "Screen id is captured and verified."),
            Report("screen.title", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Inferred, validationKey: false, "screen-title-presentation", "Screen title is public presentation metadata and may be re-derived by the live screen."),
            Report("screen.instanceId", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Inferred, validationKey: false, "screen-instance-id", "Screen instance id is stable for the captured screen instance but re-created on replay."),
            Report("screen.source", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Inferred, validationKey: false, "screen-source-metadata", "Screen source metadata is captured for diagnostics and re-derived from the runtime locator."),
            Report("screen.rawType", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Inferred, validationKey: false, "screen-raw-type-metadata", "Raw screen type is diagnostic metadata and is not a sparse restore input."),
            Report("screen.className", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Inferred, validationKey: false, "screen-class-name-metadata", "Screen class name is diagnostic metadata and is not a sparse restore input."),
            Report("resolvedPerspective.playerId", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Exact, validationKey: true, "resolved-perspective-player", "Resolved perspective player id is captured and verified."),
            Report("resolvedPerspective.scope", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Exact, validationKey: true, "resolved-perspective-scope", "Resolved perspective scope is captured and verified."),
            Report("hostPlayerId", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Partial, validationKey: true, "host-player-id", "Host player id is captured when the runtime exposes ownership metadata."),
            Report("localRole", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Partial, validationKey: true, "local-role", "Local multiplayer role is captured when the runtime exposes ownership metadata."),
            Report("remoteOrchestration", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Partial, validationKey: false, "remote-orchestration-capability", "Remote orchestration capability is captured as diagnostic metadata, not a remote-client replay guarantee."),
        ];
        if (snapshot.Run is null)
        {
            reports.AddRange([
                Report("run.act", RestoreFieldFidelity.Omitted, RestoreFieldFidelity.Unsupported, validationKey: true, "run-state-unavailable", "Run act is unavailable for this screen capture."),
                Report("run.floor", RestoreFieldFidelity.Omitted, RestoreFieldFidelity.Unsupported, validationKey: true, "run-state-unavailable", "Run floor is unavailable for this screen capture."),
                Report("run.seed", RestoreFieldFidelity.Omitted, RestoreFieldFidelity.Unsupported, validationKey: true, "run-state-unavailable", "Run seed is unavailable for this screen capture."),
            ]);
        }
        else
        {
            reports.AddRange([
                Report("run.act", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Exact, validationKey: true, "run-act", "Run act is captured from public run state and verified."),
                Report("run.floor", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Exact, validationKey: true, "run-floor", "Run floor is captured from public run state and verified."),
                Report("run.seed", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Partial, validationKey: true, "run-seed", "Seed is captured when exposed; restore verifies the observed seed without RNG continuation claims."),
                Report("run.players[].id", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Exact, validationKey: true, "run-player-ids", "Run player ids are captured as stable public ownership keys."),
                Report("run.players[].character", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Exact, validationKey: true, "run-player-character", "Run player character ids are captured and verified."),
                Report("run.players[].hp", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Exact, validationKey: true, "run-player-hp", "Run player HP is captured and verified."),
                Report("run.players[].gold", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Partial, validationKey: false, "run-player-presentation", "Run player presentation fields are captured when observable; sparse restore is bounded to supported fixture inputs."),
                Report("run.players[].relics[]", RestoreFieldFidelity.Partial, RestoreFieldFidelity.Partial, validationKey: false, "run-player-relics-observable", "Relic presentation is public when observable, but counters/history are only partially replayed."),
                Report("run.players[].potions[]", RestoreFieldFidelity.Partial, RestoreFieldFidelity.Partial, validationKey: false, "run-player-potions-observable", "Potion presentation is public when observable and partially replayed for local players."),
                Report("run.players[].masterDeck", RestoreFieldFidelity.Partial, RestoreFieldFidelity.Unsupported, validationKey: false, "run-master-deck-observable", "Master deck presentation can be captured when exposed, but sparse restore does not promise exact deck provenance."),
                Report("run.players[].statusEffects[]", RestoreFieldFidelity.Partial, RestoreFieldFidelity.Unsupported, validationKey: false, "run-status-effects-observable", "Run status effect presentation is observable metadata and not a sparse restore guarantee."),
            ]);
        }
        reports.AddRange([
            Report("cardOverlay.cards[]", RestoreFieldFidelity.Partial, RestoreFieldFidelity.Unsupported, validationKey: false, "card-overlay-partial", "Card overlay details are public when visible, but overlay restore is diagnostic-only."),
            Report("cardOverlay.breadcrumbs[]", RestoreFieldFidelity.Partial, RestoreFieldFidelity.Unsupported, validationKey: false, "card-overlay-breadcrumbs", "Overlay breadcrumbs preserve source-screen diagnostics without replaying private overlay internals."),
            Report("cardOverlay.close", RestoreFieldFidelity.Partial, RestoreFieldFidelity.Unsupported, validationKey: false, "card-overlay-controls", "Overlay close/back controls are captured only when legal hooks are visible; sparse restore does not force overlay UI state."),
            Report("exactBundle.nativeSave", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Exact, validationKey: false, "exact-sidecar-native-save-boundary", "Exact sidecar/native save continuation is separate from sparse recipe fixture restore."),
            Report("sparseRecipe.screenState", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Partial, validationKey: false, "sparse-recipe-boundary", "Sparse scenario YAML records reviewable public screen state and fixture inputs, not native hidden state."),
            Report("privateUiInternals", RestoreFieldFidelity.Omitted, RestoreFieldFidelity.Unsupported, validationKey: false, "private-ui-internals-omitted", "Private UI internals and scene-tree details are intentionally outside default state and restore support."),
        ]);
        return reports;
    }

    private static IReadOnlyList<RestoreFieldReportSnapshot> MultiplayerReports(MultiplayerRestoreSnapshot multiplayer)
    {
        var activeRestore = multiplayer.RequiresRemoteClients
            ? RestoreFieldFidelity.DegradedLocalMultiplayer
            : RestoreFieldFidelity.Partial;
        return [
            Report("multiplayer.localPlayerId", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Exact, validationKey: true, "multiplayer-local-player", "Local multiplayer player identity is captured by stable player id and verified after restore."),
            Report("multiplayer.hostPlayerId", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Exact, validationKey: true, "multiplayer-host-player", "Host player identity is captured by stable player id and verified after restore."),
            Report("multiplayer.players[].id", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Exact, validationKey: true, "multiplayer-player-ids", "Multiplayer player ids are captured by stable player id."),
            Report("multiplayer.players[].slotId", RestoreFieldFidelity.Exact, RestoreFieldFidelity.Exact, validationKey: true, "multiplayer-slot-ids", "Lobby slot ids are captured when observable."),
            Report("multiplayer.players[].selectedCharacterId", RestoreFieldFidelity.Exact, activeRestore, validationKey: true, "multiplayer-selected-character", "Selected characters are captured from observable multiplayer state."),
            Report("multiplayer.players[].isReady", RestoreFieldFidelity.Exact, activeRestore, validationKey: true, "multiplayer-ready-state", "Ready state is captured from lobby state when observable."),
            Report("multiplayer.players[isRemote=true]", RestoreFieldFidelity.Exact, activeRestore, validationKey: true, "remote-player-degraded-local", multiplayer.RequiresRemoteClients
                ? "Remote-owned player metadata is captured, but allow-degraded local replay omits remote clients explicitly."
                : "Remote-owned lobby player metadata is captured for lobby placeholder replay."),
            Report("multiplayer.remoteRuntime", RestoreFieldFidelity.Omitted, RestoreFieldFidelity.Unsupported, validationKey: false, "remote-clients-not-captured", "Remote client runtime processes are not captured by the local bridge and are never reported as successful replay."),
        ];
    }

    private static string ReasonCode(string path, string suffix)
        => $"{path.Replace("[]", string.Empty).Replace('.', '-').Replace(":", string.Empty)}-{suffix}";

    private static RestoreFieldReportSnapshot Report(
        string path,
        RestoreFieldFidelity capture,
        RestoreFieldFidelity restore,
        bool validationKey,
        string reasonCode,
        string message)
        => new(path, capture, restore, validationKey, reasonCode, message);
}
