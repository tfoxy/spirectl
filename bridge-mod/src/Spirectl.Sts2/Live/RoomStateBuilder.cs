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

internal static class RoomStateBuilder
{
    internal static StateRunEventRoomSnapshot? ResolveEventRoom(
        RunState runState,
        RunManager manager,
        object currentRoom,
        object? canonicalEvent,
        ICollection<StateNoticeSnapshot> notices)
    {
        var eventNotices = new List<StateNoticeSnapshot>();
        try
        {
            var synchronizer = Sts2LiveIntrospection.GetMemberValue(manager, "EventSynchronizer");
            if (synchronizer is null)
            {
                eventNotices.Add(StateProjectionValues.PartialNotice("run.currentRoom.event", "state-event-synchronizer-unavailable", "The current event room did not expose RunManager.EventSynchronizer."));
            }

            return new StateRunEventRoomSnapshot(
                Scene: ResolveEventScene(currentRoom, canonicalEvent, eventNotices),
                CanonicalEventModelId: StateProjectionValues.ResolveModelId(canonicalEvent),
                CanonicalSourceType: canonicalEvent?.GetType().FullName,
                IsPreFinished: StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(currentRoom, "IsPreFinished")),
                IsShared: StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(synchronizer, "IsShared"))
                    || StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(canonicalEvent, "IsShared")),
                PlayerStates: ResolveEventPlayerStates(runState, synchronizer, eventNotices),
                SharedVotes: ResolveSharedVotes(runState, synchronizer, eventNotices),
                Notices: eventNotices);
        }
        catch (Exception ex)
        {
            notices.Add(StateProjectionValues.PartialNotice("run.currentRoom.event", "state-event-room-unavailable", $"The current event room state could not be read: {ex.Message}"));
            return new StateRunEventRoomSnapshot(
                Scene: ResolveEventScene(currentRoom, canonicalEvent, eventNotices),
                CanonicalEventModelId: StateProjectionValues.ResolveModelId(canonicalEvent),
                CanonicalSourceType: canonicalEvent?.GetType().FullName,
                IsPreFinished: StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(currentRoom, "IsPreFinished")),
                IsShared: false,
                PlayerStates: [],
                SharedVotes: [],
                Notices: eventNotices);
        }
    }

    private static string? ResolveEventScene(object currentRoom, object? canonicalEvent, ICollection<StateNoticeSnapshot> notices)
    {
        notices.Add(StateProjectionValues.PartialNotice(
            "run.currentRoom.event.scene",
            "state-event-scene-unavailable",
            "The current event room did not expose an observable EventContainer current scene."));
        return null;
    }

    private static IReadOnlyList<StateRunEventPlayerStateSnapshot> ResolveEventPlayerStates(
        RunState runState,
        object? synchronizer,
        ICollection<StateNoticeSnapshot> notices)
    {
        var players = StateProjectionValues.EnumerateCollection(Sts2LiveIntrospection.GetMemberValue(runState, "Players")).ToArray();
        var result = new List<StateRunEventPlayerStateSnapshot>();
        for (var index = 0; index < players.Length; index++)
        {
            var player = players[index];
            if (player is null)
            {
                continue;
            }

            var playerId = StateProjectionValues.ResolveRunPlayerId(player, index);
            try
            {
                var playerEvent = Sts2LiveIntrospection.InvokeMethod(synchronizer, "GetEventForPlayer", player);
                if (playerEvent is null)
                {
                    notices.Add(StateProjectionValues.PartialNotice("run.currentRoom.event.playerStates", "state-player-event-unavailable", $"The event state for player {playerId} was not available."));
                    continue;
                }

                result.Add(ResolveEventPlayerState(playerId, playerEvent, notices));
            }
            catch (Exception ex)
            {
                notices.Add(StateProjectionValues.PartialNotice("run.currentRoom.event.playerStates", "state-player-event-unavailable", $"The event state for player {playerId} could not be read: {ex.Message}"));
            }
        }

        return result;
    }

    private static StateRunEventPlayerStateSnapshot ResolveEventPlayerState(
        string playerId,
        object playerEvent,
        ICollection<StateNoticeSnapshot> notices)
    {
        var canonical = Sts2LiveIntrospection.GetMemberValue(playerEvent, "CanonicalInstance");
        var owner = Sts2LiveIntrospection.GetMemberValue(playerEvent, "Owner");
        return new StateRunEventPlayerStateSnapshot(
            PlayerId: playerId,
            EventModelId: StateProjectionValues.ResolveModelId(playerEvent),
            CanonicalEventModelId: StateProjectionValues.ResolveModelId(canonical),
            SourceType: playerEvent.GetType().FullName ?? playerEvent.GetType().Name,
            OwnerPlayerId: owner is null ? null : StateProjectionValues.ResolveRunPlayerId(owner, 0),
            LayoutType: Sts2LiveIntrospection.GetMemberValue(playerEvent, "LayoutType")?.ToString() ?? string.Empty,
            IsFinished: StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(playerEvent, "IsFinished")),
            DescriptionLoc: StateProjectionValues.ResolveLocRef(Sts2LiveIntrospection.GetMemberValue(playerEvent, "Description"), notices, "run.currentRoom.event.playerStates[].descriptionLoc"),
            Options: ResolveEventOptions(Sts2LiveIntrospection.GetMemberValue(playerEvent, "CurrentOptions"), notices),
            Ancient: ResolveAncientEvent(playerEvent, notices));
    }

    private static IReadOnlyList<StateRunEventOptionSnapshot> ResolveEventOptions(
        object? options,
        ICollection<StateNoticeSnapshot> notices)
    {
        var result = new List<StateRunEventOptionSnapshot>();
        foreach (var option in StateProjectionValues.EnumerateCollection(options))
        {
            var index = result.Count;
            if (option is null)
            {
                continue;
            }

            try
            {
                var optionId = Sts2EventRoomIds.ResolveOptionId(
                    Sts2LiveIntrospection.GetMemberValue(option, "TextKey")?.ToString(),
                    StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(option, "IsProceed")),
                    ResolveEventOptionLabel(option),
                    index,
                    out var fallbackKind);
                AddEventOptionIdFallbackNotice(notices, fallbackKind);
                var choiceId = Sts2EventRoomIds.ChoiceId(optionId, index);
                result.Add(new StateRunEventOptionSnapshot(
                    Id: choiceId,
                    Index: checked((uint)index),
                    TextKey: StateProjectionValues.NormalizeNullable(Sts2LiveIntrospection.GetMemberValue(option, "TextKey")?.ToString()),
                    TitleLoc: StateProjectionValues.ResolveLocRef(Sts2LiveIntrospection.GetMemberValue(option, "Title"), notices, "run.currentRoom.event.playerStates[].options[].titleLoc"),
                    DescriptionLoc: StateProjectionValues.ResolveLocRef(Sts2LiveIntrospection.GetMemberValue(option, "Description"), notices, "run.currentRoom.event.playerStates[].options[].descriptionLoc"),
                    TitleText: ResolveEventOptionText(Sts2LiveIntrospection.GetMemberValue(option, "Title")),
                    DescriptionText: ResolveEventOptionText(Sts2LiveIntrospection.GetMemberValue(option, "Description")),
                    IsLocked: StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(option, "IsLocked")),
                    IsProceed: StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(option, "IsProceed")),
                    WasChosen: StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(option, "WasChosen")),
                    RelicId: StateProjectionValues.ResolveModelId(Sts2LiveIntrospection.GetMemberValue(option, "Relic")),
                    ShouldSaveChoiceToHistory: StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(option, "ShouldSaveChoiceToHistory")),
                    ShouldSaveVariablesToHistory: StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(option, "ShouldSaveVariablesToHistory")),
                    HoverTips: ResolveEventOptionHoverTips(option),
                    // The card this option previews (a CardHoverTip in its HoverTips, e.g. an
                    // option that adds a card) — drives the focus card preview. Null otherwise.
                    PreviewedCard: Sts2HoverTipProjection.TryResolvePreviewedCard(
                        Sts2LiveIntrospection.GetMemberValue(option, "HoverTips"), $"card:{choiceId}:preview")));
            }
            catch (Exception ex)
            {
                notices.Add(StateProjectionValues.PartialNotice("run.currentRoom.event.playerStates[].options", "state-event-option-unavailable", $"Event option {index} could not be read: {ex.Message}"));
            }
        }

        return result;
    }

    // The option's hover-tip stack (EventOption.HoverTips, e.g. a relic option's
    // HoverTipsExcludingRelic — the relic's keyword/power tips). Same resolved-
    // up-front projection as the potion/relic model tips: final strings plus the
    // power icon and debuff flag (Sts2HoverTipProjection).
    private static IReadOnlyList<ModelHoverTipSnapshot> ResolveEventOptionHoverTips(object option)
    {
        try
        {
            return Sts2HoverTipProjection.ExtractResolvedTips(Sts2LiveIntrospection.GetMemberValue(option, "HoverTips"));
        }
        catch
        {
            return Array.Empty<ModelHoverTipSnapshot>();
        }
    }

    private static string? ResolveEventOptionLabel(object option)
        => ResolveEventOptionText(Sts2LiveIntrospection.GetMemberValue(option, "Title"))
            ?? ResolveEventOptionText(Sts2LiveIntrospection.GetMemberValue(option, "Description"))
            ?? ResolveEventOptionText(Sts2LiveIntrospection.GetMemberValue(option, "HistoryName"));

    private static string? ResolveEventOptionText(object? value)
        => StateProjectionValues.NormalizeNullable(TryInvokeText(value, "GetFormattedText"))
            ?? StateProjectionValues.NormalizeNullable(TryInvokeText(value, "GetRawText"))
            ?? StateProjectionValues.NormalizeNullable(Sts2LiveIntrospection.GetMemberValue(value, "Text")?.ToString())
            ?? StateProjectionValues.NormalizeNullable(value is string ? value.ToString() : null);

    private static string? TryInvokeText(object? value, string methodName)
    {
        try
        {
            return Sts2LiveIntrospection.InvokeMethod(value, methodName)?.ToString();
        }
        catch (TargetInvocationException exception) when (IsLocalizationFailure(exception))
        {
            return null;
        }
    }

    private static bool IsLocalizationFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var type = current.GetType();
            if (string.Equals(type.Name, "LocException", StringComparison.Ordinal)
                || string.Equals(type.FullName, "MegaCrit.Sts2.Core.Localization.LocException", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddEventOptionIdFallbackNotice(
        ICollection<StateNoticeSnapshot> notices,
        Sts2EventRoomIds.OptionIdFallbackKind fallbackKind)
    {
        if (fallbackKind == Sts2EventRoomIds.OptionIdFallbackKind.Label)
        {
            Sts2StateNotice.AddPartialOnce(
                notices,
                "state-event-option-id-label-fallback",
                "Event option ids fell back to localized option text because the live option did not expose a stable text key.",
                "run.currentRoom.event.playerStates[].options[].id",
                nameof(Sts2StateProvider));
            return;
        }

        if (fallbackKind == Sts2EventRoomIds.OptionIdFallbackKind.Order)
        {
            Sts2StateNotice.AddPartialOnce(
                notices,
                "state-event-option-id-order-fallback",
                "Event option ids fell back to screen ordering because the live option did not expose a stable key or label.",
                "run.currentRoom.event.playerStates[].options[].id",
                nameof(Sts2StateProvider));
        }
    }

    private static StateRunEventAncientSnapshot? ResolveAncientEvent(
        object playerEvent,
        ICollection<StateNoticeSnapshot> notices)
    {
        var healedAmount = Sts2LiveIntrospection.GetMemberValue(playerEvent, "HealedAmount");
        var visibleDialogue = ResolveAncientVisibleDialogue(notices);
        if (healedAmount is null && visibleDialogue is null)
        {
            return null;
        }

        return new StateRunEventAncientSnapshot(
            HealedAmount: StateProjectionValues.ToInt32(healedAmount),
            View: visibleDialogue is null ? null : new StateRunEventAncientViewSnapshot(visibleDialogue));
    }

    private static StateRunEventAncientVisibleDialogueSnapshot? ResolveAncientVisibleDialogue(ICollection<StateNoticeSnapshot> notices)
    {
        try
        {
            var eventRoom = Sts2EventRoomScreenInspector.ResolveActiveRoom();
            var layout = Sts2LiveIntrospection.GetMemberValue(eventRoom, "Layout");
            if (layout is null || !(layout.GetType().FullName?.Contains("NAncientEventLayout", StringComparison.Ordinal) ?? false))
            {
                return null;
            }

            var currentLineIndex = StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(layout, "_currentDialogueLine"));
            var lineLocKeys = StateProjectionValues.EnumerateCollection(Sts2LiveIntrospection.GetMemberValue(layout, "_dialogue"))
                .Select(line => StateProjectionValues.ResolveLocRef(Sts2LiveIntrospection.GetMemberValue(line, "LineText"), notices, "run.currentRoom.event.playerStates[].ancient.view.visibleDialogue.lineLocKeys")?.Key ?? string.Empty)
                .ToArray();
            var currentLineLocKey = currentLineIndex >= 0 && currentLineIndex < lineLocKeys.Length
                ? StateProjectionValues.NormalizeNullable(lineLocKeys[currentLineIndex])
                : null;

            return new StateRunEventAncientVisibleDialogueSnapshot(
                SourceType: layout.GetType().FullName ?? layout.GetType().Name,
                DialogueId: ResolveAncientDialogueId(currentLineLocKey ?? lineLocKeys.FirstOrDefault(key => !string.IsNullOrWhiteSpace(key))),
                CurrentLineIndex: currentLineIndex,
                CurrentLineLocKey: currentLineLocKey,
                LineLocKeys: lineLocKeys);
        }
        catch (Exception ex)
        {
            notices.Add(StateProjectionValues.PartialNotice("run.currentRoom.event.playerStates[].ancient.view.visibleDialogue", "state-ancient-dialogue-unavailable", $"The visible ancient dialogue reference could not be read: {ex.Message}"));
            return null;
        }
    }

    private static string? ResolveAncientDialogueId(string? locKey)
    {
        var key = StateProjectionValues.NormalizeNullable(locKey);
        if (key is null)
        {
            return null;
        }

        var roleSeparator = key.LastIndexOf('.');
        if (roleSeparator > 0)
        {
            key = key[..roleSeparator];
        }

        var lineSeparator = key.LastIndexOf('-');
        return lineSeparator > 0 ? key[..lineSeparator] : key;
    }

    internal static StateRunTreasureRoomSnapshot ResolveTreasureRoom(
        RunState runState,
        RunManager manager,
        ICollection<StateNoticeSnapshot> notices)
    {
        var treasureNotices = new List<StateNoticeSnapshot>();
        try
        {
            // Whether the chest is opened (relics revealed) and whether proceed is
            // available are observable only from the live UI: a closed chest shows an
            // open-chest control, and relic/proceed controls appear once it is opened.
            // Resolve the live treasure-room UI node (which owns those buttons, unlike
            // the logical room) and reuse the screen inspector's choice resolution,
            // matching the fixture loader's canProceed check exactly. The open-chest
            // control is the canonical "closed" signal (relic holders can stay stale-
            // visible after a re-load), while proceed is the canonical "opened" signal
            // (it appears alongside the relics even while the open-chest button is still
            // fading out). So the chest is opened when proceed is offered, or a relic is
            // takeable and the open-chest control is gone.
            var screenObject = Sts2LiveIntrospection.ResolveCurrentScreenObject();
            var roomObject = Sts2TreasureRoomScreenInspector.ResolveActiveRoom(screenObject);
            var choices = Sts2TreasureRoomScreenInspector.ResolveChoices(roomObject, screenObject, null);
            // Require the open-chest control to be executable: in multiplayer the
            // button lingers visible-but-disabled after opening, so a presence-only
            // check would keep reporting the chest closed (hiding relics and votes).
            var canOpenChest = choices.Any(choice =>
                choice.Kind == TreasureRoomChoiceKind.OpenChest && choice.IsExecutable);
            var canTakeRelic = choices.Any(choice => choice.Kind == TreasureRoomChoiceKind.Relic);
            var canProceed = choices.Any(choice =>
                choice.Kind == TreasureRoomChoiceKind.Proceed && choice.IsExecutable);
            var chestOpened = canProceed || (canTakeRelic && !canOpenChest);

            var synchronizer = Sts2LiveIntrospection.GetMemberValue(manager, "TreasureRoomRelicSynchronizer");
            if (synchronizer is null)
            {
                treasureNotices.Add(StateProjectionValues.PartialNotice("run.currentRoom.treasure", "state-treasure-synchronizer-unavailable", "The current treasure room did not expose RunManager.TreasureRoomRelicSynchronizer."));
                return new StateRunTreasureRoomSnapshot(chestOpened, [], [], treasureNotices, canProceed);
            }

            var currentRelicsObject = Sts2LiveIntrospection.GetMemberValue(synchronizer, "CurrentRelics");
            var currentRelics = StateProjectionValues.EnumerateCollection(currentRelicsObject)
                .Select((relic, index) =>
                {
                    var modelId = StateProjectionValues.ResolveModelId(relic) ?? StateProjectionValues.Slug(relic?.GetType().Name);
                    return new StateTreasureRelicSnapshot($"treasure-relic:{index}:{modelId}", modelId);
                })
                .ToArray();
            if (!chestOpened)
            {
                // Chest still closed: relics stay hidden until it is opened.
                return new StateRunTreasureRoomSnapshot(false, currentRelics, [], treasureNotices, canProceed);
            }

            var players = StateProjectionValues.EnumerateCollection(Sts2LiveIntrospection.GetMemberValue(runState, "Players")).ToArray();
            var votes = new List<StateTreasurePlayerVoteSnapshot>();
            for (var index = 0; index < players.Length; index++)
            {
                var player = players[index];
                if (player is null)
                {
                    continue;
                }

                var playerId = StateProjectionValues.ResolveRunPlayerId(player, index);
                var vote = Sts2LiveIntrospection.InvokeMethod(synchronizer, "GetPlayerVote", player);
                votes.Add(new StateTreasurePlayerVoteSnapshot(
                    playerId,
                    MapStateBuilder.ToNullableInt32(Sts2LiveIntrospection.GetMemberValue(vote, "index")),
                    StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(vote, "voteReceived"))));
            }

            return new StateRunTreasureRoomSnapshot(chestOpened, currentRelics, votes, treasureNotices, canProceed);
        }
        catch (Exception ex)
        {
            notices.Add(StateProjectionValues.PartialNotice("run.currentRoom.treasure", "state-treasure-room-unavailable", $"The current treasure room state could not be read: {ex.Message}"));
            return new StateRunTreasureRoomSnapshot(false, [], [], treasureNotices);
        }
    }

    internal static StateRunShopRoomSnapshot ResolveShopRoom(
        object currentRoom,
        ICollection<StateNoticeSnapshot> notices)
    {
        var shopNotices = new List<StateNoticeSnapshot>();
        try
        {
            var inventory = Sts2LiveIntrospection.InvokeMethod(currentRoom, "GetLocalInventory");
            if (inventory is null)
            {
                shopNotices.Add(StateProjectionValues.PartialNotice("run.currentRoom.shop.inventory", "state-shop-inventory-unavailable", "The current shop room did not expose MerchantRoom.GetLocalInventory()."));
                return new StateRunShopRoomSnapshot(null, shopNotices, new StateShopViewSnapshot(false));
            }

            return new StateRunShopRoomSnapshot(ResolveShopInventory(inventory, shopNotices), shopNotices, ResolveShopView(shopNotices));
        }
        catch (Exception ex)
        {
            notices.Add(StateProjectionValues.PartialNotice("run.currentRoom.shop", "state-shop-room-unavailable", $"The current shop room state could not be read: {ex.Message}"));
            return new StateRunShopRoomSnapshot(null, shopNotices, new StateShopViewSnapshot(false));
        }
    }

    private static StateShopViewSnapshot ResolveShopView(
        ICollection<StateNoticeSnapshot> notices)
    {
        // The wares-open flag lives on the live NMerchantInventory screen node, not on the
        // MerchantInventory data entity exposed by MerchantRoom.GetLocalInventory() (which has no IsOpen).
        var screen = Sts2ShopScreenInspector.ResolveShopScreenObject();
        var isOpen = Sts2ShopScreenInspector.TryResolveIsOpen(screen);
        if (screen is null || isOpen is null)
        {
            notices.Add(StateProjectionValues.PartialNotice(
                "run.currentRoom.shop.view.isOpen",
                "state-shop-view-is-open-unavailable",
                "The current shop room did not expose a live NMerchantInventory screen node with IsOpen."));
            return new StateShopViewSnapshot(false);
        }

        return new StateShopViewSnapshot(isOpen.Value);
    }

    private static StateShopInventorySnapshot ResolveShopInventory(
        object inventory,
        ICollection<StateNoticeSnapshot> notices)
    {
        var player = Sts2LiveIntrospection.GetMemberValue(inventory, "Player");
        return new StateShopInventorySnapshot(
            SourceType: inventory.GetType().FullName ?? inventory.GetType().Name,
            PlayerId: player is null ? null : StateProjectionValues.ResolveRunPlayerId(player, 0),
            CharacterCardEntries: ResolveShopCardEntries(Sts2LiveIntrospection.GetMemberValue(inventory, "CharacterCardEntries"), Sts2ShopIds.CharacterCardCollection, notices),
            ColorlessCardEntries: ResolveShopCardEntries(Sts2LiveIntrospection.GetMemberValue(inventory, "ColorlessCardEntries"), Sts2ShopIds.ColorlessCardCollection, notices),
            RelicEntries: ResolveShopRelicEntries(Sts2LiveIntrospection.GetMemberValue(inventory, "RelicEntries"), Sts2ShopIds.RelicCollection, notices),
            PotionEntries: ResolveShopPotionEntries(Sts2LiveIntrospection.GetMemberValue(inventory, "PotionEntries"), Sts2ShopIds.PotionCollection, notices),
            CardRemovalEntry: ResolveShopCardRemovalEntry(Sts2LiveIntrospection.GetMemberValue(inventory, "CardRemovalEntry"), notices));
    }

    private static IReadOnlyList<StateShopCardEntrySnapshot> ResolveShopCardEntries(
        object? entries,
        string collection,
        ICollection<StateNoticeSnapshot> notices)
        => StateProjectionValues.EnumerateCollection(entries)
            .Select((entry, index) => ResolveShopCardEntry(entry, Sts2ShopIds.InventoryEntryId(collection, index), notices))
            .Where(entry => entry is not null)
            .Cast<StateShopCardEntrySnapshot>()
            .ToArray();

    private static StateShopCardEntrySnapshot? ResolveShopCardEntry(
        object? entry,
        string id,
        ICollection<StateNoticeSnapshot> notices)
    {
        if (entry is null)
        {
            return null;
        }

        try
        {
            var card = Sts2LiveIntrospection.GetMemberValue(Sts2LiveIntrospection.GetMemberValue(entry, "CreationResult"), "Card");
            return new StateShopCardEntrySnapshot(
                Id: id,
                SourceType: entry.GetType().FullName ?? entry.GetType().Name,
                Cost: card is null ? null : MapStateBuilder.ToNullableInt32(Sts2LiveIntrospection.GetMemberValue(entry, "Cost")),
                IsOnSale: StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(entry, "IsOnSale")),
                Card: card is null
                    ? null
                    : Sts2CardStateSnapshotFactory.CreateFallback(card, $"{id}:card"));
        }
        catch (Exception ex)
        {
            notices.Add(StateProjectionValues.PartialNotice("run.currentRoom.shop.inventory", "state-shop-card-entry-unavailable", $"A shop card entry could not be read: {ex.Message}"));
            return null;
        }
    }

    private static IReadOnlyList<StateShopRelicEntrySnapshot> ResolveShopRelicEntries(
        object? entries,
        string collection,
        ICollection<StateNoticeSnapshot> notices)
        => StateProjectionValues.EnumerateCollection(entries)
            .Select((entry, index) => ResolveShopRelicEntry(entry, Sts2ShopIds.InventoryEntryId(collection, index), notices))
            .Where(entry => entry is not null)
            .Cast<StateShopRelicEntrySnapshot>()
            .ToArray();

    private static StateShopRelicEntrySnapshot? ResolveShopRelicEntry(
        object? entry,
        string id,
        ICollection<StateNoticeSnapshot> notices)
    {
        if (entry is null)
        {
            return null;
        }

        try
        {
            var model = Sts2LiveIntrospection.GetMemberValue(entry, "Model");
            return new StateShopRelicEntrySnapshot(
                id,
                entry.GetType().FullName ?? entry.GetType().Name,
                model is null ? null : MapStateBuilder.ToNullableInt32(Sts2LiveIntrospection.GetMemberValue(entry, "Cost")),
                StateProjectionValues.ResolveModelId(model));
        }
        catch (Exception ex)
        {
            notices.Add(StateProjectionValues.PartialNotice("run.currentRoom.shop.inventory", "state-shop-relic-entry-unavailable", $"A shop relic entry could not be read: {ex.Message}"));
            return null;
        }
    }

    private static IReadOnlyList<StateShopPotionEntrySnapshot> ResolveShopPotionEntries(
        object? entries,
        string collection,
        ICollection<StateNoticeSnapshot> notices)
        => StateProjectionValues.EnumerateCollection(entries)
            .Select((entry, index) => ResolveShopPotionEntry(entry, Sts2ShopIds.InventoryEntryId(collection, index), notices))
            .Where(entry => entry is not null)
            .Cast<StateShopPotionEntrySnapshot>()
            .ToArray();

    private static StateShopPotionEntrySnapshot? ResolveShopPotionEntry(
        object? entry,
        string id,
        ICollection<StateNoticeSnapshot> notices)
    {
        if (entry is null)
        {
            return null;
        }

        try
        {
            var model = Sts2LiveIntrospection.GetMemberValue(entry, "Model");
            return new StateShopPotionEntrySnapshot(
                id,
                entry.GetType().FullName ?? entry.GetType().Name,
                model is null ? null : MapStateBuilder.ToNullableInt32(Sts2LiveIntrospection.GetMemberValue(entry, "Cost")),
                StateProjectionValues.ResolveModelId(model));
        }
        catch (Exception ex)
        {
            notices.Add(StateProjectionValues.PartialNotice("run.currentRoom.shop.inventory", "state-shop-potion-entry-unavailable", $"A shop potion entry could not be read: {ex.Message}"));
            return null;
        }
    }

    private static StateShopCardRemovalEntrySnapshot? ResolveShopCardRemovalEntry(
        object? entry,
        ICollection<StateNoticeSnapshot> notices)
    {
        if (entry is null)
        {
            return null;
        }

        try
        {
            return new StateShopCardRemovalEntrySnapshot(
                Sts2ShopIds.CardRemovalEntryIdValue,
                entry.GetType().FullName ?? entry.GetType().Name,
                MapStateBuilder.ToNullableInt32(Sts2LiveIntrospection.GetMemberValue(entry, "Cost")),
                StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(entry, "Used")));
        }
        catch (Exception ex)
        {
            notices.Add(StateProjectionValues.PartialNotice("run.currentRoom.shop.inventory.cardRemovalEntry", "state-shop-card-removal-entry-unavailable", $"The shop card removal entry could not be read: {ex.Message}"));
            return null;
        }
    }

    internal static StateRunRestSiteRoomSnapshot ResolveRestSiteRoom(
        RunState runState,
        RunManager manager,
        ICollection<StateNoticeSnapshot> notices)
    {
        var restSiteNotices = new List<StateNoticeSnapshot>();
        try
        {
            var synchronizer = Sts2LiveIntrospection.GetMemberValue(manager, "RestSiteSynchronizer");
            if (synchronizer is null)
            {
                restSiteNotices.Add(StateProjectionValues.PartialNotice("run.currentRoom.restSite", "state-rest-site-synchronizer-unavailable", "The current rest site room did not expose RunManager.RestSiteSynchronizer."));
                return new StateRunRestSiteRoomSnapshot([], restSiteNotices, new StateRestSiteRoomViewSnapshot(false));
            }

            var players = StateProjectionValues.EnumerateCollection(Sts2LiveIntrospection.GetMemberValue(runState, "Players")).ToArray();
            var playerStates = new List<StateRestSitePlayerStateSnapshot>();
            for (var index = 0; index < players.Length; index++)
            {
                var player = players[index];
                if (player is null)
                {
                    continue;
                }

                var playerId = StateProjectionValues.ResolveRunPlayerId(player, index);
                var netId = Sts2LiveIntrospection.GetMemberValue(player, "NetId");
                playerStates.Add(new StateRestSitePlayerStateSnapshot(
                    playerId,
                    MapStateBuilder.ToNullableInt32(Sts2LiveIntrospection.InvokeMethod(synchronizer, "GetHoveredOptionIndex", netId)),
                    MapStateBuilder.ToNullableInt32(Sts2LiveIntrospection.InvokeMethod(synchronizer, "GetChosenOptionIndex", netId)),
                    ResolveRestSiteOptions(playerId, Sts2LiveIntrospection.InvokeMethod(synchronizer, "GetOptionsForPlayer", player), restSiteNotices)));
            }

            return new StateRunRestSiteRoomSnapshot(
                playerStates,
                restSiteNotices,
                new StateRestSiteRoomViewSnapshot(ResolveRestSiteCanProceed()));
        }
        catch (Exception ex)
        {
            notices.Add(StateProjectionValues.PartialNotice("run.currentRoom.restSite", "state-rest-site-room-unavailable", $"The current rest site room state could not be read: {ex.Message}"));
            return new StateRunRestSiteRoomSnapshot([], restSiteNotices, new StateRestSiteRoomViewSnapshot(false));
        }
    }

    // Whether the player may leave the rest site without choosing an option. This is
    // observable only from the live UI: the proceed/continue control is offered (visible
    // + executable) just when proceed is allowed; the screen inspector's flow choice
    // mirrors the live ProceedButton.Visible && IsEnabled state exactly. Mirrors the
    // treasure room's canProceed (ResolveTreasureRoom). Any failure resolves to false so
    // the render hides the ribbon by default.
    private static bool ResolveRestSiteCanProceed()
    {
        try
        {
            var roomObject = Sts2LiveIntrospection.ResolveCurrentScreenObject();
            if (roomObject is null
                || !Sts2LiveIntrospection.IsType(roomObject, Sts2RestSiteScreenInspector.RestSiteRoomType))
            {
                return false;
            }

            return Sts2RestSiteScreenInspector
                .ResolveChoices(roomObject, defaultPlayerId: null)
                .Any(choice => choice.IsFlowChoice && choice.IsExecutable);
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<StateRestSiteOptionSnapshot> ResolveRestSiteOptions(
        string playerId,
        object? options,
        ICollection<StateNoticeSnapshot> notices)
        => StateProjectionValues.EnumerateCollection(options)
            .Select((option, index) => ResolveRestSiteOption(option, $"rest-site-option:{playerId}:{index}", notices))
            .Where(option => option is not null)
            .Cast<StateRestSiteOptionSnapshot>()
            .ToArray();

    private static StateRestSiteOptionSnapshot? ResolveRestSiteOption(
        object? option,
        string idPrefix,
        ICollection<StateNoticeSnapshot> notices)
    {
        if (option is null)
        {
            return null;
        }

        try
        {
            var optionId = StateProjectionValues.NormalizeNullable(Sts2LiveIntrospection.GetMemberValue(option, "OptionId")?.ToString()) ?? StateProjectionValues.Slug(option.GetType().Name);
            var isEnabled = StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(option, "IsEnabled"));
            var requiresConfirmation = StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(option, "RequiresConfirmation"))
                || StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(option, "RequireConfirmation"));
            // Mirror Sts2RestSiteScreenInspector: isExecutable = isEnabled && !requiresConfirmation.
            // Surface the cause so consumers can distinguish a hard-disabled option from one that
            // is merely awaiting confirmation; both are non-derivable from is_enabled alone.
            var disabledReason = !isEnabled ? "not-enabled" : requiresConfirmation ? "confirmation-required" : null;
            return new StateRestSiteOptionSnapshot(
                $"{idPrefix}:{optionId}",
                option.GetType().FullName ?? option.GetType().Name,
                optionId,
                isEnabled,
                MapStateBuilder.ToNullableInt32(Sts2LiveIntrospection.GetMemberValue(option, "SmithCount")),
                ResolveLiftsLeft(optionId, option),
                disabledReason);
        }
        catch (Exception ex)
        {
            notices.Add(StateProjectionValues.PartialNotice("run.currentRoom.restSite.playerStates[].options", "state-rest-site-option-unavailable", $"A rest site option could not be read: {ex.Message}"));
            return null;
        }
    }

    // LIFT only: remaining lifts = MaxLifts(3) - Girya.TimesLifted (mirrors
    // LiftRestSiteOption.Description's "LiftsLeft" var). Read from the option owner's
    // Girya relic instance; null for non-LIFT options or if the relic is absent.
    private const int RestSiteMaxLifts = 3;

    private static int? ResolveLiftsLeft(string optionId, object option)
    {
        if (!string.Equals(optionId, "LIFT", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var owner = Sts2LiveIntrospection.GetMemberValue(option, "Owner");
        var girya = StateProjectionValues.EnumerateCollection(Sts2LiveIntrospection.GetMemberValue(owner, "Relics"))
            .FirstOrDefault(relic => string.Equals(relic?.GetType().Name, "Girya", StringComparison.Ordinal));
        if (girya is null)
        {
            return null;
        }

        var timesLifted = StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(girya, "TimesLifted"));
        return Math.Max(0, RestSiteMaxLifts - timesLifted);
    }

    private static IReadOnlyList<StateRunEventSharedVoteSnapshot> ResolveSharedVotes(
        RunState runState,
        object? synchronizer,
        ICollection<StateNoticeSnapshot> notices)
    {
        if (synchronizer is null)
        {
            return [];
        }

        var players = StateProjectionValues.EnumerateCollection(Sts2LiveIntrospection.GetMemberValue(runState, "Players")).ToArray();
        var result = new List<StateRunEventSharedVoteSnapshot>();
        for (var index = 0; index < players.Length; index++)
        {
            var player = players[index];
            if (player is null)
            {
                continue;
            }

            var playerId = StateProjectionValues.ResolveRunPlayerId(player, index);
            try
            {
                var vote = Sts2LiveIntrospection.InvokeMethod(synchronizer, "GetPlayerVote", player);
                var optionIndex = ResolveVoteOptionIndex(vote);
                result.Add(new StateRunEventSharedVoteSnapshot(
                    PlayerId: playerId,
                    HasOptionIndex: optionIndex.HasValue,
                    OptionIndex: optionIndex ?? 0));
            }
            catch (Exception ex)
            {
                notices.Add(StateProjectionValues.PartialNotice("run.currentRoom.event.sharedVotes", "state-event-vote-unavailable", $"The shared event vote for player {playerId} could not be read: {ex.Message}"));
            }
        }

        return result;
    }

    private static uint? ResolveVoteOptionIndex(object? vote)
        => vote switch
        {
            null => null,
            int typed when typed >= 0 => (uint)typed,
            uint typed => typed,
            long typed when typed >= 0 => checked((uint)typed),
            ulong typed => checked((uint)typed),
            _ => StateProjectionValues.ToUInt64(Sts2LiveIntrospection.GetMemberValue(vote, "OptionIndex")
                ?? Sts2LiveIntrospection.GetMemberValue(vote, "Index")) is { } index ? checked((uint)index) : null,
        };

    // The host NetHostGameService is the only net service that knows which ENet peers are currently connected.
    // Mirrors Sts2ActionHandler.ExecuteDisconnectClient's resolution (StartRunLobby.NetService at character-select
    // time, RunManager.Instance.NetService once a run starts) — with the order flipped for the RUN path: this runs
    // only when RunManager.IsInProgress is already true, so the run's own net service (the caller already holds
    // RunManager.Instance.NetService) is authoritative, and the lobby is consulted ONLY if it is missing. That
    // keeps the per-snapshot hot path off the screen-tree walk while still covering both sources.
}
