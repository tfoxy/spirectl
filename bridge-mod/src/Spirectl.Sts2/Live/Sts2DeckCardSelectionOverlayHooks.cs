using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live;

// Resolves the deck-card-selection overlay family — the rest-site SMITH (upgrade)
// dialog and its siblings transform/enchant/remove, shown live as
// `NDeck*SelectScreen` overlays on the OverlayStack. Unlike the choose-a-card
// overlay there is no transient command flow to Harmony-patch (the screen is a
// persistent mounted node), so this is a passive visible-screen resolver only,
// invoked from Sts2StateProvider.ResolvePlayerOverlays. Card selection and the
// upgrade preview are reused wholesale from Sts2CardSelectionScreenInspector;
// each card carries its upgraded form inline via StateCard.NextDynamicVars.
internal static class Sts2DeckCardSelectionOverlayHooks
{
    public static StateRunOverlaySnapshot? ResolveVisibleDeckCardSelectionOverlay(string playerId)
    {
        var screen = ResolveVisibleDeckScreen();
        if (!Sts2CardSelectionScreenInspector.IsDeckCardSelectionScreen(screen))
        {
            return null;
        }

        var (kind, screenId) = ResolveKind(screen);
        if (kind is null || screenId is null)
        {
            return null;
        }

        // Read the screen's authored card list directly. The deck grid screens hold
        // their cards in the inherited `_cards` field (the live grid is `_grid`, an
        // NCardGrid, not the `_cardRow` that ResolveChoices walks), and `_cards` is
        // populated synchronously by the screen factory — so this is both correct
        // and free of UI-population timing, mirroring the choose-a-card hook.
        var cards = Enumerate(Sts2LiveIntrospection.GetMemberValue(screen, "_cards"))
            .OfType<CardModel>()
            .ToArray();
        if (cards.Length == 0)
        {
            return null;
        }

        // Multiplayer scoping: the overlay belongs to the deck's owner.
        var ownerNetId = cards
            .Select(card => ToUInt64(Sts2LiveIntrospection.GetMemberValue(
                Sts2LiveIntrospection.GetMemberValue(card, "Owner"), "NetId")))
            .FirstOrDefault(netId => netId.HasValue);
        if (ownerNetId is null || !string.Equals(playerId, $"p:{ownerNetId.Value}", StringComparison.Ordinal))
        {
            return null;
        }

        var scene = ResolveScene(screen!) ?? $"screens/card_selection/deck_{kind}_select_screen";
        var canSkip = Sts2LiveIntrospection.GetMemberValue(screen, "_skipButton") is Control skip && skip.Visible;
        var (enchantmentTitle, enchantmentDescription, enchantmentIconPath, enchantmentExtraCardText) =
            kind == "enchant" ? ResolveEnchantmentPreview(screen!) : (null, null, null, null);

        // Build the card snapshots and a model->stable-id map in one pass so the
        // selected-card ids resolve to the same ids the renderer uses for select-card.
        var cardIdByModel = new Dictionary<CardModel, string>(ReferenceEqualityComparer.Instance);
        var cardSnapshots = cards
            .Select((card, index) =>
            {
                var id = $"card:{playerId}:deck-card-selection:{kind}:{index}";
                cardIdByModel[card] = id;
                return Sts2CardStateSnapshotFactory.Create(card, id);
            })
            .ToArray();

        // Selection sizing + prompt + current selection, mirroring the simple-grid
        // overlay so the renderer can drive confirm visibility, the bottom prompt,
        // and per-card selected state. The live game shows confirm only when
        // MinSelect != MaxSelect && selected >= MinSelect (exact-N removals never
        // show it and auto-preview at max); the renderer applies that rule.
        var prefs = Sts2LiveIntrospection.GetMemberValue(screen, "_prefs");
        var (promptText, promptLoc) = Sts2CardSelectionScreenInspector.ResolveSelectionPrompt(prefs);
        var selectedCardIds = Sts2CardSelectionScreenInspector.ResolveSelectedCards(screen)
            .OfType<CardModel>()
            .Select(card => cardIdByModel.TryGetValue(card, out var id) ? id : null)
            .Where(id => id is not null)
            .Select(id => id!)
            .ToArray();

        return new StateRunOverlaySnapshot(
            Id: $"overlay:{playerId}:deck-card-selection:{kind}:visible",
            ScreenType: "CardSelection",
            ScreenId: screenId,
            Scene: scene,
            ChooseACard: null,
            Rewards: null,
            DeckCardSelection: new StateDeckCardSelectionOverlaySnapshot(
                Kind: kind,
                CanSkip: canSkip,
                CanConfirm: Sts2CardSelectionScreenInspector.CanConfirmSelection(screen),
                PreviewActive: Sts2CardSelectionScreenInspector.IsDeckPreviewVisible(screen),
                Cards: cardSnapshots,
                PromptText: promptText,
                PromptLoc: promptLoc,
                MinSelect: Sts2LiveIntrospection.GetMemberValue(prefs, "MinSelect") as int? ?? 0,
                MaxSelect: Sts2LiveIntrospection.GetMemberValue(prefs, "MaxSelect") as int? ?? 0,
                SelectedCardIds: selectedCardIds,
                CanCancel: Sts2CardSelectionScreenInspector.CanCancelSelection(screen),
                EnchantmentTitle: enchantmentTitle,
                EnchantmentDescription: enchantmentDescription,
                EnchantmentIconPath: enchantmentIconPath,
                EnchantmentExtraCardText: enchantmentExtraCardText));
    }

