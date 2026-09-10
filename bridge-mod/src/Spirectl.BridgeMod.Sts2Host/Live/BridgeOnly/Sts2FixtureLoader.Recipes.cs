using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;
using MegaCrit.Sts2.Core.Saves.Runs;
using Spirectl.Sts2.Core.Fixtures;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2;

namespace Spirectl.Sts2.Live;


public sealed partial class Sts2FixtureLoader
{
    private static bool IsSelectionFixtureScreen(string screen)
        => Sts2SupportedScreenIds.IsCardSelectionFamily(screen);

    private static bool IsOverlayFixtureScreen(string screen)
        => string.Equals(screen, "card-overlay", StringComparison.Ordinal)
            || string.Equals(screen, "passive-card-overlay", StringComparison.Ordinal);

    private static FixtureRecipeRestoreReport BuildRecipeReport(
        FixtureDocument fixture,
        IReadOnlyList<FixtureRecipeFieldReport> appliedFields,
        IReadOnlyList<FixtureRecipeFieldReport> inferredFields,
        IReadOnlyList<FixtureRecipeFieldReport> unsupportedFields,
        IReadOnlyList<FixtureRecipeFieldReport> degradedMultiplayerFields,
        FixtureBridgeValidationResult bridgeValidation)
    {
        var applied = new List<FixtureRecipeFieldReport>
        {
            Report("screen", fixture.Screen, "applied-authored-field", "Selected the live fixture recipe family."),
        };
        applied.AddRange(appliedFields);

        var inferred = new List<FixtureRecipeFieldReport>
        {
            Report("run.seed", fixture.Run.Seed, "inferred-runtime-seed", "Used as deterministic run setup input where the live STS2 API accepts it."),
            Report("run.view.playerId", fixture.Perspective.PlayerId, "bridge-owner-perspective", "Resolved fixture load through the local bridge owner perspective."),
        };
        inferred.AddRange(inferredFields);

        var omitted = new List<FixtureRecipeFieldReport>();
        if (fixture.Room is not null && string.IsNullOrWhiteSpace(fixture.Room.EncounterId))
        {
            omitted.Add(Report("run.currentRoom.combat.encounterId", string.Empty, "omitted-not-applicable", "This recipe family does not author a combat encounter room."));
        }

        return new FixtureRecipeRestoreReport(
            RecipeName: $"{fixture.Screen}-recipe",
            AppliedFields: applied,
            InferredFields: inferred,
            OmittedFields: omitted,
            UnsupportedFields: unsupportedFields,
            DegradedMultiplayerFields: degradedMultiplayerFields,
            BridgeValidation: bridgeValidation);
    }

    private static FixtureRecipeFieldReport Report(string fieldPath, string? valueSummary, string reasonCode, string message)
        => new(fieldPath, valueSummary ?? string.Empty, reasonCode, message);

    private static FixtureBridgeValidationResult BuildBridgeValidation(string status, string fieldPath, string valueSummary)
        => new(
            status,
            [Report(fieldPath, valueSummary, status == "passed" ? "bridge-validated" : "bridge-validated-partial", "The live bridge checked the realized screen id after recipe setup.")]);

    private static IReadOnlyList<FixtureRecipeFieldReport> AppliedShopFields(FixtureShop? shop)
    {
        if (shop is null)
        {
            return [];
        }

        var reports = new List<FixtureRecipeFieldReport>();
        if (shop.Gold is int gold)
        {
            reports.Add(Report("run.players[].gold", gold.ToString(), "applied-authored-field", "Applied player gold before entering the merchant screen."));
        }

        reports.AddRange(shop.CardIds.Select((id, index) => Report($"run.currentRoom.shop.inventory.characterCardEntries[{index}].card.modelId", id, "bridge-validated-card-id", "Resolved authored shop card id through the installed STS2 model database.")));
        reports.AddRange(shop.RelicIds.Select((id, index) => Report($"run.currentRoom.shop.inventory.relicEntries[{index}].modelId", id, "bridge-validated-relic-id", "Resolved authored shop relic id through the installed STS2 model database.")));
        reports.AddRange(shop.PotionIds.Select((id, index) => Report($"run.currentRoom.shop.inventory.potionEntries[{index}].modelId", id, "bridge-validated-potion-id", "Resolved authored shop potion id through the installed STS2 model database.")));
        if (shop.CardRemovalAvailable is bool cardRemovalAvailable)
        {
            reports.Add(Report("run.currentRoom.shop.inventory.cardRemovalEntry.used", (!cardRemovalAvailable).ToString().ToLowerInvariant(), "applied-authored-field", "Applied visible card-removal availability through the merchant inventory API."));
        }

        return reports;
    }

    internal static IReadOnlyList<FixtureRecipeFieldReport> InferredGeneratedShopFields(bool usesGeneratedInventory)
    {
        if (!usesGeneratedInventory)
        {
            return [];
        }

        return
        [
            Report(
                "run.currentRoom.shop.inventory",
                "generated",
                "inferred-runtime-shop-inventory",
                "Omitted shop recipe; the live merchant room generated inventory from run setup and seed."),
        ];
    }

    private static IReadOnlyList<FixtureRecipeFieldReport> AppliedSelectionFields(FixtureSelection? selection)
    {
        if (selection is null)
        {
            return [];
        }

        var reports = new List<FixtureRecipeFieldReport>();
        if (!string.IsNullOrWhiteSpace(selection.Kind))
        {
            reports.Add(Report("run.players[].overlays[].deckCardSelection.kind", selection.Kind!, "applied-authored-field", "Pushed the authored deck-card-selection screen variant onto the overlay stack."));
        }

        reports.AddRange(selection.Cards.Select((id, index) => Report($"run.players[].overlays[].cards[{index}].modelId", id, "bridge-validated-card-id", "Resolved authored selection card id through the installed STS2 model database.")));
        reports.AddRange(selection.Bundles.SelectMany((bundle, bundleIndex) =>
            bundle.CardIds.Select((id, cardIndex) => Report($"run.players[].overlays[].bundleSelection.bundles[{bundleIndex}].cardIds[{cardIndex}]", id, "bridge-validated-card-id", "Resolved authored bundle card id through the installed STS2 model database."))));
        if (selection.CanSkip is bool canSkip)
        {
            reports.Add(Report("run.players[].overlays[].canSkip", canSkip.ToString().ToLowerInvariant(), "applied-authored-field", "Applied visible skip/cancel affordance through selection screen setup."));
        }

        if (selection.CanConfirm is bool canConfirm)
        {
            reports.Add(Report("run.players[].overlays[].canConfirm", canConfirm.ToString().ToLowerInvariant(), "applied-authored-field", "Applied visible confirmation affordance through selection screen setup."));
        }

        return reports;
    }

    private static IReadOnlyList<FixtureRecipeFieldReport> AppliedCombatFields(
        FixtureDocument fixture,
        IReadOnlyList<HostLocalFixturePlayerRecipe> players,
        EncounterModel encounter)
    {
        var reports = new List<FixtureRecipeFieldReport>
        {
            Report("run.currentRoom.combat.encounterId", encounter.Id.Entry, "bridge-validated-encounter-id", "Resolved authored encounter id through the installed STS2 model database."),
        };
        if (fixture.Ui?.SelectedCard is { } selectedCard)
        {
            reports.Add(Report(
                "run.view.selectedCard",
                $"{selectedCard.PlayerId}:{selectedCard.CardModelId}",
                "applied-authored-field",
                "Pre-selected the authored hand card in spirectl view state."));
        }

        reports.AddRange(AppliedHostLocalPlayerFields(fixture, players));
        return reports;
    }

    private static IReadOnlyList<FixtureRecipeFieldReport> AppliedEventRoomFields(
        FixtureDocument fixture,
        IReadOnlyList<HostLocalFixturePlayerRecipe> players,
        EventModel eventModel,
        IReadOnlyList<FixtureRecipeFieldReport> ancientDialogueReports)
    {
        var reports = new List<FixtureRecipeFieldReport>
        {
            Report("run.currentRoom.event.canonicalEventModelId", eventModel.Id.Entry, "applied-authored-field", "Resolved authored event id through the installed STS2 model database."),
        };
        reports.AddRange(ancientDialogueReports);

        reports.AddRange(AppliedHostLocalPlayerFields(fixture, players));
        return reports;
    }

    private static IReadOnlyList<FixtureRecipeFieldReport> AppliedHostLocalPlayerFields(
        FixtureDocument fixture,
        IReadOnlyList<HostLocalFixturePlayerRecipe> players)
    {
        var reports = new List<FixtureRecipeFieldReport>();
        for (var index = 0; index < players.Count; index += 1)
        {
            var player = players[index];
            reports.Add(Report($"run.players[{index}].id", player.FixtureId, "applied-authored-field", "Applied authored stable player id as the runtime net id."));
            reports.Add(Report($"run.players[{index}].characterId", player.Character.Id.Entry, "bridge-validated-character-id", "Resolved authored player character through the installed STS2 model database."));
            if (fixture.Players[index].Hp is int hp)
            {
                reports.Add(Report($"run.players[{index}].creature.currentHp", hp.ToString(), "applied-authored-field", "Applied authored current hp to the created runtime player."));
            }

            if (fixture.Players[index].MaxHp is int maxHp)
            {
                reports.Add(Report($"run.players[{index}].creature.maxHp", maxHp.ToString(), "applied-authored-field", "Applied authored max hp to the created runtime player."));
            }

            if (fixture.Players[index].IsHostLocalSeat is bool isHostLocalSeat)
            {
                reports.Add(Report($"run.players[{index}].isHostLocalSeat", isHostLocalSeat.ToString().ToLowerInvariant(), "applied-authored-field", "Registered the authored player as a host-local combat seat."));
            }

            if (fixture.Players[index].IsLocal is bool isLocal)
            {
                reports.Add(Report($"run.players[{index}].isLocal", isLocal.ToString().ToLowerInvariant(), "applied-authored-field", "Preserved the authored local-control metadata in the recipe report."));
            }

            if (fixture.Players[index].SlotId is int slotId)
            {
                reports.Add(Report($"run.players[{index}].slotId", slotId.ToString(), "applied-authored-field", "Preserved the authored seat slot metadata in the recipe report."));
            }

            if (fixture.Players[index].PotionIds is { } potionIds)
            {
                reports.AddRange(potionIds.Select((id, potionIndex) => Report($"run.players[{index}].potions[{potionIndex}].modelId", id, "bridge-validated-potion-id", "Resolved authored player potion id through the installed STS2 model database.")));
            }
        }

        return reports;
    }

    private static IReadOnlyList<FixtureRecipeFieldReport> UnsupportedEventRoomFields(FixtureEventRoom? eventRoom)
    {
        var reports = new List<FixtureRecipeFieldReport>();
        reports.AddRange(UnsupportedJsonFields("run.currentRoom.event", eventRoom?.ExtraFields));
        return reports;
    }

    private static IReadOnlyList<FixtureRecipeFieldReport> AppliedTreasureFields(FixtureDocument fixture)
    {
        var reports = new List<FixtureRecipeFieldReport>();
        var playerVotes = fixture.TreasureRoom?.PlayerVotes ?? [];
        for (var index = 0; index < playerVotes.Count; index++)
        {
            var vote = playerVotes[index];
            var summary = vote.Index is int relicIndex ? $"{vote.PlayerId}:relic:{relicIndex}" : $"{vote.PlayerId}:skip";
            reports.Add(Report($"run.currentRoom.treasure.playerVotes[{index}]", summary, "applied-authored-field", "The authored treasure-room vote was cast on the live relic synchronizer."));
        }

        return reports;
    }

    private static IReadOnlyList<FixtureRecipeFieldReport> UnsupportedTreasureFields(FixtureDocument fixture)
    {
        var reports = new List<FixtureRecipeFieldReport>();
        if (fixture.TreasureRoom is not null)
        {
            reports.AddRange(UnsupportedJsonFields("run.currentRoom.treasure", fixture.TreasureRoom.ExtraFields));
        }

        return reports;
    }

    private static IReadOnlyList<FixtureRecipeFieldReport> UnsupportedJsonFields(
        string parentPath,
        IReadOnlyDictionary<string, JsonElement>? extraFields)
        => extraFields is null
            ? []
            : extraFields
                .Select(field => Report($"{parentPath}.{field.Key}", SummarizeJson(field.Value), "unsupported-hidden-or-derived-field", "The live recipe loader does not patch this authored field; it remains explicit in the recipe report."))
                .ToArray();

    private static string SummarizeJson(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
            JsonValueKind.Array => $"{element.GetArrayLength()} item(s)",
            JsonValueKind.Object => "object",
            JsonValueKind.Null => "null",
            _ => element.GetRawText(),
        };

