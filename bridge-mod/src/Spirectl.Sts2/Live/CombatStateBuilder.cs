using System.Collections;
using System.Globalization;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;
using Spirectl.Sts2.Core.Models;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Core.Perspective;
using System.Reflection;

namespace Spirectl.Sts2.Live;

internal static class CombatStateBuilder
{
    internal static StateRunCombatRoomSnapshot? ResolveCombatRoom(
        object currentRoom,
        object? combatState,
        ICollection<StateNoticeSnapshot> notices)
    {
        var combatNotices = new List<StateNoticeSnapshot>();
        try
        {
            if (combatState is null)
            {
                combatNotices.Add(StateProjectionValues.PartialNotice("run.currentRoom.combat", "state-combat-state-unavailable", "The current combat room did not expose CombatRoom.CombatState."));
            }

            return new StateRunCombatRoomSnapshot(
                EncounterId: StateProjectionValues.ResolveModelOrId(Sts2LiveIntrospection.GetMemberValue(currentRoom, "Encounter")),
                ParentEventId: StateProjectionValues.ResolveModelOrId(Sts2LiveIntrospection.GetMemberValue(currentRoom, "ParentEventId")),
                GoldProportion: StateProjectionValues.ToDouble(Sts2LiveIntrospection.GetMemberValue(currentRoom, "GoldProportion")),
                IsPreFinished: StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(currentRoom, "IsPreFinished")),
                ShouldCreateCombat: StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(currentRoom, "ShouldCreateCombat")),
                ShouldResumeParentEventAfterCombat: StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(currentRoom, "ShouldResumeParentEventAfterCombat")),
                CombatState: combatState is null ? null : ResolveCombatState(combatState, combatNotices),
                Notices: combatNotices,
                Background: ResolveCombatBackground(currentRoom));
        }
        catch (Exception ex)
        {
            notices.Add(StateProjectionValues.PartialNotice("run.currentRoom.combat", "state-combat-room-unavailable", $"The current combat room state could not be read: {ex.Message}"));
            return new StateRunCombatRoomSnapshot(
                EncounterId: StateProjectionValues.ResolveModelOrId(Sts2LiveIntrospection.GetMemberValue(currentRoom, "Encounter")),
                ParentEventId: StateProjectionValues.ResolveModelOrId(Sts2LiveIntrospection.GetMemberValue(currentRoom, "ParentEventId")),
                GoldProportion: StateProjectionValues.ToDouble(Sts2LiveIntrospection.GetMemberValue(currentRoom, "GoldProportion")),
                IsPreFinished: StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(currentRoom, "IsPreFinished")),
                ShouldCreateCombat: StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(currentRoom, "ShouldCreateCombat")),
                ShouldResumeParentEventAfterCombat: StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(currentRoom, "ShouldResumeParentEventAfterCombat")),
                CombatState: null,
                Notices: combatNotices);
        }
    }

    // Read the room's actual composited background (the game's seeded layer pick)
    // from the combat encounter's cached BackgroundAssets: the root scene plus the
    // chosen bg layers (one per group, in order) and the chosen foreground. This is
    // the same instance the visual room used to build the background. Out of combat
    // the assets are not yet generated, so this returns null. Fully exception-safe.
    private static StateCombatBackgroundSnapshot? ResolveCombatBackground(object currentRoom)
    {
        try
        {
            var encounter = Sts2LiveIntrospection.GetMemberValue(currentRoom, "Encounter");
            var backgroundAssets = Sts2LiveIntrospection.GetMemberValue(encounter, "_backgroundAssets");
            if (backgroundAssets is null)
            {
                return null;
            }

            var encounterFpi = (StateProjectionValues.ResolveModelOrId(encounter) ?? string.Empty).ToLowerInvariant();
            var scenePath = NormalizeScenePath(
                Sts2LiveIntrospection.GetMemberValue(backgroundAssets, "BackgroundScenePath") as string);
            var layers = new List<Spirectl.Sts2.Core.Models.CombatBackgroundLayerSnapshot>();

            if (Sts2LiveIntrospection.GetMemberValue(backgroundAssets, "BgLayers") is IEnumerable bgLayers)
            {
                foreach (var entry in bgLayers)
                {
                    AddCombatBackgroundLayer(layers, entry as string, encounterFpi, forceForeground: false);
                }
            }

            AddCombatBackgroundLayer(
                layers,
                Sts2LiveIntrospection.GetMemberValue(backgroundAssets, "FgLayer") as string,
                encounterFpi,
                forceForeground: true);

            if (string.IsNullOrWhiteSpace(scenePath) && layers.Count == 0)
            {
                return null;
            }

            return new StateCombatBackgroundSnapshot(scenePath, layers);
        }
        catch
        {
            return null;
        }
    }

    private static void AddCombatBackgroundLayer(
        List<Spirectl.Sts2.Core.Models.CombatBackgroundLayerSnapshot> layers,
        string? rawPath,
        string encounterFpi,
        bool forceForeground)
    {
        var resPath = NormalizeScenePath(rawPath);
        if (string.IsNullOrWhiteSpace(resPath))
        {
            return;
        }

        var fileName = resPath[(resPath.LastIndexOf('/') + 1)..];
        var stem = fileName.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".tscn".Length]
            : fileName;
        var isForeground = forceForeground || fileName.Contains("_fg_", StringComparison.Ordinal);
        var groupKey = string.Empty;
        if (!isForeground && fileName.Contains("_bg_", StringComparison.Ordinal))
        {
            groupKey = fileName.Split("_bg_")[1].Split('_')[0];
        }

        var fpi = BackgroundOwnerFilePathIdentifier(resPath);
        var family = !string.IsNullOrEmpty(fpi) && fpi == encounterFpi ? "encounters" : "acts";
        var assetKey = string.IsNullOrEmpty(fpi)
            ? string.Empty
            : $"model://{family}/{fpi}/backgroundLayer/{stem}";
        layers.Add(new Spirectl.Sts2.Core.Models.CombatBackgroundLayerSnapshot(
            assetKey, resPath, isForeground, groupKey));
    }

    private static string? NormalizeScenePath(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : path.Replace('\\', '/').Trim();

    // res://scenes/backgrounds/<filePathIdentifier>/layers/<stem>.tscn -> <filePathIdentifier>
    private static string BackgroundOwnerFilePathIdentifier(string resPath)
    {
        const string prefix = "res://scenes/backgrounds/";
        if (!resPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var rest = resPath[prefix.Length..];
        var slash = rest.IndexOf('/');
        return slash > 0 ? rest[..slash] : string.Empty;
    }

    private static StateCombatStateSnapshot ResolveCombatState(
        object combatState,
        ICollection<StateNoticeSnapshot> notices)
    {
        var combatCreatures = StateProjectionValues.EnumerateCollection(Sts2LiveIntrospection.GetMemberValue(combatState, "Creatures"))
            .OfType<Creature>()
            .ToArray();
        var enemies = StateProjectionValues.EnumerateCollection(Sts2LiveIntrospection.GetMemberValue(combatState, "Enemies"))
            .Select((enemy, index) => ResolveCreature(enemy, notices, "run.currentRoom.combat.combatState.enemies", $"creature:enemy:{index}", combatCreatures: combatCreatures))
            .Where(enemy => enemy is not null)
            .Cast<StateRunCreatureSnapshot>()
            .ToArray();

        return new StateCombatStateSnapshot(
            SourceType: combatState.GetType().FullName ?? combatState.GetType().Name,
            CurrentSide: Sts2LiveIntrospection.GetMemberValue(combatState, "CurrentSide")?.ToString() ?? string.Empty,
            RoundNumber: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(combatState, "RoundNumber")),
            ModifierIds: StateProjectionValues.ResolveModelIds(Sts2LiveIntrospection.GetMemberValue(combatState, "Modifiers")),
            EscapedCreatureIds: StateProjectionValues.ResolveCreatureIds(Sts2LiveIntrospection.GetMemberValue(combatState, "EscapedCreatures"), notices, "run.currentRoom.combat.combatState.escapedCreatureIds", "creature:escaped"),
            Enemies: enemies,
            PlayerActionsDisabled: CombatManager.Instance?.PlayerActionsDisabled ?? false,
            // The bridge never derives transient effects from live combat (a host folds the
            // event stream); only fixture-authored effects flow, via the registry.
            TransientEffects: Sts2AuthoredTransientEffectsRegistry.Snapshot());
    }

    internal static IReadOnlyList<StateRunPlayerSnapshot> ResolveRunPlayers(
        RunState runState,
        ulong? localNetId,
        string? localPlayerId,
        string? hostPlayerId,
        object? platform,
        bool includeCombat,
        object? combatState,
        IReadOnlySet<ulong>? connectedNetIds = null)
    {
        if (Sts2LiveIntrospection.GetMemberValue(runState, "Players") is not IEnumerable players)
        {
            return [];
        }

        var result = new List<StateRunPlayerSnapshot>();
        var index = 0;
        foreach (var player in players)
        {
            if (player is null)
            {
                continue;
            }

            var netId = StateProjectionValues.ToUInt64(Sts2LiveIntrospection.GetMemberValue(player, "NetId"));
            var playerId = netId.HasValue ? $"p:{netId.Value}" : $"p:unknown:{index}";
            var playerNotices = new List<StateNoticeSnapshot>();
            var inventoryComplete = true;
            var deck = ResolveDeck(player, playerId, playerNotices, ref inventoryComplete);
            var relics = ResolveRelics(player, playerId, playerNotices, ref inventoryComplete);
            var potions = ResolvePotions(player, playerId, combatState, playerNotices, ref inventoryComplete);
            var displayName = netId.HasValue ? Sts2LobbyNameResolver.Resolve(platform, netId.Value, playerNotices) : null;
            var creature = ResolveCreature(
                Sts2LiveIntrospection.GetMemberValue(player, "Creature"),
                playerNotices,
                "run.players[].creature",
                $"creature:{playerId}:player",
                includeCombatFields: includeCombat);
            var combat = includeCombat ? ResolvePlayerCombat(player, playerId, playerNotices) : null;
            var overlays = ResolvePlayerOverlays(playerId, netId, localNetId);

            result.Add(new StateRunPlayerSnapshot(
                Id: playerId,
                SourceType: player.GetType().FullName ?? player.GetType().Name,
                NetId: netId?.ToString(CultureInfo.InvariantCulture),
                DisplayName: displayName,
                CharacterId: StateProjectionValues.ResolveModelId(Sts2LiveIntrospection.GetMemberValue(player, "Character")) ?? "unknown",
                IsLocal: netId.HasValue && localNetId.HasValue && netId.Value == localNetId.Value,
                IsHost: string.Equals(playerId, hostPlayerId, StringComparison.Ordinal),
                IsRemote: !string.IsNullOrWhiteSpace(localPlayerId) && !string.Equals(playerId, localPlayerId, StringComparison.Ordinal),
                Creature: creature,
                Gold: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(player, "Gold")),
                Deck: deck,
                Relics: relics,
                InventoryComplete: inventoryComplete,
                Notices: playerNotices,
                Combat: combat,
                Overlays: overlays,
                Potions: potions,
                CanRemovePotions: Sts2LiveIntrospection.GetMemberValue(player, "CanRemovePotions") is not { } canRemovePotions
                    || StateProjectionValues.ToBoolean(canRemovePotions),
                IsConnected: Sts2RunPlayerConnectivity.IsConnected(connectedNetIds, netId)));
            index++;
        }

        return result;
    }

    private static IReadOnlyList<StateRunOverlaySnapshot> ResolvePlayerOverlays(
        string playerId,
        ulong? playerNetId,
        ulong? localNetId)
    {
        if (!playerNetId.HasValue || !Sts2RunOverlayRegistry.IsObservableLocalOwner(playerNetId.Value, localNetId))
        {
            return [];
        }

        var overlays = Sts2RunOverlayRegistry.GetForPlayer(playerId).ToList();
        if (overlays.All(overlay => overlay.ChooseACard is null)
            && Sts2ChooseACardOverlayHooks.ResolveVisibleChooseACardOverlay(playerId) is { } visibleChooseACard)
        {
            overlays.Add(visibleChooseACard);
        }

        if (overlays.All(overlay => overlay.Rewards is null)
            && Sts2RewardsOverlayInspector.ResolveVisibleRewardsOverlay(playerId) is { } visibleRewards)
        {
            overlays.Add(visibleRewards);
        }

        if (overlays.All(overlay => overlay.DeckCardSelection is null)
            && Sts2DeckCardSelectionOverlayHooks.ResolveVisibleDeckCardSelectionOverlay(playerId) is { } visibleDeckSelection)
        {
            overlays.Add(visibleDeckSelection);
        }

        // Simple-grid and bundle screens are local-initiated (no per-player command
        // hook), so attach them to the local seat only to avoid leaking the overlay
        // across co-op players.
        var isLocalPlayer = localNetId.HasValue && playerNetId.Value == localNetId.Value;
        if (isLocalPlayer)
        {
            if (overlays.All(overlay => overlay.SimpleGridCardSelection is null)
                && Sts2CardSelectionScreenInspector.ResolveVisibleSimpleGridCardSelectionOverlay(playerId) is { } visibleSimpleGrid)
            {
                overlays.Add(visibleSimpleGrid);
            }

            if (overlays.All(overlay => overlay.BundleCardSelection is null)
                && Sts2CardSelectionScreenInspector.ResolveVisibleBundleCardSelectionOverlay(playerId) is { } visibleBundle)
            {
                overlays.Add(visibleBundle);
            }

            if (overlays.All(overlay => overlay.CrystalSphere is null)
                && Sts2CrystalSphereOverlayInspector.ResolveVisibleCrystalSphereOverlay(playerId) is { } visibleCrystalSphere)
            {
                overlays.Add(visibleCrystalSphere);
            }
        }

        return overlays;
    }

    private static StateRunPlayerCombatSnapshot? ResolvePlayerCombat(
        object player,
        string playerId,
        ICollection<StateNoticeSnapshot> notices)
    {
        var combat = Sts2LiveIntrospection.GetMemberValue(player, "PlayerCombatState");
        if (combat is null)
        {
            return null;
        }

        var combatNotices = new List<StateNoticeSnapshot>();
        // Host-computed card values (damage matrix + description template) are HAND-only: the enemies a
        // played card could hit. Computed once per player and passed to the hand pile alone.
        var hostValueEnemies = Sts2CombatPreviewCore.HittableEnemies();
        return new StateRunPlayerCombatSnapshot(
            SourceType: combat.GetType().FullName ?? combat.GetType().Name,
            HasEndedTurn: ResolvePlayerHasEndedTurn(player),
            Energy: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(combat, "Energy")),
            MaxEnergy: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(combat, "MaxEnergy")),
            Stars: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(combat, "Stars")),
            Hand: ResolveCombatCardPile(Sts2LiveIntrospection.GetMemberValue(combat, "Hand"), playerId, combatNotices, "run.players[].combat.hand", hostValueEnemies),
            DrawPile: ResolveCombatCardPile(Sts2LiveIntrospection.GetMemberValue(combat, "DrawPile"), playerId, combatNotices, "run.players[].combat.drawPile"),
            DiscardPile: ResolveCombatCardPile(Sts2LiveIntrospection.GetMemberValue(combat, "DiscardPile"), playerId, combatNotices, "run.players[].combat.discardPile"),
            ExhaustPile: ResolveCombatCardPile(Sts2LiveIntrospection.GetMemberValue(combat, "ExhaustPile"), playerId, combatNotices, "run.players[].combat.exhaustPile"),
            PlayPile: ResolveCombatCardPile(Sts2LiveIntrospection.GetMemberValue(combat, "PlayPile"), playerId, combatNotices, "run.players[].combat.playPile"),
            PetCreatures: StateProjectionValues.EnumerateCollection(Sts2LiveIntrospection.GetMemberValue(combat, "Pets"))
                .Select((pet, index) => ResolveCreature(pet, combatNotices, "run.players[].combat.petCreatures", $"creature:{playerId}:pet:{index}"))
                .Where(pet => pet is not null)
                .Cast<StateRunCreatureSnapshot>()
                .ToArray(),
            OrbQueue: ResolveCombatOrbQueue(Sts2LiveIntrospection.GetMemberValue(combat, "OrbQueue"), playerId),
            Notices: combatNotices);
    }

    // Co-op turn state: mirrors the live end-turn button / multiplayer turn-end
    // indicator (CombatManager.IsPlayerReadyToEndTurn), OR a fixture-authored
    // ended-turn intent (Sts2AuthoredEndedTurnRegistry) for the "ended but still
    // holding a locked hand" state the live turn machine cannot itself hold. False
    // outside combat and for any seat still acting.
    private static bool ResolvePlayerHasEndedTurn(object player)
    {
        if (player is not MegaCrit.Sts2.Core.Entities.Players.Player typedPlayer)
        {
            return false;
        }

        if (Sts2AuthoredEndedTurnRegistry.Contains(typedPlayer.NetId))
        {
            return true;
        }

        if (CombatManager.Instance is not { } combatManager)
        {
            return false;
        }

        try
        {
            return combatManager.IsPlayerReadyToEndTurn(typedPlayer);
        }
        catch
        {
            // State reads must never throw if the live combat manager is
            // mid-transition or the player is not part of the active combat.
            return false;
        }
    }

    private static StateRunCreatureSnapshot? ResolveCreature(
        object? creature,
        ICollection<StateNoticeSnapshot>? notices = null,
        string path = "run.players[].creature",
        string fallbackId = "creature:unknown",
        bool includeCombatFields = true,
        IReadOnlyList<Creature>? combatCreatures = null)
    {
        if (creature is null)
        {
            return null;
        }

        var powerInstances = includeCombatFields
            ? ResolvePowerInstances(
                Sts2LiveIntrospection.GetMemberValue(creature, "Powers"),
                StateProjectionValues.ResolveCreatureId(creature, null, path, fallbackId),
                notices,
                path)
            : null;

        return new StateRunCreatureSnapshot(
            SourceType: creature.GetType().FullName ?? creature.GetType().Name,
            CurrentHp: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(creature, "CurrentHp")),
            MaxHp: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(creature, "MaxHp")),
            Id: includeCombatFields ? StateProjectionValues.ResolveCreatureId(creature, notices, path, fallbackId) : null,
            ModelId: StateProjectionValues.ResolveModelOrId(Sts2LiveIntrospection.GetMemberValue(creature, "ModelId"))
                ?? StateProjectionValues.ResolveModelId(Sts2LiveIntrospection.GetMemberValue(creature, "Monster"))
                ?? StateProjectionValues.ResolveModelId(Sts2LiveIntrospection.GetMemberValue(Sts2LiveIntrospection.GetMemberValue(creature, "Player"), "Character")),
            Side: includeCombatFields ? Sts2LiveIntrospection.GetMemberValue(creature, "Side")?.ToString() : null,
            SlotName: includeCombatFields ? StateProjectionValues.NormalizeNullable(Sts2LiveIntrospection.GetMemberValue(creature, "SlotName")?.ToString()) : null,
            Block: includeCombatFields ? StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(creature, "Block")) : null,
            IsHittable: includeCombatFields ? StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(creature, "IsHittable")) : null,
            PowerInstances: powerInstances,
            NextMove: includeCombatFields ? ResolveCreatureNextMove(creature, combatCreatures, notices, path) : null,
            // The card the creature body hover previews: the creature hover aggregates its
            // powers' tips, so reuse the first power that carries a previewedCard (each power's
            // is resolved from its CardHoverTip, e.g. PERSONAL_HIVE_POWER -> Dazed). This matches
            // what the creature body shows, without depending on a Creature.HoverTips member that
            // isn't populated during headless state extraction.
            PreviewedCard: powerInstances?.FirstOrDefault(pi => pi.PreviewedCard is not null)?.PreviewedCard);
    }

    private static StateCombatNextMoveSnapshot? ResolveCreatureNextMove(
        object? creature,
        IReadOnlyList<Creature>? combatCreatures,
        ICollection<StateNoticeSnapshot>? notices,
        string path)
    {
        if (creature is not Creature typed || typed.Monster is null)
        {
            return null;
        }

        try
        {
            var move = Sts2CombatFacts.ResolveNextMove(typed, combatCreatures ?? []);
            if (move is not { } resolved)
            {
                return null;
            }

            return new StateCombatNextMoveSnapshot(
                Id: resolved.Id,
                Intents: resolved.Intents
                    .Select(intent => new StateCombatIntentSnapshot(
                        Type: intent.Type,
                        Attack: intent.IsAttack ? new StateCombatAttackIntentSnapshot(intent.Damage, intent.Hits, intent.Repeats) : null,
                        CardCount: intent.CardCount))
                    .ToArray());
        }
        catch (Exception ex)
        {
            notices?.Add(StateProjectionValues.PartialNotice($"{path}.nextMove", "state-next-move-unavailable", $"The creature next move could not be read: {ex.Message}"));
            return null;
        }
    }

    private static StateCombatCardPileSnapshot? ResolveCombatCardPile(
        object? pile,
        string playerId,
        ICollection<StateNoticeSnapshot> notices,
        string path,
        // Non-null only for the HAND pile: the live hittable enemies to build each card's host-computed
        // damage matrix + description template against. Other piles stay base (deck/draw/discard/exhaust).
        IReadOnlyList<Creature>? hostValueEnemies = null)
    {
        if (pile is null)
        {
            return null;
        }

        try
        {
            var type = Sts2LiveIntrospection.GetMemberValue(pile, "Type")?.ToString() ?? string.Empty;
            var id = $"pile:{playerId}:{StateProjectionValues.NormalizeIdentifier(type) ?? "unknown"}";
            return new StateCombatCardPileSnapshot(
                SourceType: pile.GetType().FullName ?? pile.GetType().Name,
                Id: id,
                Type: type,
                Cards: StateProjectionValues.EnumerateCollection(Sts2LiveIntrospection.GetMemberValue(pile, "Cards"))
                    .Select((card, index) => ResolveCombatCard(card, playerId, index, notices, $"{path}.cards", hostValueEnemies))
                    .Where(card => card is not null)
                    .Cast<StateCombatCardSnapshot>()
                    .ToArray());
        }
        catch (Exception ex)
        {
            notices.Add(StateProjectionValues.PartialNotice(path, "state-combat-pile-unavailable", $"The combat card pile could not be read: {ex.Message}"));
            return null;
        }
    }

    private static StateCombatCardSnapshot? ResolveCombatCard(
        object? card,
        string fallbackPlayerId,
        int index,
        ICollection<StateNoticeSnapshot> notices,
        string path,
        // Non-null only for hand cards: compute the host damage matrix + description template.
        IReadOnlyList<Creature>? hostValueEnemies = null)
    {
        if (card is null)
        {
            return null;
        }

        try
        {
            // Use the unified game-native combat card id so state (and the
            // presentation actions surface that reads it) advertise exactly the
            // id the action executor resolves via NetCombatCardDb.
            var cardId = Sts2CombatIds.CardId(card, fallbackPlayerId, index, notices);
            var currentTarget = Sts2LiveIntrospection.GetMemberValue(card, "CurrentTarget");
            var model = card as CardModel;
            var energyCost = model is not null
                ? model.EnergyCost.GetWithModifiers(CostModifiers.All)
                : StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(Sts2LiveIntrospection.GetMemberValue(card, "EnergyCost"), "Canonical"));
            var unplayableReason = model is not null
                ? Sts2CombatFacts.ResolveUnplayableReasons(model)
                : (IReadOnlyList<string>)[];
            var glow = model is not null
                ? Sts2CombatFacts.ResolveCardGlow(model)
                : (Gold: false, Red: false);
            var affliction = Sts2LiveIntrospection.GetMemberValue(card, "Affliction");
            var afflictionModelId = StateProjectionValues.ResolveModelId(affliction);
            var upgradeLevel = StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(card, "CurrentUpgradeLevel")
                ?? Sts2LiveIntrospection.GetMemberValue(card, "UpgradeLevel"));
            var hostValues = hostValueEnemies is not null && model is not null
                ? Sts2CardHostValueResolver.Resolve(model, cardId, upgradeLevel, energyCost, hostValueEnemies, notices, path)
                : null;
            return new StateCombatCardSnapshot(
                Id: cardId,
                ModelId: StateProjectionValues.ResolveModelId(card) ?? StateProjectionValues.Slug(card.GetType().Name),
                UpgradeLevel: upgradeLevel,
                CurrentTargetCreatureId: currentTarget is null ? null : StateProjectionValues.ResolveCreatureId(currentTarget, notices, path, $"creature:target:{cardId}"),
                ExhaustOnNextPlay: StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(card, "ExhaustOnNextPlay")),
                HasSingleTurnRetain: StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(card, "HasSingleTurnRetain")),
                HasSingleTurnSly: StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(card, "HasSingleTurnSly")),
                ShouldRetainThisTurn: StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(card, "ShouldRetainThisTurn")),
                EnergyCost: energyCost,
                StarCost: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(card, "CurrentStarCost")),
                UnplayableReason: unplayableReason,
                ShouldGlowGold: glow.Gold,
                ShouldGlowRed: glow.Red,
                AfflictionModelId: afflictionModelId,
                AfflictionAmount: afflictionModelId is null
                    ? 0
                    : StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(affliction, "Amount")),
                Enchantment: Sts2CardStateSnapshotFactory.ResolveEnchantmentSnapshot(
                    Sts2LiveIntrospection.GetMemberValue(card, "Enchantment"), card),
                DescriptionTemplate: hostValues?.DescriptionTemplate,
                DescriptionText: hostValues?.DescriptionText,
                Preview: hostValues?.Preview);
        }
        catch (Exception ex)
        {
            notices.Add(StateProjectionValues.PartialNotice(path, "state-combat-card-unavailable", $"A combat card could not be read: {ex.Message}"));
            return null;
        }
    }

    private static StateCombatOrbQueueSnapshot? ResolveCombatOrbQueue(object? queue, string playerId)
    {
        if (queue is null)
        {
            return null;
        }

        return new StateCombatOrbQueueSnapshot(
            SourceType: queue.GetType().FullName ?? queue.GetType().Name,
            Capacity: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(queue, "Capacity")),
            Orbs: StateProjectionValues.EnumerateCollection(Sts2LiveIntrospection.GetMemberValue(queue, "Orbs"))
                .Select((orb, index) => new StateCombatOrbSnapshot(
                    Id: $"orb:{playerId}:{index}:{StateProjectionValues.ResolveModelId(orb) ?? StateProjectionValues.Slug(orb?.GetType().Name)}",
                    ModelId: StateProjectionValues.ResolveModelId(orb) ?? StateProjectionValues.Slug(orb?.GetType().Name),
                    PassiveVal: StateProjectionValues.ToDouble(Sts2LiveIntrospection.GetMemberValue(orb, "PassiveVal")),
                    EvokeVal: StateProjectionValues.ToDouble(Sts2LiveIntrospection.GetMemberValue(orb, "EvokeVal")),
                    OwnerPlayerId: Sts2LiveIntrospection.GetMemberValue(orb, "Owner") is { } owner ? StateProjectionValues.ResolveRunPlayerId(owner, 0) : playerId,
                    HasBeenRemovedFromState: StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(orb, "HasBeenRemovedFromState"))))
                .ToArray());
    }

    private static IReadOnlyList<StateCombatPowerInstanceSnapshot> ResolvePowerInstances(
        object? powers,
        string? ownerCreatureId,
        ICollection<StateNoticeSnapshot>? notices,
        string path)
        => StateProjectionValues.EnumerateCollection(powers)
            .Select((power, index) => ResolvePowerInstance(power, ownerCreatureId, index, notices, path))
            .Where(power => power is not null)
            .Cast<StateCombatPowerInstanceSnapshot>()
            .ToArray();

    private static StateCombatPowerInstanceSnapshot? ResolvePowerInstance(
        object? power,
        string? ownerCreatureId,
        int index,
        ICollection<StateNoticeSnapshot>? notices,
        string path)
    {
        if (power is null)
        {
            return null;
        }

        try
        {
            var modelId = StateProjectionValues.ResolveModelId(power) ?? StateProjectionValues.Slug(power.GetType().Name);
            var owner = Sts2LiveIntrospection.GetMemberValue(power, "Owner");
            var target = Sts2LiveIntrospection.GetMemberValue(power, "Target");
            var applier = Sts2LiveIntrospection.GetMemberValue(power, "Applier");
            var powerId = $"power:{ownerCreatureId ?? "creature:unknown"}:{index}:{modelId}";
            return new StateCombatPowerInstanceSnapshot(
                Id: powerId,
                ModelId: modelId,
                SourceType: power.GetType().FullName ?? power.GetType().Name,
                Amount: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(power, "Amount")),
                DisplayAmount: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(power, "DisplayAmount")),
                AmountOnTurnStart: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(power, "AmountOnTurnStart")),
                Type: Sts2LiveIntrospection.GetMemberValue(power, "Type")?.ToString() ?? string.Empty,
                TypeForCurrentAmount: Sts2LiveIntrospection.GetMemberValue(power, "TypeForCurrentAmount")?.ToString() ?? string.Empty,
                StackType: Sts2LiveIntrospection.GetMemberValue(power, "StackType")?.ToString() ?? string.Empty,
                IsVisible: StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(power, "IsVisible")),
                SkipNextDurationTick: StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(power, "SkipNextDurationTick")),
                AmountLabelColor: Sts2LiveIntrospection.GetMemberValue(power, "AmountLabelColor") is Color color ? StateProjectionValues.CssColor(color) : null,
                OwnerCreatureId: owner is null ? ownerCreatureId : StateProjectionValues.ResolveCreatureId(owner, notices, $"{path}.powerInstances", $"creature:power-owner:{index}"),
                TargetCreatureId: target is null ? null : StateProjectionValues.ResolveCreatureId(target, notices, $"{path}.powerInstances", $"creature:power-target:{index}"),
                ApplierCreatureId: applier is null ? null : StateProjectionValues.ResolveCreatureId(applier, notices, $"{path}.powerInstances", $"creature:power-applier:{index}"),
                // The instance's resolved tip stack (PowerModel.HoverTips: own tip with
                // the smart description's live owner/amount vars, then ExtraHoverTips).
                HoverTips: Sts2HoverTipProjection.ExtractResolvedTips(Sts2LiveIntrospection.GetMemberValue(power, "HoverTips")),
                // The card this power previews (a CardHoverTip in its HoverTips/ExtraHoverTips,
                // e.g. SwipePower's stolen card, PainfulStabs->Wound) — drives the focus card
                // preview. Null otherwise.
                PreviewedCard: Sts2HoverTipProjection.TryResolvePreviewedCard(
                    Sts2LiveIntrospection.GetMemberValue(power, "HoverTips"), $"card:{powerId}:preview"));
        }
        catch (Exception ex)
        {
            notices?.Add(StateProjectionValues.PartialNotice($"{path}.powerInstances", "state-combat-power-unavailable", $"A combat power instance could not be read: {ex.Message}"));
            return null;
        }
    }

    private static StateCardPileSnapshot? ResolveDeck(
        object player,
        string playerId,
        ICollection<StateNoticeSnapshot> notices,
        ref bool inventoryComplete)
    {
        try
        {
            var deck = Sts2LiveIntrospection.GetMemberValue(player, "Deck");
            if (deck is null)
            {
                inventoryComplete = false;
                notices.Add(StateProjectionValues.PartialNotice("run.players[].deck", "run-player-deck-unavailable", "The player's deck was not available in the attached runtime."));
                return null;
            }

            var cards = StateProjectionValues.EnumerateCollection(Sts2LiveIntrospection.GetMemberValue(deck, "Cards"))
                .Select((card, index) => Sts2CardStateSnapshotFactory.CreateFallback(card, $"card:{playerId}:deck:{index}"))
                .ToArray();

            return new StateCardPileSnapshot(
                SourceType: deck.GetType().FullName ?? deck.GetType().Name,
                Id: $"deck:{playerId}",
                Type: Sts2LiveIntrospection.GetMemberValue(deck, "Type")?.ToString() ?? "Deck",
                Count: Math.Max(StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(Sts2LiveIntrospection.GetMemberValue(deck, "Cards"), "Count")), cards.Length),
                Cards: cards,
                OrderObservable: true);
        }
        catch (Exception ex)
        {
            inventoryComplete = false;
            notices.Add(StateProjectionValues.PartialNotice("run.players[].deck", "run-player-deck-unavailable", $"The player's deck could not be read: {ex.Message}"));
            return null;
        }
    }

    private static IReadOnlyList<StateRelicSnapshot> ResolveRelics(
        object player,
        string playerId,
        ICollection<StateNoticeSnapshot> notices,
        ref bool inventoryComplete)
    {
        try
        {
            var relics = Sts2LiveIntrospection.GetMemberValue(player, "Relics");
            if (relics is null)
            {
                inventoryComplete = false;
                notices.Add(StateProjectionValues.PartialNotice("run.players[].relics", "run-player-relics-unavailable", "The player's relics were not available in the attached runtime."));
                return [];
            }

            return StateProjectionValues.EnumerateCollection(relics)
                .Select((relic, index) =>
                {
                    var modelId = StateProjectionValues.ResolveModelId(relic) ?? StateProjectionValues.Slug(relic?.GetType().Name);
                    return new StateRelicSnapshot(
                        Id: $"relic:{playerId}:{index}:{modelId}",
                        ModelId: modelId,
                        SlotIndex: index,
                        // Faithful to NRelicInventoryHolder.RefreshAmount: the badge is the relic
                        // model's ShowCounter (bool) + DisplayAmount (int) — e.g. Girya->TimesLifted.
                        // Shown even when DisplayAmount == 0 (Girya at 0 lifts renders "0").
                        HasCounter: StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(relic, "ShowCounter")),
                        Counter: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(relic, "DisplayAmount")));
                })
                .ToArray();
        }
        catch (Exception ex)
        {
            inventoryComplete = false;
            notices.Add(StateProjectionValues.PartialNotice("run.players[].relics", "run-player-relics-unavailable", $"The player's relics could not be read: {ex.Message}"));
            return [];
        }
    }

    private static IReadOnlyList<StateCombatPotionSnapshot> ResolvePotions(
        object player,
        string playerId,
        object? combatState,
        ICollection<StateNoticeSnapshot> notices,
        ref bool inventoryComplete)
    {
        try
        {
            var potionSlots = Sts2LiveIntrospection.GetMemberValue(player, "PotionSlots");
            if (potionSlots is null)
            {
                inventoryComplete = false;
                notices.Add(StateProjectionValues.PartialNotice("run.players[].potions", "run-player-potion-slots-unavailable", "The player's potion slots were not available in the attached runtime."));
                return [];
            }

            return StateProjectionValues.EnumerateCollection(potionSlots)
                .Select((potion, index) => potion is null
                    ? new StateCombatPotionSnapshot(index, ModelId: null, IsQueued: false, PassesUsabilityCheck: true)
                    : ResolvePotion(potion, index))
                .ToArray();
        }
        catch (Exception ex)
        {
            inventoryComplete = false;
            notices.Add(StateProjectionValues.PartialNotice("run.players[].potions", "run-player-potion-slots-unavailable", $"The player's potion slots could not be read: {ex.Message}"));
            return [];
        }
    }

    private static StateCombatPotionSnapshot ResolvePotion(object potion, int slotIndex)
    {
        var modelId = StateProjectionValues.ResolveModelId(potion) ?? StateProjectionValues.Slug(potion.GetType().Name);
        var isQueued = StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(potion, "IsQueued"));
        var passesUsability = Sts2LiveIntrospection.GetMemberValue(potion, "PassesCustomUsabilityCheck") is not { } passes || StateProjectionValues.ToBoolean(passes);

        return new StateCombatPotionSnapshot(
            Id: slotIndex,
            ModelId: modelId,
            IsQueued: isQueued,
            PassesUsabilityCheck: passesUsability);
    }

    private static IReadOnlyList<string> ResolvePotionTargetIds(object potion, string playerId, object combatState)
    {
        if (combatState is not CombatState typedCombatState)
        {
            return [];
        }

        var targetType = Sts2LiveIntrospection.GetMemberValue(potion, "TargetType")?.ToString();
        return targetType switch
        {
            "Self" => typedCombatState.Allies
                .Where(creature => creature.Player is not null && IsPlayerId(creature.Player!, playerId))
                .Select((creature, index) => Sts2CombatIds.CreatureId(creature, index))
                .ToArray(),
            "AnyEnemy" => typedCombatState.Enemies
                .Select((enemy, index) => new { enemy, id = Sts2CombatIds.CreatureId(enemy, index) })
                .Where(entry => entry.enemy.IsAlive && SafeBool(() => ((dynamic)potion).CanPlayTargeting(entry.enemy), fallback: true))
                .Select(entry => entry.id)
                .ToArray(),
            "AnyPlayer" or "AnyAlly" => typedCombatState.Allies
                .Where(creature => creature.Player is not null && creature.IsAlive)
                .Select((creature, index) => Sts2CombatIds.CreatureId(creature, index))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            _ => [],
        };
    }

    private static bool IsPlayerId(MegaCrit.Sts2.Core.Entities.Players.Player player, string playerId)
        => string.Equals(Sts2CombatIds.PlayerId(player), playerId, StringComparison.Ordinal);

    private static bool PotionNeedsTarget(object potion)
    {
        var targetType = Sts2LiveIntrospection.GetMemberValue(potion, "TargetType")?.ToString();
        return targetType is "Self" or "AnyEnemy" or "AnyPlayer" or "AnyAlly";
    }

    private static bool PotionCanBeUsedManually(object potion)
    {
        var usage = Sts2LiveIntrospection.GetMemberValue(potion, "Usage")?.ToString();
        return !string.Equals(usage, "Automatic", StringComparison.Ordinal);
    }

    private static bool SafeBool(Func<bool> action, bool fallback = false)
    {
        try
        {
            return action();
        }
        catch
        {
            return fallback;
        }
    }

    internal static StateSelectedPotionSnapshot? ResolveSelectedPotionView(object player)
    {
        var holder = ResolveActivePotionHolder(player);
        if (holder is null)
        {
            return null;
        }

        var potionNode = Sts2LiveIntrospection.GetMemberValue(holder, "Potion");
        var potion = Sts2LiveIntrospection.GetMemberValue(potionNode, "Model");
        if (potion is null)
        {
            return null;
        }

        var slotIndex = ResolvePotionSlotIndex(player, potion);
        if (slotIndex < 0)
        {
            return null;
        }

        var selectedPotion = ResolveSelectedPotion();
        var isTargeting = Sts2TargetManagerAccess.IsInSelection
            && ReferenceEquals(selectedPotion, potion);
        var popup = Sts2LiveIntrospection.GetMemberValue(holder, "_popup");
        var isPopupOpen = IsLiveGodotObject(popup);
        if (!isTargeting && !isPopupOpen)
        {
            return null;
        }

        return new StateSelectedPotionSnapshot(
            isTargeting ? "targeting" : "popup",
            slotIndex);
    }

    private static object? ResolveActivePotionHolder(object player)
    {
        var selectedPotion = Sts2TargetManagerAccess.IsInSelection
            ? ResolveSelectedPotion()
            : null;
        var holders = Sts2LiveIntrospection.GetMemberValue(
            Sts2LiveIntrospection.GetMemberValue(
                Sts2LiveIntrospection.GetMemberValue(
                    Sts2LiveIntrospection.GetMemberValue(NRun.Instance, "GlobalUi"),
                    "TopBar"),
                "PotionContainer"),
            "_holders");
        foreach (var holder in StateProjectionValues.EnumerateCollection(holders))
        {
            if (holder is null)
            {
                continue;
            }

            var potionNode = Sts2LiveIntrospection.GetMemberValue(holder, "Potion");
            var potion = Sts2LiveIntrospection.GetMemberValue(potionNode, "Model");
            if (potion is null || ResolvePotionSlotIndex(player, potion) < 0)
            {
                continue;
            }

            if (ReferenceEquals(potion, selectedPotion)
                || StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(holder, "_potionTargeting"))
                || IsLiveGodotObject(Sts2LiveIntrospection.GetMemberValue(holder, "_popup")))
            {
                return holder;
            }
        }

        return null;
    }

    private static object? ResolveSelectedPotion()
        => Sts2LiveIntrospection.GetMemberValue(RunManager.Instance?.HoveredModelTracker, "_localSelectedPotion");

    private static int ResolvePotionSlotIndex(object player, object potion)
    {
        var potionSlots = Sts2LiveIntrospection.GetMemberValue(player, "PotionSlots");
        var index = 0;
        foreach (var slotPotion in StateProjectionValues.EnumerateCollection(potionSlots))
        {
            if (ReferenceEquals(slotPotion, potion))
            {
                return index;
            }

            index += 1;
        }

        return -1;
    }

    private static bool IsLiveGodotObject(object? value)
        => value is GodotObject godotObject && GodotObject.IsInstanceValid(godotObject);

}