    // The enchant dialog targets a specific enchantment at a specific amount,
    // shown in the description panel. Mirror NDeckEnchantSelectScreen.Setup:
    // clone the authored enchantment, apply the screen's amount, recalculate, and
    // read the same formatted title/description/icon the live labels show — so the
    // render is faithful (amount already baked in) instead of the .tscn placeholder.
    private static (string? title, string? description, string? iconPath, string? extraCardText) ResolveEnchantmentPreview(object screen)
    {
        var enchantment = Sts2LiveIntrospection.GetMemberValue(screen, "_enchantment");
        if (enchantment is null)
        {
            return (null, null, null, null);
        }

        var model = Sts2LiveIntrospection.InvokeMethod(enchantment, "ToMutable") ?? enchantment;
        if (Sts2LiveIntrospection.GetMemberValue(screen, "_enchantmentAmount") is int amount)
        {
            Sts2LiveIntrospection.TrySetMemberValue(model, "Amount", amount);
            Sts2LiveIntrospection.TryInvokeParameterlessMethod(model, "RecalculateValues");
        }

        return (
            FormattedText(Sts2LiveIntrospection.GetMemberValue(model, "Title")),
            FormattedText(Sts2LiveIntrospection.GetMemberValue(model, "DynamicDescription")),
            Sts2LiveIntrospection.GetMemberValue(
                Sts2LiveIntrospection.GetMemberValue(model, "Icon"), "ResourcePath") as string,
            // The short text the enchantment appends to an affected card's description
            // (CardModel.GetDescriptionForPile appends "[purple]" + DynamicExtraCardText +
            // "[/purple]"). Read from the CANONICAL `_enchantment`, NOT the mutable clone:
            // DynamicExtraCardText's non-canonical branch dereferences the (null) attached
            // Card.TargetType and hard-crashes Godot; the canonical branch is card-free
            // (TargetType "None"). Null when the enchantment has no extra card text.
            FormattedText(Sts2LiveIntrospection.GetMemberValue(enchantment, "DynamicExtraCardText")));
    }

    // Enchantment Title/DynamicDescription are loc strings exposing GetFormattedText().
    private static string? FormattedText(object? locString)
        => locString is null
            ? null
            : Sts2LiveIntrospection.InvokeMethod(locString, "GetFormattedText") as string;

    // The deck-card-selection screens live on the OverlayStack, which renders over
    // whatever room/screen is active (e.g. the smith dialog over the campfire). The
    // OverlayStack top is the authoritative source — ActiveScreenContext returns the
    // room underneath, so peek the stack first and fall back to the current screen
    // for the roomless case.
    private static object? ResolveVisibleDeckScreen()
    {
        var top = NOverlayStack.Instance?.Peek();
        if (Sts2CardSelectionScreenInspector.IsDeckCardSelectionScreen(top))
        {
            return top;
        }

        return Sts2LiveIntrospection.ResolveCurrentScreenObject();
    }

    // Most specific first: the upgrade/transform/enchant screens may subtype the
    // base NDeckCardSelectScreen ("remove"), so the base must be matched last.
    private static (string? kind, string? screenId) ResolveKind(object? screen)
    {
        if (Sts2LiveIntrospection.IsType(screen, Sts2CardSelectionScreenInspector.DeckUpgradeSelectionScreenType))
        {
            return ("upgrade", Sts2SupportedScreenIds.DeckUpgradeSelectionScreenId);
        }

        if (Sts2LiveIntrospection.IsType(screen, Sts2CardSelectionScreenInspector.DeckTransformSelectionScreenType))
        {
            return ("transform", Sts2SupportedScreenIds.DeckTransformSelectionScreenId);
        }

        if (Sts2LiveIntrospection.IsType(screen, Sts2CardSelectionScreenInspector.DeckEnchantSelectionScreenType))
        {
            return ("enchant", Sts2SupportedScreenIds.DeckEnchantSelectionScreenId);
        }

        if (Sts2LiveIntrospection.IsType(screen, Sts2CardSelectionScreenInspector.DeckCardSelectionScreenType))
        {
            return ("remove", Sts2SupportedScreenIds.DeckCardSelectionScreenId);
        }

        return (null, null);
    }

    // res://scenes/<rel>.tscn -> <rel> (the catalog scene key), e.g.
    // res://scenes/screens/card_selection/deck_upgrade_select_screen.tscn ->
    // screens/card_selection/deck_upgrade_select_screen.
    private static string? ResolveScene(object screen)
    {
        if (Sts2LiveIntrospection.GetMemberValue(screen, "SceneFilePath") is not string path
            || path.Length == 0)
        {
            return null;
        }

        const string prefix = "res://scenes/";
        var rel = path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
        if (rel.EndsWith(".tscn", StringComparison.Ordinal))
        {
            rel = rel[..^".tscn".Length];
        }

        return rel.Length > 0 ? rel : null;
    }

    private static IEnumerable<object> Enumerate(object? value)
    {
        if (value is System.Collections.IEnumerable items && value is not string)
        {
            foreach (var item in items)
            {
                if (item is not null)
                {
                    yield return item;
                }
            }
        }
    }

    private static ulong? ToUInt64(object? value)
        => value switch
        {
            ulong u => u,
            uint u => u,
            long l when l >= 0 => (ulong)l,
            int i when i >= 0 => (ulong)i,
            _ => null,
        };
}