    // Reads run.currentRoom.mapRoom.travelToRow from the raw map-room JSON (the fixture document serializes it
    // only when authored). Returns null when absent, leaving the loader on its default start-parked map.
    private static int? ReadMapRoomTravelToRow(JsonElement? mapRoom)
    {
        if (mapRoom is { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty("travelToRow", out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var row))
        {
            return row;
        }

        return null;
    }

    // Reads run.currentRoom.mapRoom.firstNode ("combat" | "ancient") from the raw map-room JSON.
    private static string? ReadMapRoomFirstNode(JsonElement? mapRoom)
        => mapRoom is { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty("firstNode", out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    // Reads run.currentRoom.mapRoom.travelPath (a string array of MapPointType / "Unknown:<RoomType>"
    // entries) from the raw map-room JSON. Null when absent/empty.
    private static List<string>? ReadMapRoomTravelPath(JsonElement? mapRoom)
    {
        if (mapRoom is not { ValueKind: JsonValueKind.Object } element
            || !element.TryGetProperty("travelPath", out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var path = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } entry)
            {
                path.Add(entry);
            }
        }

        return path.Count > 0 ? path : null;
    }

    private static IReadOnlyList<CardModel>? ResolveCardModels(
        FixtureLoadRequestSnapshot request,
        string field,
        IReadOnlyList<string> cardIds,
        bool allowEmpty,
        out FixtureLoadResult? invalid)
    {
        invalid = null;
        var cards = new List<CardModel>();
        foreach (var (cardId, index) in cardIds.Select((cardId, index) => (cardId, index)))
        {
            if (!Sts2ModelResolver.TryResolveFixtureCard(cardId, out var card))
            {
                invalid = InvalidFixture(
                    request,
                    $"{field}[{index}]",
                    cardId,
                    "Use a card id from the installed STS2 content.");
                return null;
            }

            cards.Add(card);
        }

        if (!allowEmpty && cards.Count == 0)
        {
            invalid = InvalidFixture(request, field, string.Empty, "Provide one or more ids for this fixture section.");
            return null;
        }

        return cards;
    }

    private static IReadOnlyList<RelicModel>? ResolveRelicModels(
        FixtureLoadRequestSnapshot request,
        string field,
        IReadOnlyList<string> relicIds,
        bool allowEmpty,
        out FixtureLoadResult? invalid)
    {
        invalid = null;
        var relics = new List<RelicModel>();
        foreach (var (relicId, index) in relicIds.Select((relicId, index) => (relicId, index)))
        {
            if (!Sts2ModelResolver.TryResolveFixtureRelic(relicId, out var relic))
            {
                invalid = InvalidFixture(
                    request,
                    $"{field}[{index}]",
                    relicId,
                    "Use a relic id from the installed STS2 content.");
                return null;
            }

            relics.Add(relic);
        }

        if (!allowEmpty && relics.Count == 0)
        {
            invalid = InvalidFixture(request, field, string.Empty, "Provide one or more ids for this fixture section.");
            return null;
        }

        return relics;
    }

    private static IReadOnlyList<OrbModel>? ResolveOrbModels(
        FixtureLoadRequestSnapshot request,
        string field,
        IReadOnlyList<string> orbIds,
        out FixtureLoadResult? invalid)
    {
        invalid = null;
        var orbs = new List<OrbModel>();
        foreach (var (orbId, index) in orbIds.Select((orbId, index) => (orbId, index)))
        {
            if (!Sts2ModelResolver.TryResolveFixtureOrb(orbId, out var orb))
            {
                invalid = InvalidFixture(
                    request,
                    $"{field}[{index}]",
                    orbId,
                    "Use an orb id from the installed STS2 content (e.g. LIGHTNING_ORB, FROST_ORB, DARK_ORB, PLASMA_ORB).");
                return null;
            }

            orbs.Add(orb);
        }

        return orbs;
    }

    private static IReadOnlyList<IReadOnlyList<CardModel>>? ResolveBundleModels(
        FixtureLoadRequestSnapshot request,
        IReadOnlyList<FixtureBundle> bundles,
        out FixtureLoadResult? invalid)
    {
        invalid = null;
        var resolved = new List<IReadOnlyList<CardModel>>();
        foreach (var (bundle, index) in bundles.Select((bundle, index) => (bundle, index)))
        {
            var bundleCards = ResolveCardModels(
                request,
                $"run.players[].overlays[].bundleSelection.bundles[{index}].cardIds",
                bundle.CardIds,
                allowEmpty: false,
                out invalid);
            if (invalid is not null)
            {
                return null;
            }

            resolved.Add(bundleCards!);
        }

        return resolved;
    }

    private static CardSelectorPrefs CreateSelectorPrefs(FixtureSelection selection)
    {
        // The prompt text is driven by prefs (NCardGridSelectionScreen sets the
        // bottom label to `_prefs.Prompt`), so match the loc key to the screen
        // variant: enchant -> "Choose a card to Enchant", upgrade/transform/remove
        // similarly (card_selection table). {Amount} renders from MaxSelect.
        var promptKey = SelectionPromptKey(selection.Screen);

        int minSelect;
        int maxSelect;
        if (string.Equals(selection.Screen, Sts2SupportedScreenIds.DeckEnchantSelectionScreenId, StringComparison.Ordinal))
        {
            // The enchant screen selects a fixed count (enchantAmount, single by
            // default) — faithful to the live FromDeckForEnchantment flow — and
            // uses single-selection mode when MaxSelect == 1, not the remove
            // flow's 0..2 confirm range.
            maxSelect = Math.Max(1, selection.EnchantAmount ?? 1);
            minSelect = selection.CanSkip == true ? 0 : maxSelect;
        }
        else
        {
            minSelect = selection.CanSkip == true ? 0 : 1;
            maxSelect = selection.CanConfirm == true ? Math.Max(minSelect, 2) : Math.Max(minSelect, 1);
        }

        return new CardSelectorPrefs(new MegaCrit.Sts2.Core.Localization.LocString("card_selection", promptKey), minSelect, maxSelect)
        {
            RequireManualConfirmation = selection.CanConfirm ?? false,
            Cancelable = selection.CanSkip ?? false,
        };
    }

    // The card_selection loc key for each deck-selection screen variant
    // (TO_ENCHANT/TO_UPGRADE/TO_TRANSFORM/TO_REMOVE). The base/remove and `select`
    // variants fall back to TO_REMOVE.
    private static string SelectionPromptKey(string? screenId)
    {
        if (string.Equals(screenId, Sts2SupportedScreenIds.DeckEnchantSelectionScreenId, StringComparison.Ordinal))
        {
            return "TO_ENCHANT";
        }

        if (string.Equals(screenId, Sts2SupportedScreenIds.DeckUpgradeSelectionScreenId, StringComparison.Ordinal))
        {
            return "TO_UPGRADE";
        }

        if (string.Equals(screenId, Sts2SupportedScreenIds.DeckTransformSelectionScreenId, StringComparison.Ordinal))
        {
            return "TO_TRANSFORM";
        }

        return "TO_REMOVE";
    }

    private static void ShowChooseACardSelection(IReadOnlyList<CardModel> cards, bool canSkip)
    {
        var screen = NChooseACardSelectionScreen.ShowScreen(cards, canSkip);
        if (screen is null)
        {
            return;
        }

        // The screen pops itself off the overlay stack inside CardsSelected()
        // (which the game's CardSelectCmd awaits). Hold that await here so a
        // select-card/skip against the fixture-shown screen closes it like the
        // live flow — there is just no card effect consuming the result.
        _ = screen.CardsSelected().ContinueWith(
            task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void ShowSimpleCardSelection(IReadOnlyList<CardModel> cards, CardSelectorPrefs prefs)
    {
        NOverlayStack.Instance!.Push(NSimpleCardSelectScreen.Create(cards, prefs));
    }

    // The player's own deck cards to show in a composed deck-card-selection overlay,
    // filtered to match the screen variant (upgrade->upgradeable, remove->removable,
    // transform/enchant->all). These are real owned card instances, so the overlay
    // can scope to the owner and compute the per-card preview.
    private static IReadOnlyList<CardModel> ResolveDeckCardsForScreen(Player player, string screenId)
    {
        var deck = Sts2LiveIntrospection.GetMemberValue(player, "MasterDeck")
            ?? Sts2LiveIntrospection.GetMemberValue(player, "Deck");
        var cards = Sts2LiveIntrospection.GetMemberValue(deck, "Cards") ?? deck;
        if (cards is not System.Collections.IEnumerable items)
        {
            return [];
        }

        Func<CardModel, bool> filter = screenId switch
        {
            Sts2SupportedScreenIds.DeckUpgradeSelectionScreenId =>
                card => Sts2LiveIntrospection.GetMemberValue(card, "IsUpgradable") is true,
            Sts2SupportedScreenIds.DeckCardSelectionScreenId =>
                card => Sts2LiveIntrospection.GetMemberValue(card, "IsRemovable") is true,
            _ => _ => true,
        };

        return items.OfType<CardModel>().Where(filter).ToList();
    }

    // Pushes the deck-card-selection screen variant the fixture asked for. The
    // upgrade (smith) screen owns its own push + needs the run state for the
    // upgrade preview; the remaining family members (base "remove", and the
    // transform/enchant variants, which require extra per-card arguments not
    // expressible in a fixture yet) fall back to the base deck screen.
    private static void ShowDeckCardSelection(
        string screenId,
        IReadOnlyList<CardModel> cards,
        CardSelectorPrefs prefs,
        RunState runState,
        EnchantmentModel? enchantment = null,
        int enchantAmount = 1)
    {
        switch (screenId)
        {
            case Sts2SupportedScreenIds.DeckUpgradeSelectionScreenId:
                NDeckUpgradeSelectScreen.ShowScreen(cards, prefs, runState);
                break;
            case Sts2SupportedScreenIds.DeckEnchantSelectionScreenId when enchantment is not null:
                NDeckEnchantSelectScreen.ShowScreen(cards, enchantment, enchantAmount, prefs);
                break;
            default:
                NOverlayStack.Instance!.Push(NDeckCardSelectScreen.Create(cards, prefs));
                break;
        }
    }

    // Resolves an authored enchantment id (e.g. SWIFT) to its model. ModelDb has no
    // by-id enchantment lookup, so match DebugEnchantments (every EnchantmentModel
    // subtype) on the normalized Id.Entry.
    private static EnchantmentModel? ResolveEnchantmentModel(string enchantmentId)
    {
        var normalized = enchantmentId.Trim().ToUpperInvariant();
        return ModelDb.DebugEnchantments.FirstOrDefault(enchantment =>
            string.Equals(
                Sts2LiveIntrospection.GetMemberValue(
                    Sts2LiveIntrospection.GetMemberValue(enchantment, "Id"), "Entry")?.ToString()?.ToUpperInvariant(),
                normalized,
                StringComparison.Ordinal));
    }

    // Composes the authored deck-card-selection overlay (smith/upgrade/enchant
    // dialog) on top of the current room — faithful to picking SMITH at a rest
    // site or the enchant/upgrade option at an event (e.g. Field of Man-Sized
    // Holes -> PerfectFit enchant). The overlay stack renders over the current
    // room and shows the player's own deck cards (the real path reads owner.Deck),
    // so the cards carry an owner for state scoping and a live preview. Returns a
    // non-null invalid result on failure; null on success or when no overlay is
    // authored. `composedContext` labels the room in the "no matching cards" error.
    private static FixtureLoadResult? ComposeDeckSelectionOverlay(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture,
        Player localPlayer,
        RunState runState,
        string composedContext)
    {
        if (fixture.Selection is null)
        {
            return null;
        }

        var overlayScreenId = fixture.Selection.Screen ?? Sts2SupportedScreenIds.DeckUpgradeSelectionScreenId;
        var overlayCards = ResolveDeckCardsForScreen(localPlayer, overlayScreenId);
        if (overlayCards.Count == 0)
        {
            return InvalidFixture(
                request,
                "run.players[].overlays[]",
                overlayScreenId,
                $"The composed {composedContext} deck overlay needs matching cards in the player's deck; author a deck/character that has some.");
        }

        EnchantmentModel? enchantment = null;
        if (string.Equals(overlayScreenId, Sts2SupportedScreenIds.DeckEnchantSelectionScreenId, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(fixture.Selection.EnchantmentId))
            {
                return InvalidFixture(request, "run.players[].overlays[].deckCardSelection.enchantmentId", string.Empty, "Provide deckCardSelection.enchantmentId for the enchant deck screen (e.g. SWIFT).");
            }

            enchantment = ResolveEnchantmentModel(fixture.Selection.EnchantmentId);
            if (enchantment is null)
            {
                return InvalidFixture(request, "run.players[].overlays[].deckCardSelection.enchantmentId", fixture.Selection.EnchantmentId, "Use an enchantment id from the installed STS2 content (e.g. SWIFT).");
            }
        }

        ShowDeckCardSelection(
            overlayScreenId,
            overlayCards,
            CreateSelectorPrefs(fixture.Selection),
            runState,
            enchantment,
            fixture.Selection.EnchantAmount ?? 1);
        return null;
    }

    private static void ShowBundleSelection(IReadOnlyList<IReadOnlyList<CardModel>> bundles)
    {
        NChooseABundleSelectionScreen.ShowScreen(bundles);
    }

    private static void ShowRelicSelection(IReadOnlyList<RelicModel> relics)
    {
        NChooseARelicSelection.ShowScreen(relics);
    }

    private bool TryResolveHostLocalPlayers(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture,
        string screenLabel,
        out IReadOnlyList<HostLocalFixturePlayerRecipe>? players,
        out FixtureLoadResult? invalid)
    {
        players = null;
        invalid = null;
        if (fixture.Players.Count == 0)
        {
            invalid = InvalidFixture(
                request,
                "run.players",
                "0",
                $"Provide at least one authored {screenLabel} player.");
            return false;
        }

        if (ValidateHostLocalPlayerOwnership(request, fixture, screenLabel) is FixtureLoadResult ownershipInvalid)
        {
            invalid = ownershipInvalid;
            return false;
        }

        var playerNetIds = new HashSet<ulong>();
        var resolved = new List<HostLocalFixturePlayerRecipe>(fixture.Players.Count);
        for (var index = 0; index < fixture.Players.Count; index += 1)
        {
            var authoredPlayer = fixture.Players[index];
            if (!Sts2ModelResolver.TryResolveLobbyPlayerId(authoredPlayer.Id, out var playerNetId))
            {
                invalid = InvalidFixture(
                    request,
                    $"run.players[{index}].id",
                    authoredPlayer.Id,
                    $"Use a stable {screenLabel} player id in the runtime form p:<positive-net-id>, such as p:1.");
                return false;
            }

            if (!playerNetIds.Add(playerNetId))
            {
                invalid = InvalidFixture(
                    request,
                    $"run.players[{index}].id",
                    authoredPlayer.Id,
                    "Each authored combat player id must be unique.");
                return false;
            }

            if (!Sts2ModelResolver.TryResolveFixtureCharacter(authoredPlayer.Character, out var character))
            {
                invalid = InvalidFixture(
                    request,
                    $"run.players[{index}].characterId",
                    authoredPlayer.Character,
                    "Use a character id from the installed STS2 content, such as IRONCLAD.");
                return false;
            }

            if (authoredPlayer.MaxHp is int maxHp && authoredPlayer.Hp is int hp && hp > maxHp)
            {
                invalid = InvalidFixture(
                    request,
                    $"run.players[{index}].creature.currentHp",
                    hp.ToString(),
                    "hp cannot exceed maxHp in the live fixture loader.");
                return false;
            }

            var player = Player.CreateForNewRun(character, UnlockState.all, playerNetId);
            if (authoredPlayer.MaxHp is int requestedMaxHp)
            {
                player.Creature.SetMaxHpInternal(requestedMaxHp);
            }

            if (authoredPlayer.Hp is int requestedHp)
            {
                player.Creature.SetCurrentHpInternal(requestedHp);
            }

            if (authoredPlayer.PotionIds is not null)
            {
                var potionModels = ResolvePotionModels(request, $"run.players[{index}].potions", authoredPlayer.PotionIds, allowEmpty: true, out invalid);
                if (invalid is not null)
                {
                    return false;
                }

                ApplyAuthoredPlayerPotions(player, potionModels!);
            }

            resolved.Add(new HostLocalFixturePlayerRecipe(
                FixtureId: authoredPlayer.Id,
                NetId: playerNetId,
                Character: character,
                Player: player,
                IsLocal: authoredPlayer.IsLocal == true,
                IsHostLocalSeat: authoredPlayer.IsHostLocalSeat == true,
                SlotId: authoredPlayer.SlotId ?? index,
                EndedTurn: authoredPlayer.EndedTurn == true));
        }

        players = resolved;
        return true;
    }

    private static void ApplyAuthoredPlayerPotions(Player player, IReadOnlyList<PotionModel> potionModels)
    {
        foreach (var potion in player.Potions.ToArray())
        {
            player.DiscardPotionInternal(potion, silent: true);
        }

        if (potionModels.Count > player.MaxPotionCount)
        {
            player.AddToMaxPotionCount(potionModels.Count - player.MaxPotionCount);
        }

        for (var index = 0; index < potionModels.Count; index += 1)
        {
            player.AddPotionInternal(potionModels[index].ToMutable(), slotIndex: index, silent: true);
        }
    }

    // Grants authored relics + extra deck cards to each player's live model AFTER the
    // run is launched but BEFORE the room is entered, so relic/card rest-site hooks
    // (e.g. SHOVEL->DIG, MEAT_CLEAVER->COOK, BYRDONIS_EGG->HATCH) fire in
    // RestSiteOption.Generate. Mirrors ApplyAuthoredPlayerPotions; relics/cards use the
    // live obtain commands (RelicCmd.Obtain / CardPileCmd.Add) since those need an
    // active run.
    private static async Task<FixtureLoadResult?> ApplyAuthoredPlayerGrantsAsync(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture,
        RunState runState,
        IReadOnlyDictionary<string, Player> livePlayersByFixtureId)
    {
        for (var index = 0; index < fixture.Players.Count; index++)
        {
            var authored = fixture.Players[index];
            if (!livePlayersByFixtureId.TryGetValue(authored.Id, out var player))
            {
                continue;
            }

            if (authored.RelicIds is { Count: > 0 } relicIds)
            {
                var relicModels = ResolveRelicModels(request, $"run.players[{index}].relics", relicIds, allowEmpty: true, out var relicInvalid);
                if (relicInvalid is not null)
                {
                    return relicInvalid;
                }

                foreach (var relic in relicModels!)
                {
                    if (relic.HasUponPickupEffect)
                    {
                        // Upon-pickup relics (e.g. PRECARIOUS_SHEARS at the Neow ancient
                        // event) run AfterObtained() inside Obtain, which opens a blocking
                        // card-selection dialog (CardSelectCmd.FromDeckForRemoval). Awaiting
                        // it here would hang the fixture load on the never-resolved dialog,
                        // so fire-and-forget: the relic is added to the bar and the dialog
                        // opens + stays open (the desired "dialog open" checkpoint). Mirrors
                        // ShowChooseACardSelection's loose await of the screen's selection.
                        _ = RelicCmd.Obtain(relic.ToMutable(), player).ContinueWith(
                            task => _ = task.Exception,
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }
                    else
                    {
                        await RelicCmd.Obtain(relic.ToMutable(), player);
                    }
                }
            }

            if (authored.DeckCards is { Count: > 0 } deckCardRecipes)
            {
                // Reuse the combat-pile resolver (model + optional enchantment, with CanEnchant
                // validation). Afflictions on deck cards are ignored — a combat-card concept.
                var deckCards = ResolveAuthoredCombatPileCards(request, $"run.players[{index}].deck.cards", deckCardRecipes, out var cardInvalid);
                if (cardInvalid is not null)
                {
                    return cardInvalid;
                }

                if (authored.DeckReplace)
                {
                    // Replace the starter deck: remove the existing deck cards first so the
                    // master deck (and the deck viewer) shows exactly the authored cards.
                    var deckPile = Sts2LiveIntrospection.GetMemberValue(player, "Deck");
                    if (Sts2LiveIntrospection.GetMemberValue(deckPile, "Cards") is System.Collections.IEnumerable existingCards)
                    {
                        foreach (var existingCard in existingCards.OfType<CardModel>().ToArray())
                        {
                            await CardPileCmd.RemoveFromDeck(existingCard);
                        }
                    }
                }

                foreach (var authoredCard in deckCards)
                {
                    var mutableCard = authoredCard.Card.ToMutable();
                    if (authoredCard.Enchantment is { } enchantment)
                    {
                        var mutableEnchantment = enchantment.ToMutable();
                        mutableCard.EnchantInternal(mutableEnchantment, authoredCard.EnchantAmount);
                        mutableEnchantment.ModifyCard();
                    }

                    // Register ownership + run-state membership so card hooks can
                    // resolve the owning player, then update the actual deck pile
                    // that live state and deck selection screens observe.
                    runState.AddCard(mutableCard, player);
                    await CardPileCmd.Add(mutableCard, PileType.Deck);
                }
            }
        }

        return null;
    }

    // Channels authored orbs into each player's combat orb queue AFTER the combat
    // room is entered (the queue only exists in combat). Mirrors the relic/card
    // grant above, but uses the semantic OrbCmd.Channel command so the orb's
    // Owner, channel history, and AfterOrbChanneled hooks are wired the same way a
    // real channel does. OrbCmd.Channel's UI calls (AddOrbAnim/AddSlotAnim) are
    // all null-conditional and fire-and-forget, so it is headless-safe (unlike the
    // animated CardPileCmd.Add path the deck-card grant avoids).
    //
    // Empty slots are expressed as OrbSlotCount > orb count: capacity is raised to
    // OrbSlotCount before channeling. This is NOT gated on Defect — any character
    // whose live combat state has an orb queue can be granted orbs (allies hold
    // orbs in special situations); OrbCmd.Channel/AddSlots raise a 0-slot
    // character's capacity as needed.
    private static async Task<FixtureLoadResult?> ApplyAuthoredOrbsAsync(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture,
        IReadOnlyDictionary<string, Player> livePlayersByFixtureId)
    {
        for (var index = 0; index < fixture.Players.Count; index++)
        {
            var authored = fixture.Players[index];
            var hasOrbs = authored.OrbIds is { Count: > 0 };
            if (!hasOrbs && authored.OrbSlotCount is null)
            {
                continue;
            }

            if (!livePlayersByFixtureId.TryGetValue(authored.Id, out var player))
            {
                continue;
            }

            // Resolve orb ids up front so a bad id fails before we mutate state.
            IReadOnlyList<OrbModel> orbModels = [];
            if (hasOrbs)
            {
                var resolved = ResolveOrbModels(request, $"run.players[{index}].orbs", authored.OrbIds!, out var orbInvalid);
                if (orbInvalid is not null)
                {
                    return orbInvalid;
                }

                orbModels = resolved!;
            }

            // Wait until combat is fully started before touching the orb queue (the
            // queue and the opening hand both come up when combat starts).
            MegaCrit.Sts2.Core.Entities.Orbs.OrbQueue? orbQueue = null;
            for (var attempt = 0; attempt < 60; attempt += 1)
            {
                orbQueue = player.PlayerCombatState?.OrbQueue;
                var handDealt = player.PlayerCombatState?.Hand?.Cards is { Count: > 0 };
                if (orbQueue is not null && handDealt)
                {
                    break;
                }

                await Task.Delay(16);
            }

            if (orbQueue is null)
            {
                return RuntimeFailure(
                    request,
                    $"run.players[{index}].orbs",
                    "The player's combat orb queue did not become available; orbs require a combat room for the orb-holding player.");
            }

            // Authored orbs are ADDITIVE: they are channeled on top of whatever the
            // character's relics naturally generate at combat start (e.g. Defect's
            // CrackedCore channels a Lightning orb), so do NOT clear the queue. A fixture
            // authors only the orbs that relics don't already provide — letting the relic
            // supply the rest avoids both double-channeling (clearing raced the relic's
            // opening orb) and the stray unpositioned slot that the clear/re-add churn
            // left behind. Raise capacity only when the authored slot count needs more
            // room than the live base (e.g. an ally whose base orb capacity is 0).
            var targetCapacity = authored.OrbSlotCount ?? orbQueue.Capacity;

            if (targetCapacity > orbQueue.Capacity)
            {
                await OrbCmd.AddSlots(player, targetCapacity - orbQueue.Capacity);
            }

            // ToMutable() clones the canonical orb model (Channel asserts a mutable
            // orb and stamps its Owner). A blocking choice context resolves any
            // channel-driven sub-choices headlessly.
            var choiceContext = new MegaCrit.Sts2.Core.GameActions.Multiplayer.BlockingPlayerChoiceContext();
            foreach (var orb in orbModels)
            {
                await OrbCmd.Channel(choiceContext, orb.ToMutable(), player);
            }
        }

        return null;
    }

    // Replaces the live combat piles with authored fixture card lists. The wait below
    // holds until combat's opening work has finished, not merely until the first hand
    // card appears, so the authored piles are the combat's final word. Cards are
    // created in combat scope so ownership, pile ids, and action resolution match
    // normal generated cards.
    private static async Task<FixtureLoadResult?> ApplyAuthoredCombatPilesAsync(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture,
        IReadOnlyDictionary<string, Player> livePlayersByFixtureId)
    {
        for (var playerIndex = 0; playerIndex < fixture.Players.Count; playerIndex += 1)
        {
            var authored = fixture.Players[playerIndex];
            if (authored.Combat is not { HasAuthoredPiles: true } combat)
            {
                continue;
            }

            if (!livePlayersByFixtureId.TryGetValue(authored.Id, out var player))
            {
                continue;
            }

            var resolved = new Dictionary<PileType, IReadOnlyList<AuthoredCombatCard>>
            {
                [PileType.Hand] = ResolveAuthoredCombatPileCards(
                    request,
                    $"run.players[{playerIndex}].combat.hand.cards",
                    combat.HandCards,
                    out var handInvalid),
                [PileType.Draw] = ResolveAuthoredCombatPileCards(
                    request,
                    $"run.players[{playerIndex}].combat.drawPile.cards",
                    combat.DrawPileCards,
                    out var drawInvalid),
                [PileType.Discard] = ResolveAuthoredCombatPileCards(
                    request,
                    $"run.players[{playerIndex}].combat.discardPile.cards",
                    combat.DiscardPileCards,
                    out var discardInvalid),
                [PileType.Exhaust] = ResolveAuthoredCombatPileCards(
                    request,
                    $"run.players[{playerIndex}].combat.exhaustPile.cards",
                    combat.ExhaustPileCards,
                    out var exhaustInvalid),
                [PileType.Play] = ResolveAuthoredCombatPileCards(
                    request,
                    $"run.players[{playerIndex}].combat.playPile.cards",
                    combat.PlayPileCards,
                    out var playInvalid),
            };
            var invalid = handInvalid ?? drawInvalid ?? discardInvalid ?? exhaustInvalid ?? playInvalid;
            if (invalid is not null)
            {
                return invalid;
            }

            // Do not use a non-empty hand as the readiness condition: a hand can be
            // populated while opening work still owns queued card moves. Wait for that
            // work to drain before replacing the piles. Bounded at roughly four seconds
            // to match the other combat setup waits.
            var combatState = player.Creature?.CombatState;
            var openingDealSettled = false;
            var stableOpeningDealSettled = false;
            var consecutiveIdleObservations = 0;
            for (var attempt = 0; attempt < 240; attempt += 1)
            {
                combatState = player.Creature?.CombatState;
                var runManager = RunManager.Instance;
                var actionExecutor = runManager?.ActionExecutor;
                var actionQueueSet = runManager?.ActionQueueSet;
                var combatManager = CombatManager.Instance;
                openingDealSettled = IsOpeningCombatDealSettled(
                    hasCombatState: combatState is not null,
                    hasCombatPiles: player.PlayerCombatState?.AllPiles is not null,
                    handCardCount: player.PlayerCombatState?.Hand?.Cards.Count ?? 0,
                    isAnyPlayerInPlayPhase: Sts2CombatFacts.IsAnyPlayerInPlayPhase(),
                    isPlayerCombatSide: combatManager?.DebugOnlyGetState()?.CurrentSide == CombatSide.Player,
                    hasActionQueue: actionExecutor is not null && actionQueueSet is not null,
                    isActionExecutorRunning: actionExecutor?.IsRunning == true,
                    hasRunningAction: actionExecutor?.CurrentlyRunningAction is not null,
                    hasReadyAction: actionQueueSet?.GetReadyAction() is not null);
                consecutiveIdleObservations = openingDealSettled
                    ? consecutiveIdleObservations + 1
                    : 0;
                // A second observation arrives after AwaitFixtureRenderFramesAsync below,
                // so a momentary gap between queued actions cannot authorize mutation.
                if (HasStableOpeningCombatDealIdleObservation(consecutiveIdleObservations))
                {
                    stableOpeningDealSettled = true;
                    break;
                }

                await AwaitFixtureRenderFramesAsync(1);
            }

            if (combatState is null || player.PlayerCombatState is null)
            {
                return RuntimeFailure(
                    request,
                    $"run.players[{playerIndex}].combat",
                    "The player's combat state did not become available; authored combat piles require a combat room.");
            }

            if (!stableOpeningDealSettled)
            {
                return RuntimeFailure(
                    request,
                    $"run.players[{playerIndex}].combat",
                    "Combat opening work did not settle before the fixture timeout; authored combat piles were left unchanged.");
            }

            // The authored pile is also the rendered pile: remove cards through the
            // normal visual path so no pre-fixture card remains interactive on screen.
            foreach (var existingCard in player.PlayerCombatState.AllPiles.SelectMany(pile => pile.Cards).ToArray())
            {
                await CardPileCmd.RemoveFromCombat(existingCard, skipVisuals: false);
            }

            foreach (var (pileType, cards) in resolved)
            {
                foreach (var authoredCard in cards)
                {
                    var combatCard = combatState.CreateCard(authoredCard.Card, player);
                    await CardPileCmd.AddGeneratedCardToCombat(combatCard, pileType, player, CardPilePosition.Random);
                    if (authoredCard.Affliction is { } affliction)
                    {
                        combatCard.AfflictInternal(affliction.ToMutable(), authoredCard.AfflictionAmount);
                    }

                    if (authoredCard.Enchantment is { } enchantment)
                    {
                        // Same calls NEnchantPreview uses: attach a mutable clone of the canonical
                        // enchantment at the authored amount, then ModifyCard so the card reflects
                        // the enchant (e.g. Tezcatara's Ember -> cost 0 + Eternal).
                        var mutableEnchantment = enchantment.ToMutable();
                        combatCard.EnchantInternal(mutableEnchantment, authoredCard.EnchantAmount);
                        mutableEnchantment.ModifyCard();
                    }
                }
            }
        }

        return null;
    }

    internal static bool IsOpeningCombatDealSettled(
        bool hasCombatState,
        bool hasCombatPiles,
        int handCardCount,
        bool isAnyPlayerInPlayPhase,
        bool isPlayerCombatSide,
        bool hasActionQueue,
        bool isActionExecutorRunning,
        bool hasRunningAction,
        bool hasReadyAction)
        => hasCombatState
            && hasCombatPiles
            && handCardCount > 0
            && isAnyPlayerInPlayPhase
            && isPlayerCombatSide
            && hasActionQueue
            && !isActionExecutorRunning
            && !hasRunningAction
            && !hasReadyAction;

    internal static bool HasStableOpeningCombatDealIdleObservation(int consecutiveIdleObservations)
        => consecutiveIdleObservations >= 2;

    private sealed record AuthoredCombatCard(
        CardModel Card,
        AfflictionModel? Affliction,
        int AfflictionAmount,
        EnchantmentModel? Enchantment = null,
        int EnchantAmount = 1);

    private static IReadOnlyList<AuthoredCombatCard> ResolveAuthoredCombatPileCards(
        FixtureLoadRequestSnapshot request,
        string field,
        IReadOnlyList<FixtureCombatCardRecipe>? cards,
        out FixtureLoadResult? invalid)
    {
        if (cards is null)
        {
            invalid = null;
            return [];
        }

        invalid = null;
        var resolved = new List<AuthoredCombatCard>();
        foreach (var (cardRecipe, index) in cards.Select((card, index) => (card, index)))
        {
            if (!Sts2ModelResolver.TryResolveFixtureCard(cardRecipe.ModelId, out var card))
            {
                invalid = InvalidFixture(
                    request,
                    $"{field}[{index}].modelId",
                    cardRecipe.ModelId,
                    "Use a card id from the installed STS2 content.");
                return [];
            }

            AfflictionModel? affliction = null;
            var amount = cardRecipe.AfflictionAmount <= 0 ? 1 : cardRecipe.AfflictionAmount;
            if (!string.IsNullOrWhiteSpace(cardRecipe.AfflictionId))
            {
                if (cardRecipe.AfflictionAmount <= 0)
                {
                    invalid = InvalidFixture(
                        request,
                        $"{field}[{index}].afflictionAmount",
                        cardRecipe.AfflictionAmount.ToString(),
                        "Use a positive affliction amount.");
                    return [];
                }

                if (!Sts2ModelResolver.TryResolveFixtureAffliction(cardRecipe.AfflictionId, out affliction))
                {
                    invalid = InvalidFixture(
                        request,
                        $"{field}[{index}].afflictionId",
                        cardRecipe.AfflictionId,
                        "Use an affliction id from the installed STS2 content.");
                    return [];
                }

                var validationCard = card.ToMutable();
                if (!affliction.CanAfflict(validationCard))
                {
                    invalid = InvalidFixture(
                        request,
                        $"{field}[{index}].afflictionId",
                        cardRecipe.AfflictionId,
                        $"Affliction '{affliction.Id.Entry}' cannot apply to card '{card.Id.Entry}'.");
                    return [];
                }
            }

            EnchantmentModel? enchantment = null;
            var enchantAmount = cardRecipe.EnchantAmount <= 0 ? 1 : cardRecipe.EnchantAmount;
            if (!string.IsNullOrWhiteSpace(cardRecipe.EnchantmentId))
            {
                enchantment = ResolveEnchantmentModel(cardRecipe.EnchantmentId);
                if (enchantment is null)
                {
                    invalid = InvalidFixture(
                        request,
                        $"{field}[{index}].enchantmentId",
                        cardRecipe.EnchantmentId,
                        "Use an enchantment id from the installed STS2 content (e.g. SHARP, TEZCATARAS_EMBER).");
                    return [];
                }

                // Mirror affliction validation: CanEnchant gates by card type (SHARP/INSTINCT/
                // VIGOROUS are Attack-only, IMBUED Skill-only; Status/Curse/Power are blocked).
                var validationCard = card.ToMutable();
                if (!enchantment.CanEnchant(validationCard))
                {
                    invalid = InvalidFixture(
                        request,
                        $"{field}[{index}].enchantmentId",
                        cardRecipe.EnchantmentId,
                        $"Enchantment '{enchantment.Id.Entry}' cannot apply to card '{card.Id.Entry}'.");
                    return [];
                }
            }

            resolved.Add(new AuthoredCombatCard(card, affliction, amount, enchantment, enchantAmount));
        }

        return resolved;
    }

    // Force-injects rest-site option objects into the synchronizer's live options
    // list for a player (GetOptionsForPlayer returns the backing List). Used for
    // options whose natural trigger can't run headlessly (CLONE/HATCH). Skips ids
    // already present so it composes with relic-derived options.
    private static FixtureLoadResult? InjectExtraRestSiteOptions(
        FixtureLoadRequestSnapshot request,
        object synchronizer,
        Player player,
        IReadOnlyList<string> extraOptionIds)
    {
        if (Sts2LiveIntrospection.InvokeMethod(synchronizer, "GetOptionsForPlayer", player) is not System.Collections.IList options)
        {
            return InvalidFixture(request, "run.currentRoom.restSite.playerStates[].options", string.Empty, "The live rest-site synchronizer did not expose a mutable options list for the player.");
        }

        var present = options
            .Cast<object>()
            .Select(option => Sts2LiveIntrospection.GetMemberValue(option, "OptionId")?.ToString()?.ToUpperInvariant())
            .Where(id => id is not null)
            .ToHashSet();

        foreach (var rawId in extraOptionIds)
        {
            var optionId = rawId.Trim().ToUpperInvariant();
            if (present.Contains(optionId))
            {
                continue;
            }

            RestSiteOption? option = optionId switch
            {
                "HEAL" => new HealRestSiteOption(player),
                "SMITH" => new SmithRestSiteOption(player),
                "MEND" => new MendRestSiteOption(player),
                "COOK" => new CookRestSiteOption(player),
                "DIG" => new DigRestSiteOption(player),
                "HATCH" => new HatchRestSiteOption(player),
                "LIFT" => new LiftRestSiteOption(player),
                "CLONE" => new CloneRestSiteOption(player),
                _ => null,
            };
            if (option is null)
            {
                return InvalidFixture(request, "run.currentRoom.restSite.playerStates[].options[].optionId", rawId, "Use a rest-site option id: HEAL, SMITH, MEND, COOK, DIG, HATCH, LIFT, CLONE.");
            }

            options.Add(option);
            present.Add(optionId);
        }

        return null;
    }

    private static FixtureLoadResult? ValidateHostLocalPlayerOwnership(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture,
        string screenLabel)
    {
        if (fixture.Players.Count == 1)
        {
            if (fixture.Players[0].IsHostLocalSeat == false)
            {
                return InvalidFixture(
                    request,
                    "run.players[0].isHostLocalSeat",
                    "false",
                    $"Single-player {screenLabel} fixtures do not use host-local seat metadata.");
            }

            return null;
        }

        for (var index = 0; index < fixture.Players.Count; index += 1)
        {
            var authoredPlayer = fixture.Players[index];
            if (authoredPlayer.IsLocal is null
                || authoredPlayer.IsHostLocalSeat is null
                || authoredPlayer.SlotId is null)
            {
                return InvalidFixture(
                    request,
                    $"run.players[{index}]",
                    "isLocal/isHostLocalSeat/slotId",
                    $"Multi-player {screenLabel} fixtures must declare isLocal, isHostLocalSeat, and slotId for every player.");
            }

            if (authoredPlayer.IsLocal != true)
            {
                return InvalidFixture(
                    request,
                    $"run.players[{index}].isLocal",
                    "false",
                    $"True remote {screenLabel} players are not actionable by this local fixture setup path.");
            }
        }

        if (fixture.Players.Count > 1)
        {
            var hostOwnedLocalPlayerIndexes = fixture.Players
                .Select((player, index) => new { Player = player, Index = index })
                .Where(entry => entry.Player.IsLocal == true && entry.Player.IsHostLocalSeat == false)
                .ToArray();
            if (hostOwnedLocalPlayerIndexes.Length != 1)
            {
                return InvalidFixture(
                    request,
                    "run.players[].isLocal",
                    $"hostLocalCount={hostOwnedLocalPlayerIndexes.Length}",
                    $"Mark exactly one {screenLabel} player with isLocal: true and isHostLocalSeat: false.");
            }

            var hostOwnedLocalPlayer = hostOwnedLocalPlayerIndexes[0];
            if (!string.Equals(hostOwnedLocalPlayer.Player.Id, fixture.Perspective.PlayerId, StringComparison.Ordinal))
            {
                return InvalidFixture(
                    request,
                    "run.view.playerId",
                    fixture.Perspective.PlayerId,
                    $"Use the {screenLabel} player marked isLocal: true and isHostLocalSeat: false as the local perspective.");
            }

            for (var index = 0; index < fixture.Players.Count; index += 1)
            {
                var player = fixture.Players[index];
                if (index != hostOwnedLocalPlayer.Index && player.IsHostLocalSeat != true)
                {
                    return InvalidFixture(
                        request,
                        $"run.players[{index}].isHostLocalSeat",
                        player.IsHostLocalSeat?.ToString().ToLowerInvariant() ?? string.Empty,
                        $"Every additional local {screenLabel} player must be marked isHostLocalSeat: true.");
                }
            }
        }

        return null;
    }

    private static async Task EnterMapPointInternalCompat(dynamic runManager, int floor, MapPointType pointType, AbstractRoom room)
    {
        var method = runManager.GetType().GetMethod("EnterMapPointInternal", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (method is null)
        {
            throw new MissingMethodException(runManager.GetType().FullName, "EnterMapPointInternal");
        }

        var parameterCount = method.GetParameters().Length;
        object? result = parameterCount switch
        {
            5 => method.Invoke(runManager, new object?[] { floor, pointType, null, room, false }),
            4 => method.Invoke(runManager, new object?[] { floor, pointType, room, false }),
            _ => throw new MissingMethodException($"Unsupported EnterMapPointInternal signature with {parameterCount} parameters."),
        };

        if (result is Task task)
        {
            await task.ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------
    // Internal recipe model. Constructed from the spirectl.fixture/v0 wire
    // document via FixtureWireDocument.ToRecipeDocument(); recipes consume this
    // flattened, fully-defaulted view (the CLI normalizer applies defaults and
    // derives the `screen` recipe hint before sending JSON to the bridge).
    // ------------------------------------------------------------------

    private sealed class FixtureDocument
    {
        public string Screen { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public FixturePerspective Perspective { get; set; } = new();

        public FixtureRun Run { get; set; } = new();

        public List<FixturePlayer> Players { get; set; } = [];

        public FixtureRoom Room { get; set; } = new();

        public FixtureLobby? Lobby { get; set; }

        public FixtureEventRoom? EventRoom { get; set; }

        public FixtureTreasureRoom? TreasureRoom { get; set; }

        public FixtureShop? Shop { get; set; }

        public FixtureSelection? Selection { get; set; }

        public FixtureOverlay? Overlay { get; set; }

        public FixtureUi? Ui { get; set; }

        public FixtureRestSite? RestSite { get; set; }

        // Authored rewards overlay (run.players[].overlays[].rewards): the typed
        // reward items the loader builds, plus the synthetic combat-room context
        // that lets granted relics' reward hooks fire. Null for the legacy empty
        // `rewards: {}` overlay (the loader falls back to a default gold reward).
        public FixtureRewards? Rewards { get; set; }

        // Run-terminal defeat outcome (run.gameOver). When set, the loader stands
        // up a finished run + combat-death history and shows NGameOverScreen.
        public FixtureGameOver? GameOver { get; set; }
    }

    private sealed class FixtureGameOver
    {
        public bool Win { get; set; }

        public string KilledByEncounter { get; set; } = string.Empty;
    }

    private sealed class FixtureRewards
    {
        public string? RoomType { get; set; }

        public string? EncounterId { get; set; }

        public List<FixtureRewardItem> Items { get; set; } = [];
    }

    private sealed class FixtureRewardItem
    {
        public long? Gold { get; set; }

        public FixtureCardReward? Card { get; set; }

        public string? SpecialCardModelId { get; set; }

        public FixtureRewardPotion? Potion { get; set; }

        public FixtureRewardRelic? Relic { get; set; }

        public bool CardRemoval { get; set; }

        // Non-null for a LinkedRewardSet item; holds the nested rewards.
        public List<FixtureRewardItem>? Linked { get; set; }
    }

    private sealed class FixtureCardReward
    {
        // Specific card model ids (any rarity). Empty for a pooled choice.
        public List<string> ModelIds { get; set; } = [];

        // Pooled choice: room pool + option count. Null when ModelIds is set.
        public string? RoomType { get; set; }

        public int? Count { get; set; }

        // When false, the card reward is non-skippable (frees the Skip alternative
        // slot under the game's 2-alternative cap).
        public bool? CanSkip { get; set; }
    }

    private sealed class FixtureRewardPotion
    {
        public string? ModelId { get; set; }
    }

    private sealed class FixtureRewardRelic
    {
        public string? ModelId { get; set; }

        public string? Rarity { get; set; }
    }

    // Rest-site fixture extras. extraOptionIds force-shows campfire options whose
    // natural trigger can't run headlessly — CLONE's relic (PAELS_GROWTH) opens an
    // interactive enchant dialog on obtain, and HATCH's BYRDONIS_EGG must live in a
    // deck pile — so their RestSiteOption objects (both safe to construct, no
    // player-state deref in their descriptions) are injected directly.
    private sealed class FixtureRestSite
    {
        public List<string>? ExtraOptionIds { get; set; }

        public Dictionary<string, JsonElement> ExtraFields { get; set; } = [];
    }

    private sealed class FixturePerspective
    {
        public string PlayerId { get; set; } = string.Empty;
    }

    private sealed class FixtureRun
    {
        // 1-based act, computed from the wire run.currentActIndex (0-based).
        public int Act { get; set; } = 1;

        // 1-based floor within the act (wire run.actFloor).
        public int Floor { get; set; } = 1;

        public string Seed { get; set; } = string.Empty;

        public int? AscensionLevel { get; set; }
    }

    private sealed class FixturePlayer
    {
        public string Id { get; set; } = string.Empty;

        public string Character { get; set; } = string.Empty;

        public int? Hp { get; set; }

        public int? MaxHp { get; set; }

        public int? Gold { get; set; }

        public List<string>? PotionIds { get; set; }

        // Relics granted to the player before room entry (e.g. rest-site options
        // gated by SHOVEL/GIRYA/PAELS_GROWTH/MEAT_CLEAVER fire their hooks during
        // RestSiteOption.Generate).
        public List<string>? RelicIds { get; set; }

        // Cards added to the player's deck before room entry (e.g. the BYRDONIS_EGG quest
        // card surfaces the HATCH rest-site option). Appended to the starter deck unless
        // DeckReplace is set; each card may carry an enchantment.
        public List<FixtureCombatCardRecipe>? DeckCards { get; set; }

        // When true, clear the starter deck before adding DeckCards (replace, not append).
        public bool DeckReplace { get; set; }

        // Combat-only: exact authored combat piles applied after combat entry.
        public FixturePlayerCombat? Combat { get; set; }

        // Combat-only: orbs channeled into the player's orb queue after combat
        // entry. Empty slots are expressed via OrbSlotCount exceeding the orb
        // count, not a sentinel entry.
        public List<string>? OrbIds { get; set; }

        // Combat-only: orb-queue capacity. Null derives from the character base
        // orb slot count; set explicitly to grant slots a character lacks.
        public int? OrbSlotCount { get; set; }

        public bool? IsReady { get; set; }

        public bool? IsLocal { get; set; }

        public bool? IsHostLocalSeat { get; set; }

        public int? SlotId { get; set; }

        // Co-op: when true the loader marks this seat ready-to-end-turn on load.
        public bool? EndedTurn { get; set; }

        // Combat-only: status effects applied to the player's creature after combat
        // entry (WEAK_POWER, FRAIL_POWER, STRENGTH_POWER, …).
        public List<FixturePowerRecipe>? Powers { get; set; }
    }

    private sealed class FixturePowerRecipe
    {
        public string ModelId { get; set; } = string.Empty;

        public int Amount { get; set; } = 1;
    }

    private sealed class FixturePlayerCombat
    {
        public List<FixtureCombatCardRecipe>? HandCards { get; set; }

        public List<FixtureCombatCardRecipe>? DrawPileCards { get; set; }

        public List<FixtureCombatCardRecipe>? DiscardPileCards { get; set; }

        public List<FixtureCombatCardRecipe>? ExhaustPileCards { get; set; }

        public List<FixtureCombatCardRecipe>? PlayPileCards { get; set; }

        public bool HasAuthoredPiles =>
            HandCards is not null
            || DrawPileCards is not null
            || DiscardPileCards is not null
            || ExhaustPileCards is not null
            || PlayPileCards is not null;
    }

    private sealed class FixtureCombatCardRecipe
    {
        public string ModelId { get; set; } = string.Empty;

        public string? AfflictionId { get; set; }

        public int AfflictionAmount { get; set; } = 1;

        public string? EnchantmentId { get; set; }

        public int EnchantAmount { get; set; } = 1;
    }

    // Combat-only: per-enemy authoring (status effects + optional HP override),
    // positional (entry i → enemies[i]).
    private sealed class FixtureEnemyRecipe
    {
        public List<FixturePowerRecipe> Powers { get; set; } = [];

        public int? CurrentHp { get; set; }

        public int? MaxHp { get; set; }
    }

    private sealed class FixtureRoom
    {
        public string EncounterId { get; set; } = string.Empty;

        // Combat-only: per-enemy authoring, positional (entry i → enemies[i]).
        public List<FixtureEnemyRecipe> Enemies { get; set; } = [];

        // Combat-only: authored transient VFX injected into the snapshot.
        public List<FixtureTransientEffectRecipe> TransientEffects { get; set; } = [];

        // Map-only: author a "progressed" map. The loader auto-walks a connected path from the start
        // node up to this row, so run.visitedMapCoords covers a real walked route (the current
        // location = the deepest reached node, and the walked edges render as the traveled path).
        // Null/0 = the default start-parked map.
        public int? TravelToRow { get; set; }

        // Map-only: the start (row-0) node kind — "combat" (a Monster node, like a profile's first run)
        // or "ancient" (the Neow node). Null = "combat" (the historical default).
        public string? FirstNode { get; set; }

        // Map-only: per-row authored types for the traveled path (a richer alternative to TravelToRow).
        // Each entry is a MapPointType name or "Unknown:<RoomType>" for a revealed `?` node (e.g.
        // "Unknown:Event"). When set it supersedes TravelToRow. Null = use TravelToRow.
        public List<string>? TravelPath { get; set; }
    }

    private sealed class FixtureTransientEffectRecipe
    {
        public string Id { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;

        public string? CardModelId { get; set; }

        public string? CardId { get; set; }

        public string? SourceRelicModelId { get; set; }

        public string? AnchorCreatureId { get; set; }

        public int Amount { get; set; }

        public long SpawnedAtMs { get; set; }

        public string? ScenePath { get; set; }
    }

    private sealed class FixtureLobby
    {
        public string Kind { get; set; } = string.Empty;

        public string HostPlayerId { get; set; } = string.Empty;

        public List<string> LockedCharacters { get; set; } = [];
    }

    private sealed class FixtureEventRoom
    {
        public string EventId { get; set; } = string.Empty;

        public List<FixtureOption> Options { get; set; } = [];

        public FixtureCrystalSphere? CrystalSphere { get; set; }

        public string AncientDialogueId { get; set; } = string.Empty;

        public Dictionary<string, JsonElement> ExtraFields { get; set; } = [];
    }

    private sealed class FixtureTreasureRoom
    {
        public string ChestState { get; set; } = string.Empty;

        public List<string> RelicIds { get; set; } = [];

        public bool? CanProceed { get; set; }

        public List<FixtureTreasureRoomPlayerVote> PlayerVotes { get; set; } = [];

        public Dictionary<string, JsonElement> ExtraFields { get; set; } = [];
    }

    private sealed class FixtureTreasureRoomPlayerVote
    {
        public string PlayerId { get; set; } = string.Empty;

        public int? Index { get; set; }
    }

    private sealed class FixtureShop
    {
        // Authored view-player gold (wire run.players[].gold); null leaves the
        // live player's gold untouched.
        public int? Gold { get; set; }

        // True when the wire document authored shop inventory entries; false for
        // `shop: {}` fixtures that use the live generated inventory.
        public bool HasInventory { get; set; }

        public List<string> CardIds { get; set; } = [];

        public List<string> RelicIds { get; set; } = [];

        public List<string> PotionIds { get; set; } = [];

        public bool? CardRemovalAvailable { get; set; }

        public Dictionary<string, JsonElement> ExtraFields { get; set; } = [];
    }

    private sealed class FixtureSelection
    {
        // Deck-card-selection screen variant resolved from the overlay kind
        // (upgrade/transform/enchant/select); null for the other overlay kinds.
        public string? Screen { get; set; }

        // The authored deckCardSelection.kind, kept for recipe reporting.
        public string? Kind { get; set; }

        // For the enchant deck-screen variant: the enchantment applied in the preview
        // (e.g. SWIFT) and its amount.
        public string? EnchantmentId { get; set; }

        public int? EnchantAmount { get; set; }

        public List<string> Cards { get; set; } = [];

        public List<FixtureBundle> Bundles { get; set; } = [];

        public bool? CanSkip { get; set; }

        public bool? CanConfirm { get; set; }

        public Dictionary<string, JsonElement> ExtraFields { get; set; } = [];
    }

    private sealed class FixtureOverlay
    {
        public string Family { get; set; } = string.Empty;

        public string Policy { get; set; } = string.Empty;

        public string SourceScreen { get; set; } = string.Empty;

        public List<string> Cards { get; set; } = [];

        public Dictionary<string, JsonElement> ExtraFields { get; set; } = [];
    }

    private sealed class FixtureUi
    {
        public FixturePotionUi? Potion { get; set; }

        public FixtureCardPileUi? CardPile { get; set; }

        public FixtureDeckViewUi? DeckView { get; set; }

        public FixtureSelectedCardUi? SelectedCard { get; set; }

        public FixtureInspectRelicUi? InspectRelic { get; set; }

        public FixtureHandSelectionUi? HandSelection { get; set; }
    }

    // In-hand selection mode (run.view.handSelection): the loader enters
    // NPlayerHand.SelectCards for real so the staged state and the
    // select/deselect/confirm actions behave like a live card effect.
    private sealed class FixtureHandSelectionUi
    {
        public string PlayerId { get; set; } = string.Empty;

        public string? SourceModelId { get; set; }

        // simple-select (default) or upgrade-select
        public string Mode { get; set; } = "simple-select";

        // discard (the game's TO_DISCARD prompt)
        public string Prompt { get; set; } = "discard";

        public int MinSelect { get; set; } = 1;

        public int MaxSelect { get; set; } = 1;

        public List<int> SelectedCardIndexes { get; set; } = [];
    }

    private sealed class FixtureDeckViewUi
    {
        public string PlayerId { get; set; } = string.Empty;

        public List<FixtureDeckViewSortUi> Sort { get; set; } = [];

        public bool ShowUpgrades { get; set; }
    }

    private sealed class FixtureDeckViewSortUi
    {
        // obtained | type | cost | alphabet
        public string By { get; set; } = string.Empty;

        // ascending | descending
        public string Direction { get; set; } = string.Empty;
    }

    private sealed class FixtureInspectRelicUi
    {
        public string PlayerId { get; set; } = string.Empty;

        public string RelicModelId { get; set; } = string.Empty;
    }

    private sealed class FixtureSelectedCardUi
    {
        public string PlayerId { get; set; } = string.Empty;

        public string CardModelId { get; set; } = string.Empty;
    }

    private sealed class FixtureCardPileUi
    {
        public string PlayerId { get; set; } = string.Empty;

        // draw | discard | exhaust
        public string Pile { get; set; } = string.Empty;
    }

    private sealed class FixturePotionUi
    {
        public string PlayerId { get; set; } = string.Empty;

        public int SlotIndex { get; set; }

        public string Mode { get; set; } = string.Empty;
    }

    private sealed class FixtureBundle
    {
        public string Id { get; set; } = string.Empty;

        public List<string> CardIds { get; set; } = [];
    }

    private sealed class FixtureOption
    {
        public string Id { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;

        public string TextKey { get; set; } = string.Empty;

        public bool? WasChosen { get; set; }
    }

    private sealed class FixtureCrystalSphere
    {
        public string SelectedTool { get; set; } = string.Empty;

        public int DivinationsRemaining { get; set; }

        public List<FixtureCrystalSphereCell> Cells { get; set; } = [];
    }

    private sealed class FixtureCrystalSphereCell
    {
        public string Id { get; set; } = string.Empty;

        public int X { get; set; }

        public int Y { get; set; }

        public bool IsHidden { get; set; }
    }

    // ------------------------------------------------------------------
    // spirectl.fixture/v0 wire document (state-shaped). Mirrors the
    // canonical JSON the CLI normalizer emits; consumed fields are typed and
    // everything else lands in JsonExtensionData so it stays explicit in the
    // recipe report as unsupported.
    // ------------------------------------------------------------------

    private sealed class FixtureWireDocument
    {
        [JsonPropertyName("schemaVersion")]
        public string SchemaVersion { get; set; } = string.Empty;

        public string Screen { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("rootScene")]
        public string? RootScene { get; set; }

        public FixtureWireRun? Run { get; set; }

        [JsonPropertyName("characterSelect")]
        public FixtureCharacterSelect? CharacterSelect { get; set; }

        public FixtureDocument ToRecipeDocument()
        {
            var viewPlayerId = Run?.View?.PlayerId
                ?? CharacterSelect?.View?.PlayerId
                ?? CharacterSelect?.Lobby?.LocalPlayerId
                ?? string.Empty;
            var document = new FixtureDocument
            {
                Screen = Screen,
                Name = Name,
                Perspective = new FixturePerspective { PlayerId = viewPlayerId },
                Run = new FixtureRun
                {
                    Act = (Run?.CurrentActIndex ?? 0) + 1,
                    Floor = Run?.ActFloor ?? 1,
                    Seed = Run?.Seed ?? CharacterSelect?.Lobby?.Seed ?? string.Empty,
                    AscensionLevel = Run?.AscensionLevel,
                },
                GameOver = Run?.GameOver is { } gameOver
                    ? new FixtureGameOver
                    {
                        Win = gameOver.Win ?? false,
                        KilledByEncounter = gameOver.KilledByEncounter ?? string.Empty,
                    }
                    : null,
            };

            if (CharacterSelect is { } characterSelect)
            {
                var localPlayerId = characterSelect.Lobby?.LocalPlayerId ?? string.Empty;
                document.Players = (characterSelect.Lobby?.Players ?? [])
                    .Select(player => new FixturePlayer
                    {
                        Id = player.Id,
                        Character = player.CharacterId,
                        IsReady = player.IsReady,
                        IsLocal = string.Equals(player.Id, localPlayerId, StringComparison.Ordinal) ? true : null,
                        IsHostLocalSeat = player.IsHostLocalSeat,
                        SlotId = player.SlotId,
                    })
                    .ToList();
                document.Lobby = new FixtureLobby
                {
                    Kind = string.IsNullOrWhiteSpace(characterSelect.Kind) ? "start-run" : characterSelect.Kind!,
                    HostPlayerId = characterSelect.Lobby?.HostPlayerId ?? string.Empty,
                    LockedCharacters = characterSelect.CharacterButtons
                        .Where(button => button.IsLocked == true)
                        .Select(button => button.CharacterId)
                        .ToList(),
                };
                return document;
            }

            var runPlayers = Run?.Players ?? [];
            document.Players = runPlayers
                .Select(player => new FixturePlayer
                {
                    Id = player.Id,
                    Character = player.CharacterId,
                    Hp = player.Creature?.CurrentHp,
                    MaxHp = player.Creature?.MaxHp,
                    Gold = player.Gold,
                    PotionIds = player.Potions?.Select(potion => potion.ModelId).ToList(),
                    RelicIds = player.Relics?.Select(relic => relic.ModelId).ToList(),
                    DeckCards = player.Deck?.Cards.Select(ToCombatCardRecipe).ToList(),
                    DeckReplace = player.Deck?.Replace ?? false,
                    Combat = player.Combat is { } combat
                        ? new FixturePlayerCombat
                        {
                            HandCards = combat.Hand?.Cards.Select(ToCombatCardRecipe).ToList(),
                            DrawPileCards = combat.DrawPile?.Cards.Select(ToCombatCardRecipe).ToList(),
                            DiscardPileCards = combat.DiscardPile?.Cards.Select(ToCombatCardRecipe).ToList(),
                            ExhaustPileCards = combat.ExhaustPile?.Cards.Select(ToCombatCardRecipe).ToList(),
                            PlayPileCards = combat.PlayPile?.Cards.Select(ToCombatCardRecipe).ToList(),
                        }
                        : null,
                    OrbIds = player.Orbs?.Select(orb => orb.ModelId).ToList(),
                    OrbSlotCount = player.OrbSlotCount,
                    IsLocal = player.IsLocal,
                    IsHostLocalSeat = player.IsHostLocalSeat,
                    SlotId = player.SlotId,
                    EndedTurn = player.EndedTurn,
                    Powers = player.Powers?
                        .Select(power => new FixturePowerRecipe { ModelId = power.ModelId, Amount = power.Amount })
                        .ToList(),
                })
                .ToList();

            var currentRoom = Run?.CurrentRoom;
            document.Room = new FixtureRoom
            {
                EncounterId = currentRoom?.Combat?.EncounterId ?? string.Empty,
                Enemies = (currentRoom?.Combat?.Enemies ?? [])
                    .Select(enemy => new FixtureEnemyRecipe
                    {
                        Powers = (enemy.Powers ?? [])
                            .Select(power => new FixturePowerRecipe { ModelId = power.ModelId, Amount = power.Amount })
                            .ToList(),
                        CurrentHp = enemy.CurrentHp,
                        MaxHp = enemy.MaxHp,
                    })
                    .ToList(),
                TransientEffects = (currentRoom?.Combat?.TransientEffects ?? [])
                    .Select(effect => new FixtureTransientEffectRecipe
                    {
                        Id = effect.Id,
                        Kind = effect.Kind,
                        CardModelId = effect.CardModelId,
                        CardId = effect.CardId,
                        SourceRelicModelId = effect.SourceRelicModelId,
                        AnchorCreatureId = effect.AnchorCreatureId,
                        Amount = effect.Amount ?? 0,
                        SpawnedAtMs = effect.SpawnedAtMs ?? 0,
                        ScenePath = effect.ScenePath,
                    })
                    .ToList(),
                TravelToRow = ReadMapRoomTravelToRow(currentRoom?.MapRoom),
                FirstNode = ReadMapRoomFirstNode(currentRoom?.MapRoom),
                TravelPath = ReadMapRoomTravelPath(currentRoom?.MapRoom),
            };

            if (currentRoom?.Event is { } eventRoom)
            {
                var playerState = eventRoom.PlayerStates
                        .FirstOrDefault(state => string.Equals(state.PlayerId, viewPlayerId, StringComparison.Ordinal))
                    ?? eventRoom.PlayerStates.FirstOrDefault();
                document.EventRoom = new FixtureEventRoom
                {
                    EventId = eventRoom.CanonicalEventModelId,
                    Options = (playerState?.Options ?? [])
                        .Select(option => new FixtureOption
                        {
                            Id = option.Id,
                            Label = option.TitleText,
                            TextKey = option.TextKey,
                            WasChosen = option.WasChosen,
                        })
                        .ToList(),
                    CrystalSphere = playerState?.CrystalSphere is { } crystalSphere
                        ? new FixtureCrystalSphere
                        {
                            SelectedTool = crystalSphere.SelectedTool,
                            DivinationsRemaining = crystalSphere.DivinationsRemaining,
                            Cells = crystalSphere.Cells
                                .Select(cell => new FixtureCrystalSphereCell
                                {
                                    Id = cell.Id,
                                    X = cell.X,
                                    Y = cell.Y,
                                    IsHidden = cell.IsHidden,
                                })
                                .ToList(),
                        }
                        : null,
                    AncientDialogueId = playerState?.Ancient?.View?.VisibleDialogue?.DialogueId ?? string.Empty,
                    ExtraFields = eventRoom.ExtraFields,
                };
            }

            if (currentRoom?.Treasure is { } treasure)
            {
                document.TreasureRoom = new FixtureTreasureRoom
                {
                    ChestState = treasure.RelicSelectionOpen == true
                        ? "relic-selection"
                        : treasure.CurrentRelicsActive == true ? "opened" : "closed",
                    RelicIds = treasure.CurrentRelics.Select(relic => relic.ModelId).ToList(),
                    CanProceed = treasure.CanProceed,
                    PlayerVotes = treasure.PlayerVotes
                        .Select(vote => new FixtureTreasureRoomPlayerVote { PlayerId = vote.PlayerId, Index = vote.Index })
                        .ToList(),
                    ExtraFields = treasure.ExtraFields,
                };
            }

            if (currentRoom?.Shop is { } shop)
            {
                var viewPlayer = runPlayers
                        .FirstOrDefault(player => string.Equals(player.Id, viewPlayerId, StringComparison.Ordinal))
                    ?? runPlayers.FirstOrDefault();
                var inventory = shop.Inventory;
                if (inventory is not null || viewPlayer?.Gold is not null)
                {
                    var extraFields = new Dictionary<string, JsonElement>(shop.ExtraFields);
                    foreach (var field in inventory?.ExtraFields ?? [])
                    {
                        extraFields[$"inventory.{field.Key}"] = field.Value;
                    }

                    document.Shop = new FixtureShop
                    {
                        Gold = viewPlayer?.Gold,
                        HasInventory = inventory is not null,
                        CardIds = inventory?.CharacterCardEntries
                            .Select(entry => entry.Card?.ModelId ?? string.Empty)
                            .ToList() ?? [],
                        RelicIds = inventory?.RelicEntries.Select(entry => entry.ModelId).ToList() ?? [],
                        PotionIds = inventory?.PotionEntries.Select(entry => entry.ModelId).ToList() ?? [],
                        CardRemovalAvailable = inventory?.CardRemovalEntry is { } cardRemoval
                            ? cardRemoval.Used != true
                            : null,
                        ExtraFields = extraFields,
                    };
                }
            }

            if (currentRoom?.RestSite is { } restSite)
            {
                var playerState = restSite.PlayerStates
                        .FirstOrDefault(state => string.Equals(state.PlayerId, viewPlayerId, StringComparison.Ordinal))
                    ?? restSite.PlayerStates.FirstOrDefault();
                var extraOptionIds = playerState?.Options.Select(option => option.OptionId).ToList();
                document.RestSite = new FixtureRestSite
                {
                    ExtraOptionIds = extraOptionIds is { Count: > 0 } ? extraOptionIds : null,
                    ExtraFields = restSite.ExtraFields,
                };
            }

            var overlayEntry = runPlayers
                .SelectMany(player => player.Overlays ?? [])
                .FirstOrDefault();
            if (overlayEntry?.ChooseACard is { } chooseACard)
            {
                document.Selection = new FixtureSelection
                {
                    Screen = Sts2SupportedScreenIds.ChooseACardSelectionScreenId,
                    Cards = chooseACard.Cards.Select(card => card.ModelId).ToList(),
                    CanSkip = chooseACard.CanSkip,
                    ExtraFields = chooseACard.ExtraFields,
                };
            }
            else if (overlayEntry?.SimpleCardSelection is { } simpleCardSelection)
            {
                document.Selection = new FixtureSelection
                {
                    Screen = Sts2SupportedScreenIds.SimpleCardSelectionScreenId,
                    Cards = simpleCardSelection.Cards.Select(card => card.ModelId).ToList(),
                    CanConfirm = simpleCardSelection.CanConfirm,
                    ExtraFields = simpleCardSelection.ExtraFields,
                };
            }
            else if (overlayEntry?.DeckCardSelection is { } deckCardSelection)
            {
                document.Selection = new FixtureSelection
                {
                    Kind = deckCardSelection.Kind,
                    Screen = deckCardSelection.Kind switch
                    {
                        "transform" => Sts2SupportedScreenIds.DeckTransformSelectionScreenId,
                        "enchant" => Sts2SupportedScreenIds.DeckEnchantSelectionScreenId,
                        "select" => Sts2SupportedScreenIds.DeckCardSelectionScreenId,
                        _ => Sts2SupportedScreenIds.DeckUpgradeSelectionScreenId,
                    },
                    Cards = deckCardSelection.Cards.Select(card => card.ModelId).ToList(),
                    CanSkip = deckCardSelection.CanSkip,
                    CanConfirm = deckCardSelection.CanConfirm,
                    EnchantmentId = deckCardSelection.EnchantmentId,
                    EnchantAmount = deckCardSelection.EnchantAmount,
                    ExtraFields = deckCardSelection.ExtraFields,
                };
            }
            else if (overlayEntry?.BundleSelection is { } bundleSelection)
            {
                document.Selection = new FixtureSelection
                {
                    Screen = Sts2SupportedScreenIds.BundleSelectionScreenId,
                    Bundles = bundleSelection.Bundles
                        .Select(bundle => new FixtureBundle { Id = bundle.Id, CardIds = bundle.CardIds })
                        .ToList(),
                    CanConfirm = bundleSelection.CanConfirm,
                    ExtraFields = bundleSelection.ExtraFields,
                };
            }
            else if (overlayEntry?.CardOverlay is { } cardOverlay)
            {
                document.Overlay = new FixtureOverlay
                {
                    Family = Screen,
                    Policy = cardOverlay.Policy,
                    SourceScreen = cardOverlay.SourceScreen,
                    Cards = cardOverlay.Cards.Select(card => card.ModelId).ToList(),
                    ExtraFields = cardOverlay.ExtraFields,
                };
            }
            else if (overlayEntry?.Rewards is { } rewards)
            {
                document.Rewards = new FixtureRewards
                {
                    RoomType = rewards.RoomType,
                    EncounterId = rewards.EncounterId,
                    Items = MapRewardItems(rewards.Items),
                };
            }

            if (Run?.View is { } view
                && (view.SelectedPotion is not null
                    || view.Capstone?.CardPileView is not null
                    || view.Capstone?.DeckView is not null
                    || view.SelectedCard is not null
                    || view.InspectRelic is not null
                    || view.HandSelection is not null))
            {
                document.Ui = new FixtureUi
                {
                    Potion = view.SelectedPotion is { } potion
                        ? new FixturePotionUi
                        {
                            PlayerId = viewPlayerId,
                            SlotIndex = potion.SlotIndex,
                            Mode = potion.Mode,
                        }
                        : null,
                    CardPile = view.Capstone?.CardPileView is { } cardPile
                        ? new FixtureCardPileUi
                        {
                            PlayerId = string.IsNullOrWhiteSpace(cardPile.PlayerId) ? viewPlayerId : cardPile.PlayerId,
                            Pile = cardPile.PileType,
                        }
                        : null,
                    DeckView = view.Capstone?.DeckView is { } deckView
                        ? new FixtureDeckViewUi
                        {
                            PlayerId = string.IsNullOrWhiteSpace(deckView.PlayerId) ? viewPlayerId : deckView.PlayerId,
                            Sort = deckView.Sort
                                .Select(entry => new FixtureDeckViewSortUi
                                {
                                    By = entry.By,
                                    Direction = entry.Direction,
                                })
                                .ToList(),
                            ShowUpgrades = deckView.ShowUpgrades,
                        }
                        : null,
                    SelectedCard = view.SelectedCard is { } selectedCard
                        ? new FixtureSelectedCardUi
                        {
                            PlayerId = string.IsNullOrWhiteSpace(selectedCard.PlayerId) ? viewPlayerId : selectedCard.PlayerId,
                            CardModelId = selectedCard.CardModelId,
                        }
                        : null,
                    InspectRelic = view.InspectRelic is { } inspectRelic
                        ? new FixtureInspectRelicUi
                        {
                            PlayerId = viewPlayerId,
                            RelicModelId = inspectRelic.RelicModelId,
                        }
                        : null,
                    HandSelection = view.HandSelection is { } handSelection
                        ? new FixtureHandSelectionUi
                        {
                            PlayerId = viewPlayerId,
                            SourceModelId = handSelection.SourceModelId,
                            Mode = handSelection.Mode,
                            Prompt = handSelection.Prompt,
                            MinSelect = handSelection.MinSelect,
                            MaxSelect = handSelection.MaxSelect,
                            SelectedCardIndexes = handSelection.SelectedCardIndexes,
                        }
                        : null,
                };
            }

            return document;
        }

        private static FixtureCombatCardRecipe ToCombatCardRecipe(FixtureCombatCard card)
            => new()
            {
                ModelId = card.ModelId,
                AfflictionId = card.AfflictionId,
                AfflictionAmount = card.AfflictionAmount ?? 1,
                EnchantmentId = card.EnchantmentId,
                EnchantAmount = card.EnchantAmount ?? 1,
            };

        private static List<FixtureRewardItem> MapRewardItems(List<FixtureWireRewardItem> items)
        {
            var mapped = new List<FixtureRewardItem>(items.Count);
            foreach (var item in items)
            {
                mapped.Add(new FixtureRewardItem
                {
                    Gold = item.Gold,
                    Card = item.Card is { } card
                        ? new FixtureCardReward
                        {
                            ModelIds = card.ModelIds,
                            RoomType = card.RoomType,
                            Count = card.Count,
                            CanSkip = card.CanSkip,
                        }
                        : null,
                    SpecialCardModelId = item.SpecialCard?.ModelId,
                    Potion = item.Potion is { } potion
                        ? new FixtureRewardPotion { ModelId = potion.ModelId }
                        : null,
                    Relic = item.Relic is { } relic
                        ? new FixtureRewardRelic { ModelId = relic.ModelId, Rarity = relic.Rarity }
                        : null,
                    CardRemoval = item.CardRemoval ?? false,
                    Linked = item.Linked is { } linked ? MapRewardItems(linked.Items) : null,
                });
            }

            return mapped;
        }
    }

    private sealed class FixtureWireRun
    {
        public string? Seed { get; set; }

        [JsonPropertyName("ascensionLevel")]
        public int? AscensionLevel { get; set; }

        [JsonPropertyName("currentActIndex")]
        public int? CurrentActIndex { get; set; }

        [JsonPropertyName("actFloor")]
        public int? ActFloor { get; set; }

        public List<FixtureRunPlayer> Players { get; set; } = [];

        [JsonPropertyName("currentRoom")]
        public FixtureCurrentRoom? CurrentRoom { get; set; }

        public FixtureRunView? View { get; set; }

        [JsonPropertyName("gameOver")]
        public FixtureWireGameOver? GameOver { get; set; }
    }

    private sealed class FixtureWireGameOver
    {
        [JsonPropertyName("win")]
        public bool? Win { get; set; }

        [JsonPropertyName("killedByEncounter")]
        public string? KilledByEncounter { get; set; }
    }

    private sealed class FixtureRunPlayer
    {
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("characterId")]
        public string CharacterId { get; set; } = string.Empty;

        public FixtureCreature? Creature { get; set; }

        public int? Gold { get; set; }

        public List<FixtureModelRef>? Potions { get; set; }

        public List<FixtureModelRef>? Relics { get; set; }

        public FixtureDeck? Deck { get; set; }

        public FixtureWirePlayerCombat? Combat { get; set; }

        // Combat-only: orbs channeled into the player's orb queue on load.
        public List<FixtureModelRef>? Orbs { get; set; }

        // Combat-only: orb-queue capacity (empty slots = capacity > orb count).
        [JsonPropertyName("orbSlotCount")]
        public int? OrbSlotCount { get; set; }

        [JsonPropertyName("isLocal")]
        public bool? IsLocal { get; set; }

        [JsonPropertyName("isHostLocalSeat")]
        public bool? IsHostLocalSeat { get; set; }

        [JsonPropertyName("slotId")]
        public int? SlotId { get; set; }

        [JsonPropertyName("endedTurn")]
        public bool? EndedTurn { get; set; }

        public List<FixturePower>? Powers { get; set; }

        public List<FixtureOverlayEntry>? Overlays { get; set; }
    }

    private sealed class FixturePower
    {
        [JsonPropertyName("modelId")]
        public string ModelId { get; set; } = string.Empty;

        public int Amount { get; set; } = 1;
    }

    private sealed class FixtureCreature
    {
        [JsonPropertyName("currentHp")]
        public int? CurrentHp { get; set; }

        [JsonPropertyName("maxHp")]
        public int? MaxHp { get; set; }
    }

    private sealed class FixtureDeck
    {
        public List<FixtureCombatCard> Cards { get; set; } = [];

        // When true, replace the starter deck with Cards (clear it first) instead of append.
        [JsonPropertyName("replace")]
        public bool? Replace { get; set; }
    }

    private sealed class FixtureWirePlayerCombat
    {
        public FixtureCombatPile? Hand { get; set; }

        [JsonPropertyName("drawPile")]
        public FixtureCombatPile? DrawPile { get; set; }

        [JsonPropertyName("discardPile")]
        public FixtureCombatPile? DiscardPile { get; set; }

        [JsonPropertyName("exhaustPile")]
        public FixtureCombatPile? ExhaustPile { get; set; }

        [JsonPropertyName("playPile")]
        public FixtureCombatPile? PlayPile { get; set; }
    }

    private sealed class FixtureCombatPile
    {
        public List<FixtureCombatCard> Cards { get; set; } = [];
    }

    private sealed class FixtureCombatCard
    {
        [JsonPropertyName("modelId")]
        public string ModelId { get; set; } = string.Empty;

        [JsonPropertyName("afflictionId")]
        public string? AfflictionId { get; set; }

        [JsonPropertyName("afflictionAmount")]
        public int? AfflictionAmount { get; set; }

        [JsonPropertyName("enchantmentId")]
        public string? EnchantmentId { get; set; }

        [JsonPropertyName("enchantAmount")]
        public int? EnchantAmount { get; set; }
    }

    private sealed class FixtureModelRef
    {
        [JsonPropertyName("modelId")]
        public string ModelId { get; set; } = string.Empty;
    }

    private sealed class FixtureCurrentRoom
    {
        public FixtureCombatRoom? Combat { get; set; }

        public FixtureWireEventRoom? Event { get; set; }

        public FixtureWireTreasureRoom? Treasure { get; set; }

        public FixtureShopRoom? Shop { get; set; }

        [JsonPropertyName("restSite")]
        public FixtureRestSiteRoom? RestSite { get; set; }

        [JsonPropertyName("mapRoom")]
        public JsonElement? MapRoom { get; set; }
    }

    private sealed class FixtureCombatRoom
    {
        [JsonPropertyName("encounterId")]
        public string EncounterId { get; set; } = string.Empty;

        // Per-enemy status effects, positional (entry i → enemies[i]).
        public List<FixtureCombatEnemy>? Enemies { get; set; }

        // Authored transient combat VFX (e.g. a cardUpgrade preview); injected into the
        // snapshot so the renderer can be validated without catching a live transient.
        [JsonPropertyName("transientEffects")]
        public List<FixtureCombatTransientEffect>? TransientEffects { get; set; }
    }

    private sealed class FixtureCombatTransientEffect
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        [JsonPropertyName("cardModelId")]
        public string? CardModelId { get; set; }

        [JsonPropertyName("cardId")]
        public string? CardId { get; set; }

        [JsonPropertyName("sourceRelicModelId")]
        public string? SourceRelicModelId { get; set; }

        [JsonPropertyName("anchorCreatureId")]
        public string? AnchorCreatureId { get; set; }

        [JsonPropertyName("amount")]
        public int? Amount { get; set; }

        [JsonPropertyName("spawnedAtMs")]
        public long? SpawnedAtMs { get; set; }

        [JsonPropertyName("scenePath")]
        public string? ScenePath { get; set; }
    }

    private sealed class FixtureCombatEnemy
    {
        public List<FixturePower>? Powers { get; set; }

        // Combat-only: override the enemy creature's HP on load (e.g. a damaged bar).
        [JsonPropertyName("currentHp")]
        public int? CurrentHp { get; set; }

        [JsonPropertyName("maxHp")]
        public int? MaxHp { get; set; }
    }

    private sealed class FixtureWireEventRoom
    {
        [JsonPropertyName("canonicalEventModelId")]
        public string CanonicalEventModelId { get; set; } = string.Empty;

        [JsonPropertyName("playerStates")]
        public List<FixtureEventPlayerState> PlayerStates { get; set; } = [];

        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtraFields { get; set; } = [];
    }

    private sealed class FixtureEventPlayerState
    {
        [JsonPropertyName("playerId")]
        public string PlayerId { get; set; } = string.Empty;

        public List<FixtureEventOption> Options { get; set; } = [];

        public FixtureEventAncient? Ancient { get; set; }

        [JsonPropertyName("crystalSphere")]
        public FixtureWireCrystalSphere? CrystalSphere { get; set; }
    }

    private sealed class FixtureEventOption
    {
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("titleText")]
        public string TitleText { get; set; } = string.Empty;

        [JsonPropertyName("textKey")]
        public string TextKey { get; set; } = string.Empty;

        [JsonPropertyName("wasChosen")]
        public bool? WasChosen { get; set; }
    }

    private sealed class FixtureEventAncient
    {
        public FixtureEventAncientView? View { get; set; }
    }

    private sealed class FixtureWireCrystalSphere
    {
        [JsonPropertyName("selectedTool")]
        public string SelectedTool { get; set; } = string.Empty;

        [JsonPropertyName("divinationsRemaining")]
        public int DivinationsRemaining { get; set; }

        public List<FixtureWireCrystalSphereCell> Cells { get; set; } = [];
    }

    private sealed class FixtureWireCrystalSphereCell
    {
        public string Id { get; set; } = string.Empty;

        public int X { get; set; }

        public int Y { get; set; }

        [JsonPropertyName("isHidden")]
        public bool IsHidden { get; set; }
    }

    private sealed class FixtureEventAncientView
    {
        [JsonPropertyName("visibleDialogue")]
        public FixtureEventAncientVisibleDialogue? VisibleDialogue { get; set; }
    }

    private sealed class FixtureEventAncientVisibleDialogue
    {
        [JsonPropertyName("dialogueId")]
        public string DialogueId { get; set; } = string.Empty;
    }

    private sealed class FixtureWireTreasureRoom
    {
        [JsonPropertyName("currentRelicsActive")]
        public bool? CurrentRelicsActive { get; set; }

        [JsonPropertyName("relicSelectionOpen")]
        public bool? RelicSelectionOpen { get; set; }

        [JsonPropertyName("currentRelics")]
        public List<FixtureModelRef> CurrentRelics { get; set; } = [];

        [JsonPropertyName("canProceed")]
        public bool? CanProceed { get; set; }

        [JsonPropertyName("playerVotes")]
        public List<FixtureTreasurePlayerVote> PlayerVotes { get; set; } = [];

        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtraFields { get; set; } = [];
    }

    private sealed class FixtureTreasurePlayerVote
    {
        [JsonPropertyName("playerId")]
        public string PlayerId { get; set; } = string.Empty;

        public int? Index { get; set; }
    }

    private sealed class FixtureShopRoom
    {
        public FixtureShopInventory? Inventory { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtraFields { get; set; } = [];
    }

    private sealed class FixtureShopInventory
    {
        [JsonPropertyName("characterCardEntries")]
        public List<FixtureShopCardEntry> CharacterCardEntries { get; set; } = [];

        [JsonPropertyName("relicEntries")]
        public List<FixtureShopModelEntry> RelicEntries { get; set; } = [];

        [JsonPropertyName("potionEntries")]
        public List<FixtureShopModelEntry> PotionEntries { get; set; } = [];

        [JsonPropertyName("cardRemovalEntry")]
        public FixtureShopCardRemovalEntry? CardRemovalEntry { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtraFields { get; set; } = [];
    }

    private sealed class FixtureShopCardEntry
    {
        public FixtureModelRef? Card { get; set; }
    }

    private sealed class FixtureShopModelEntry
    {
        [JsonPropertyName("modelId")]
        public string ModelId { get; set; } = string.Empty;
    }

    private sealed class FixtureShopCardRemovalEntry
    {
        public bool? Used { get; set; }
    }

    private sealed class FixtureRestSiteRoom
    {
        [JsonPropertyName("playerStates")]
        public List<FixtureRestSitePlayerState> PlayerStates { get; set; } = [];

        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtraFields { get; set; } = [];
    }

    private sealed class FixtureRestSitePlayerState
    {
        [JsonPropertyName("playerId")]
        public string PlayerId { get; set; } = string.Empty;

        public List<FixtureRestSiteOption> Options { get; set; } = [];
    }

    private sealed class FixtureRestSiteOption
    {
        [JsonPropertyName("optionId")]
        public string OptionId { get; set; } = string.Empty;
    }

    private sealed class FixtureRunView
    {
        [JsonPropertyName("playerId")]
        public string? PlayerId { get; set; }

        public FixtureCapstoneView? Capstone { get; set; }

        [JsonPropertyName("selectedPotion")]
        public FixtureSelectedPotion? SelectedPotion { get; set; }

        [JsonPropertyName("selectedCard")]
        public FixtureSelectedCard? SelectedCard { get; set; }

        [JsonPropertyName("inspectRelic")]
        public FixtureInspectRelicView? InspectRelic { get; set; }

        [JsonPropertyName("handSelection")]
        public FixtureHandSelectionView? HandSelection { get; set; }
    }

    private sealed class FixtureHandSelectionView
    {
        [JsonPropertyName("sourceModelId")]
        public string? SourceModelId { get; set; }

        [JsonPropertyName("mode")]
        public string Mode { get; set; } = "simple-select";

        public string Prompt { get; set; } = "discard";

        [JsonPropertyName("minSelect")]
        public int MinSelect { get; set; } = 1;

        [JsonPropertyName("maxSelect")]
        public int MaxSelect { get; set; } = 1;

        [JsonPropertyName("selectedCardIndexes")]
        public List<int> SelectedCardIndexes { get; set; } = [];
    }

    private sealed class FixtureInspectRelicView
    {
        [JsonPropertyName("relicModelId")]
        public string RelicModelId { get; set; } = string.Empty;
    }

    private sealed class FixtureCapstoneView
    {
        [JsonPropertyName("cardPileView")]
        public FixtureCardPileView? CardPileView { get; set; }

        [JsonPropertyName("deckView")]
        public FixtureDeckView? DeckView { get; set; }
    }

    private sealed class FixtureCardPileView
    {
        [JsonPropertyName("playerId")]
        public string PlayerId { get; set; } = string.Empty;

        [JsonPropertyName("pileType")]
        public string PileType { get; set; } = string.Empty;
    }

    private sealed class FixtureDeckView
    {
        [JsonPropertyName("playerId")]
        public string PlayerId { get; set; } = string.Empty;

        [JsonPropertyName("sort")]
        public List<FixtureDeckViewSort> Sort { get; set; } = [];

        [JsonPropertyName("showUpgrades")]
        public bool ShowUpgrades { get; set; }
    }

    private sealed class FixtureDeckViewSort
    {
        [JsonPropertyName("by")]
        public string By { get; set; } = string.Empty;

        [JsonPropertyName("direction")]
        public string Direction { get; set; } = string.Empty;
    }

    private sealed class FixtureSelectedPotion
    {
        [JsonPropertyName("slotIndex")]
        public int SlotIndex { get; set; }

        public string Mode { get; set; } = string.Empty;
    }

    private sealed class FixtureSelectedCard
    {
        [JsonPropertyName("playerId")]
        public string PlayerId { get; set; } = string.Empty;

        [JsonPropertyName("cardModelId")]
        public string CardModelId { get; set; } = string.Empty;
    }

    private sealed class FixtureOverlayEntry
    {
        [JsonPropertyName("chooseACard")]
        public FixtureChooseACardOverlay? ChooseACard { get; set; }

        [JsonPropertyName("simpleCardSelection")]
        public FixtureSimpleCardSelectionOverlay? SimpleCardSelection { get; set; }

        [JsonPropertyName("deckCardSelection")]
        public FixtureDeckCardSelectionOverlay? DeckCardSelection { get; set; }

        [JsonPropertyName("bundleSelection")]
        public FixtureBundleSelectionOverlay? BundleSelection { get; set; }

        public FixtureRewardsOverlay? Rewards { get; set; }

        [JsonPropertyName("cardOverlay")]
        public FixtureCardOverlay? CardOverlay { get; set; }
    }

    private sealed class FixtureRewardsOverlay
    {
        // Synthetic combat room type the loader enters so relic reward hooks fire.
        [JsonPropertyName("roomType")]
        public string? RoomType { get; set; }

        [JsonPropertyName("encounterId")]
        public string? EncounterId { get; set; }

        public List<FixtureWireRewardItem> Items { get; set; } = [];

        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtraFields { get; set; } = [];
    }

    // One authored reward (tagged-union; exactly one field set per item, enforced
    // by the CLI normalizer).
    private sealed class FixtureWireRewardItem
    {
        public long? Gold { get; set; }

        public FixtureWireCardReward? Card { get; set; }

        [JsonPropertyName("specialCard")]
        public FixtureModelRef? SpecialCard { get; set; }

        public FixtureWireRewardPotion? Potion { get; set; }

        public FixtureWireRewardRelic? Relic { get; set; }

        [JsonPropertyName("cardRemoval")]
        public bool? CardRemoval { get; set; }

        public FixtureLinkedReward? Linked { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtraFields { get; set; } = [];
    }

    private sealed class FixtureWireCardReward
    {
        [JsonPropertyName("modelIds")]
        public List<string> ModelIds { get; set; } = [];

        [JsonPropertyName("roomType")]
        public string? RoomType { get; set; }

        public int? Count { get; set; }

        [JsonPropertyName("canSkip")]
        public bool? CanSkip { get; set; }
    }

    private sealed class FixtureWireRewardPotion
    {
        [JsonPropertyName("modelId")]
        public string? ModelId { get; set; }
    }

    private sealed class FixtureWireRewardRelic
    {
        [JsonPropertyName("modelId")]
        public string? ModelId { get; set; }

        public string? Rarity { get; set; }
    }

    private sealed class FixtureLinkedReward
    {
        public List<FixtureWireRewardItem> Items { get; set; } = [];
    }

    private sealed class FixtureChooseACardOverlay
    {
        public List<FixtureModelRef> Cards { get; set; } = [];

        [JsonPropertyName("canSkip")]
        public bool? CanSkip { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtraFields { get; set; } = [];
    }

    private sealed class FixtureSimpleCardSelectionOverlay
    {
        public List<FixtureModelRef> Cards { get; set; } = [];

        [JsonPropertyName("canConfirm")]
        public bool? CanConfirm { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtraFields { get; set; } = [];
    }

    private sealed class FixtureDeckCardSelectionOverlay
    {
        public string Kind { get; set; } = string.Empty;

        public List<FixtureModelRef> Cards { get; set; } = [];

        [JsonPropertyName("canSkip")]
        public bool? CanSkip { get; set; }

        [JsonPropertyName("canConfirm")]
        public bool? CanConfirm { get; set; }

        [JsonPropertyName("enchantmentId")]
        public string? EnchantmentId { get; set; }

        [JsonPropertyName("enchantAmount")]
        public int? EnchantAmount { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtraFields { get; set; } = [];
    }

    private sealed class FixtureBundleSelectionOverlay
    {
        public List<FixtureWireBundle> Bundles { get; set; } = [];

        [JsonPropertyName("canConfirm")]
        public bool? CanConfirm { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtraFields { get; set; } = [];
    }

    private sealed class FixtureWireBundle
    {
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("cardIds")]
        public List<string> CardIds { get; set; } = [];
    }

    private sealed class FixtureCardOverlay
    {
        public string Policy { get; set; } = string.Empty;

        [JsonPropertyName("sourceScreen")]
        public string SourceScreen { get; set; } = string.Empty;

        public List<FixtureModelRef> Cards { get; set; } = [];

        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtraFields { get; set; } = [];
    }

    private sealed class FixtureCharacterSelect
    {
        public string? Kind { get; set; }

        public FixtureCharacterSelectLobby? Lobby { get; set; }

        [JsonPropertyName("characterButtons")]
        public List<FixtureCharacterButton> CharacterButtons { get; set; } = [];

        public FixtureCharacterSelectView? View { get; set; }
    }

    private sealed class FixtureCharacterSelectLobby
    {
        [JsonPropertyName("localPlayerId")]
        public string LocalPlayerId { get; set; } = string.Empty;

        [JsonPropertyName("hostPlayerId")]
        public string HostPlayerId { get; set; } = string.Empty;

        public string? Seed { get; set; }

        public List<FixtureCharacterSelectPlayer> Players { get; set; } = [];
    }

    private sealed class FixtureCharacterSelectPlayer
    {
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("characterId")]
        public string CharacterId { get; set; } = string.Empty;

        [JsonPropertyName("isReady")]
        public bool? IsReady { get; set; }

        [JsonPropertyName("slotId")]
        public int? SlotId { get; set; }

        [JsonPropertyName("isHostLocalSeat")]
        public bool? IsHostLocalSeat { get; set; }
    }

    private sealed class FixtureCharacterButton
    {
        [JsonPropertyName("characterId")]
        public string CharacterId { get; set; } = string.Empty;

        [JsonPropertyName("isLocked")]
        public bool? IsLocked { get; set; }
    }

    private sealed class FixtureCharacterSelectView
    {
        [JsonPropertyName("playerId")]
        public string PlayerId { get; set; } = string.Empty;
    }

    private sealed record SinglePlayerFixtureContext(
        RunManager RunManager,
        RunState RunState,
        Player Player);

    private sealed record HostLocalFixtureContext(
        RunManager RunManager,
        RunState RunState,
        IReadOnlyList<HostLocalFixturePlayerRecipe> Players);

    private sealed record HostLocalFixturePlayerRecipe(
        string FixtureId,
        ulong NetId,
        CharacterModel Character,
        Player Player,
        bool IsLocal,
        bool IsHostLocalSeat,
        int SlotId,
        bool EndedTurn = false);

    private static bool TryResolveLobbyRecipe(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture,
        out LobbyFixtureRecipe recipe,
        out FixtureLoadResult? invalid)
    {
        recipe = null!;
        invalid = null;
        if (fixture.Lobby is null)
        {
            invalid = InvalidFixture(
                request,
                "characterSelect",
                string.Empty,
                "Provide a characterSelect section for lobby fixtures.");
            return false;
        }

        if (!string.Equals(fixture.Lobby.Kind, "start-run", StringComparison.Ordinal)
            && !string.Equals(fixture.Lobby.Kind, "load-run", StringComparison.Ordinal))
        {
            invalid = InvalidFixture(
                request,
                "characterSelect.kind",
                fixture.Lobby.Kind,
                $"The live fixture loader currently recognizes start-run {Sts2SupportedScreenIds.StartRunLobbyScreenId} and load-run {Sts2SupportedScreenIds.LoadRunLobbyScreenId} recipes.");
            return false;
        }

        if (string.Equals(fixture.Lobby.Kind, "start-run", StringComparison.Ordinal)
            && !Sts2SupportedScreenIds.IsStartRunLobbyScreenType(fixture.Screen))
        {
            invalid = InvalidFixture(
                request,
                "screen",
                fixture.Screen,
                $"start-run lobby recipes must use screen: {Sts2SupportedScreenIds.StartRunLobbyScreenId}.");
            return false;
        }

        if (string.Equals(fixture.Lobby.Kind, "load-run", StringComparison.Ordinal)
            && !Sts2SupportedScreenIds.IsLoadRunLobbyScreenType(fixture.Screen))
        {
            invalid = InvalidFixture(
                request,
                "screen",
                fixture.Screen,
                $"load-run lobby recipes must use screen: {Sts2SupportedScreenIds.LoadRunLobbyScreenId}.");
            return false;
        }

        var localPlayers = fixture.Players
            .Select((player, index) => new { Player = player, Index = index })
            .Where(entry => entry.Player.IsLocal == true)
            .ToArray();
        if (localPlayers.Length != 1)
        {
            invalid = InvalidFixture(
                request,
                "characterSelect.lobby.localPlayerId",
                fixture.Players.Count.ToString(),
                "The live CharacterSelect fixture path currently requires exactly one local player.");
            return false;
        }

        if (!string.Equals(fixture.Perspective.PlayerId, localPlayers[0].Player.Id, StringComparison.Ordinal))
        {
            invalid = InvalidFixture(
                request,
                "characterSelect.view.playerId",
                fixture.Perspective.PlayerId,
                "characterSelect.view.playerId must match the local multiplayer lobby player.");
            return false;
        }

        if (!Sts2ModelResolver.TryResolveLobbyPlayerId(localPlayers[0].Player.Id, out var localNetId))
        {
            invalid = InvalidFixture(
                request,
                $"characterSelect.lobby.players[{localPlayers[0].Index}].id",
                localPlayers[0].Player.Id,
                "Use a stable lobby player id in the runtime form p:<positive-net-id>, such as p:1.");
            return false;
        }

        if (localNetId != 1)
        {
            invalid = InvalidFixture(
                request,
                $"characterSelect.lobby.players[{localPlayers[0].Index}].id",
                localPlayers[0].Player.Id,
                "The live CharacterSelect fixture path currently realizes the local player through p:1 only.");
            return false;
        }

        var players = new List<LobbyFixturePlayerRecipe>(fixture.Players.Count);
        var playerNetIds = new HashSet<ulong>();
        for (var index = 0; index < fixture.Players.Count; index += 1)
        {
            var player = fixture.Players[index];
            if (!Sts2ModelResolver.TryResolveLobbyPlayerId(player.Id, out var playerNetId))
            {
                invalid = InvalidFixture(
                    request,
                    $"characterSelect.lobby.players[{index}].id",
                    player.Id,
                    "Use a stable lobby player id in the runtime form p:<positive-net-id>, such as p:1.");
                return false;
            }

            if (!playerNetIds.Add(playerNetId))
            {
                invalid = InvalidFixture(
                    request,
                    $"characterSelect.lobby.players[{index}].id",
                    player.Id,
                    "Each authored multiplayer lobby player id must be unique.");
                return false;
            }

            if (!Sts2ModelResolver.TryResolveFixtureCharacter(player.Character, out var character))
            {
                invalid = InvalidFixture(
                    request,
                    $"characterSelect.lobby.players[{index}].characterId",
                    player.Character,
                    "Use a character id from the installed STS2 content, such as IRONCLAD.");
                return false;
            }

            players.Add(new LobbyFixturePlayerRecipe(
                FixtureId: player.Id,
                NetId: playerNetId,
                Character: character,
                IsReady: player.IsReady == true,
                IsLocal: player.IsLocal == true,
                IsHostLocalSeat: player.IsHostLocalSeat == true,
                SlotId: player.SlotId ?? index));
        }

        if (string.IsNullOrWhiteSpace(fixture.Lobby.HostPlayerId)
            || fixture.Players.All(player => !string.Equals(player.Id, fixture.Lobby.HostPlayerId, StringComparison.Ordinal)))
        {
            invalid = InvalidFixture(
                request,
                "characterSelect.lobby.hostPlayerId",
                fixture.Lobby.HostPlayerId,
                "characterSelect.lobby.hostPlayerId must name one of the authored multiplayer lobby players.");
            return false;
        }

        foreach (var player in players)
        {
            var authoredPlayer = fixture.Players.FirstOrDefault(entry => string.Equals(entry.Id, player.FixtureId, StringComparison.Ordinal));
            if (authoredPlayer?.IsHostLocalSeat is null)
            {
                continue;
            }

            if (player.IsLocal)
            {
                invalid = InvalidFixture(
                    request,
                    "characterSelect.lobby.players[].isHostLocalSeat",
                    player.FixtureId,
                    "isHostLocalSeat cannot be combined with the local lobby player.");
                return false;
            }
        }

        var lockedCharacterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < fixture.Lobby.LockedCharacters.Count; index += 1)
        {
            var characterId = fixture.Lobby.LockedCharacters[index];
            if (!Sts2ModelResolver.TryResolveFixtureCharacter(characterId, out var character))
            {
                invalid = InvalidFixture(
                    request,
                    $"characterSelect.characterButtons[{index}].characterId",
                    characterId,
                    $"Valid values: {ValidLobbyCharacterIds()}.");
                return false;
            }

            lockedCharacterIds.Add(character.Id.Entry);
        }

        foreach (var player in players)
        {
            if (lockedCharacterIds.Contains(player.Character.Id.Entry))
            {
                invalid = InvalidFixture(
                    request,
                    "characterSelect.characterButtons",
                    string.Join(", ", fixture.Lobby.LockedCharacters),
                    $"The authored player '{player.FixtureId}' selected '{player.Character.Id.Entry}', which cannot also be marked as a locked character button.");
                return false;
            }
        }

        var localPlayer = players.Single(player => player.IsLocal);
        Sts2HostLocalSeatRegistry.ReplaceHostLocalSeats(
            players.Where(player => player.IsHostLocalSeat).Select(player => player.NetId));
        recipe = new LobbyFixtureRecipe(
            Kind: fixture.Lobby.Kind,
            LocalPlayer: localPlayer,
            RemotePlayers: players.Where(player => !player.IsLocal).ToArray(),
            AllPlayers: players,
            LockedCharacterIds: lockedCharacterIds);
        return true;
    }

    private static string ValidLobbyCharacterIds()
        => string.Join(
            ", ",
            ModelDb.AllCharacters
                .Select(character => character.Id.Entry)
                .Order(StringComparer.Ordinal));

    private sealed record LobbyFixtureRecipe(
        string Kind,
        LobbyFixturePlayerRecipe LocalPlayer,
        IReadOnlyList<LobbyFixturePlayerRecipe> RemotePlayers,
        IReadOnlyList<LobbyFixturePlayerRecipe> AllPlayers,
        IReadOnlySet<string> LockedCharacterIds);

    private sealed record LobbyFixturePlayerRecipe(
        string FixtureId,
        ulong NetId,
        CharacterModel Character,
        bool IsReady,
        bool IsLocal,
        bool IsHostLocalSeat,
        int SlotId);
}
