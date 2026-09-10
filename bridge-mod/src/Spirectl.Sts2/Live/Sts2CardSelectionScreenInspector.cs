using System.Collections;
using Godot;
using MegaCrit.Sts2.Core.Models;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live;

internal static class Sts2CardSelectionScreenInspector
{
    public const string CardRewardSelectionScreenType =
        "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen";

    public const string ChooseACardSelectionScreenType =
        "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NChooseACardSelectionScreen";

    public const string SimpleCardSelectionScreenType =
        "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NSimpleCardSelectScreen";

    public const string DeckCardSelectionScreenType =
        "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckCardSelectScreen";

    public const string DeckUpgradeSelectionScreenType =
        "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckUpgradeSelectScreen";

    public const string DeckTransformSelectionScreenType =
        "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckTransformSelectScreen";

    public const string DeckEnchantSelectionScreenType =
        "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckEnchantSelectScreen";

    public const string BundleSelectionScreenType =
        "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NChooseABundleSelectionScreen";

    public static bool IsSupportedScreen(object? screenObject)
        => Sts2LiveIntrospection.IsType(screenObject, CardRewardSelectionScreenType)
            || Sts2LiveIntrospection.IsType(screenObject, ChooseACardSelectionScreenType)
            || IsSimpleCardSelectionScreen(screenObject)
            || IsDeckCardSelectionScreen(screenObject)
            || IsBundleSelectionScreen(screenObject);

    public static bool IsPartialCardGridScreen(object? screenObject)
        => IsSimpleCardSelectionScreen(screenObject)
            || IsDeckCardSelectionScreen(screenObject);

    // The combat card reward (NCardRewardSelectionScreen) keeps its offered cards in `_cardRow`
    // holders (no flat `_cards` list like the event NChooseACardSelectionScreen). Expose them — in the
    // same holder order ResolveCardChoices uses — so the overlay hook can surface this screen as a
    // chooseACard overlay (which the catalog renders with select-card; the pick already routes here).
    public static IReadOnlyList<CardModel> ResolveCardRewardCardModels(object screenObject)
    {
        var cardRow = Sts2LiveIntrospection.GetMemberValue(screenObject, "_cardRow") as Control;
        if (cardRow is null)
        {
            return [];
        }

        var cards = new List<CardModel>();
        foreach (var holder in EnumerateChildren(cardRow))
        {
            if (!IsVisible(holder))
            {
                continue;
            }

            if (ResolveCardModel(holder) is { } model)
            {
                cards.Add(model);
            }
        }

        return cards;
    }

    public static bool ResolveCardRewardCanSkip(object screenObject)
    {
        var alternatives = (Sts2LiveIntrospection.GetMemberValue(screenObject, "_extraOptions") as IEnumerable)?
            .Cast<object>() ?? [];
        return alternatives.Any(alternative => string.Equals(
            Normalize(Sts2LiveIntrospection.GetMemberValue(alternative, "OptionId")?.ToString())?.ToLowerInvariant(),
            "skip",
            StringComparison.Ordinal));
    }

    public static IReadOnlyList<ResolvedCardSelectionChoice> ResolveChoices(
        object cardSelectionScreen,
        string? defaultPlayerId,
        ICollection<StateNoticeSnapshot>? notices = null)
    {
        if (Sts2LiveIntrospection.IsType(cardSelectionScreen, CardRewardSelectionScreenType))
        {
            return ResolveCardRewardChoices(cardSelectionScreen, defaultPlayerId, notices);
        }

        if (Sts2LiveIntrospection.IsType(cardSelectionScreen, ChooseACardSelectionScreenType))
        {
            return ResolveChooseACardChoices(cardSelectionScreen, defaultPlayerId, notices);
        }

        if (IsPartialCardGridScreen(cardSelectionScreen))
        {
            return ResolvePartialCardGridChoices(cardSelectionScreen, defaultPlayerId, notices);
        }

        if (IsBundleSelectionScreen(cardSelectionScreen))
        {
            return ResolveBundleChoices(cardSelectionScreen, defaultPlayerId, notices);
        }

        return [];
    }

    // Typed overlay for the simple card-grid screen (NSimpleCardSelectScreen, e.g.
    // Headbutt). Inspection-only (the screen is game-initiated, so no command hook
    // like choose-a-card); the caller attaches it to the local player. Card ids are
    // the same select-card choice ids the action surface emits, so the renderer can
    // drive select-card directly off card.id and match selected_card_ids by identity.
    public static StateRunOverlaySnapshot? ResolveVisibleSimpleGridCardSelectionOverlay(
        string playerId,
        ICollection<StateNoticeSnapshot>? notices = null)
    {
        var screen = Sts2LiveIntrospection.ResolveCurrentScreenObject();
        if (!IsSimpleCardSelectionScreen(screen))
        {
            return null;
        }

        var choices = ResolveChoices(screen!, playerId, notices)
            .Where(choice => choice.ExecutionKind == CardSelectionChoiceExecutionKind.Card)
            .ToArray();
        if (choices.Length == 0)
        {
            return null;
        }

        var cards = new List<StateCardSnapshot>(choices.Length);
        var cardIdByModel = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        foreach (var choice in choices)
        {
            if (choice.Target is not CardModel cardModel)
            {
                continue;
            }

            cards.Add(Sts2CardStateSnapshotFactory.Create(cardModel, choice.Snapshot.Id));
            cardIdByModel[cardModel] = choice.Snapshot.Id;
        }

        var selectedCardIds = ResolveSelectedCards(screen)
            .Select(card => cardIdByModel.TryGetValue(card, out var id) ? id : null)
            .Where(id => id is not null)
            .Select(id => id!)
            .ToArray();

        var prefs = Sts2LiveIntrospection.GetMemberValue(screen, "_prefs");
        var (promptText, promptLoc) = ResolveSelectionPrompt(prefs);

        return new StateRunOverlaySnapshot(
            Id: $"overlay:{playerId}:simple-grid:visible",
            ScreenType: "CardSelection",
            ScreenId: Sts2SupportedScreenIds.SimpleCardSelectionScreenId,
            Scene: "screens/card_selection/simple_card_select_screen",
            ChooseACard: null,
            SimpleGridCardSelection: new StateSimpleGridCardSelectionOverlaySnapshot(
                PromptText: promptText,
                PromptLoc: promptLoc,
                MinSelect: Sts2LiveIntrospection.GetMemberValue(prefs, "MinSelect") as int? ?? 0,
                MaxSelect: Sts2LiveIntrospection.GetMemberValue(prefs, "MaxSelect") as int? ?? 0,
                CanConfirm: CanConfirmSelection(screen),
                CanCancel: CanCancelSelection(screen),
                PreviewActive: IsDeckPreviewVisible(screen),
                SelectedCardIds: selectedCardIds,
                Cards: cards));
    }

    // Typed overlay for the bundle screen (NChooseABundleSelectionScreen, e.g. the
    // ScrollBoxes relic). Each bundle is a stable-id'd group of cards taken whole via
    // select-bundle; clicking a bundle stages it and shows a preview/confirm panel.
    public static StateRunOverlaySnapshot? ResolveVisibleBundleCardSelectionOverlay(
        string playerId,
        ICollection<StateNoticeSnapshot>? notices = null)
    {
        var screen = Sts2LiveIntrospection.ResolveCurrentScreenObject();
        if (!IsBundleSelectionScreen(screen))
        {
            return null;
        }

        var bundleRow = Sts2LiveIntrospection.GetMemberValue(screen, "_bundleRow");
        if (bundleRow is null || !IsVisible(bundleRow))
        {
            return null;
        }

        var bundles = new List<StateCardBundleSnapshot>();
        var optionIndex = 0;
        foreach (var bundleControl in EnumerateChildren(bundleRow))
        {
            if (!IsVisible(bundleControl))
            {
                continue;
            }

            var bundleCards = ResolveBundleCards(bundleControl);
            if (bundleCards.Count == 0)
            {
                continue;
            }

            var stableBundleId = ResolveBundleStableId(bundleCards, optionIndex, notices);
            var bundleId = Sts2CardSelectionIds.BundleChoiceId(stableBundleId, optionIndex);
            var cardSnapshots = bundleCards
                .Select((card, cardIndex) => Sts2CardStateSnapshotFactory.Create(card, $"{bundleId}:card:{cardIndex}"))
                .ToArray();
            bundles.Add(new StateCardBundleSnapshot(bundleId, cardSnapshots));
            optionIndex++;
        }

        if (bundles.Count == 0)
        {
            return null;
        }

        var previewContainer = Sts2LiveIntrospection.GetMemberValue(screen, "_bundlePreviewContainer");
        var previewActive = previewContainer is not null && IsVisible(previewContainer);

        var selectedBundleId = string.Empty;
        if (Sts2LiveIntrospection.GetMemberValue(screen, "_selectedBundle") is { } selectedBundle)
        {
            var selectedCards = ResolveBundleCards(selectedBundle);
            if (selectedCards.Count > 0)
            {
                var stableId = ResolveBundleStableId(selectedCards, 0, notices);
                selectedBundleId = bundles
                    .FirstOrDefault(bundle => bundle.Id.Contains(stableId, StringComparison.Ordinal))?.Id
                    ?? string.Empty;
            }
        }

        var confirmButton = Sts2LiveIntrospection.GetMemberValue(screen, "_previewConfirmButton");
        var canConfirm = previewActive && ResolveIsExecutable(confirmButton, defaultValue: false);

        return new StateRunOverlaySnapshot(
            Id: $"overlay:{playerId}:bundle:visible",
            ScreenType: "CardSelection",
            ScreenId: Sts2SupportedScreenIds.BundleSelectionScreenId,
            Scene: "screens/card_selection/choose_a_bundle_selection_screen",
            ChooseACard: null,
            BundleCardSelection: new StateBundleCardSelectionOverlaySnapshot(
                Bundles: bundles,
                CanConfirm: canConfirm,
                PreviewActive: previewActive,
                SelectedBundleId: selectedBundleId));
    }

    internal static (string PromptText, StateLocRefSnapshot? PromptLoc) ResolveSelectionPrompt(object? prefs)
    {
        var prompt = Sts2LiveIntrospection.GetMemberValue(prefs, "Prompt");
        if (prompt is null)
        {
            return (string.Empty, null);
        }

        var promptText = Normalize(Sts2LiveIntrospection.InvokeMethod(prompt, "GetFormattedText")?.ToString())
            ?? string.Empty;
        var promptTable = Sts2LiveIntrospection.GetMemberValue(prompt, "LocTable") as string;
        var promptKey = Sts2LiveIntrospection.GetMemberValue(prompt, "LocEntryKey") as string;
        var promptLoc = !string.IsNullOrEmpty(promptTable) && !string.IsNullOrEmpty(promptKey)
            ? new StateLocRefSnapshot(promptTable!, promptKey!)
            : null;
        return (promptText, promptLoc);
    }

    private static IReadOnlyList<ResolvedCardSelectionChoice> ResolveCardRewardChoices(
        object screenObject,
        string? defaultPlayerId,
        ICollection<StateNoticeSnapshot>? notices)
    {
        var choices = new List<ResolvedCardSelectionChoice>();
        choices.AddRange(ResolveCardChoices(
            Sts2LiveIntrospection.GetMemberValue(screenObject, "_cardRow") as Control,
            executeMethodName: "SelectCard",
            defaultPlayerId,
            notices));

        var alternatives = (Sts2LiveIntrospection.GetMemberValue(screenObject, "_extraOptions") as IEnumerable)?.Cast<object>().ToArray()
            ?? [];
        var buttons = (Sts2LiveIntrospection.GetMemberValue(screenObject, "_rewardAlternativesContainer") as Control)?
            .GetChildren()
            .OfType<Control>()
            .Where(static control => control.Visible)
            .ToArray()
            ?? [];

        for (var index = 0; index < alternatives.Length; index++)
        {
            if (index >= buttons.Length)
            {
                AddNotice(
                    notices,
                    "card-selection-alternative-button-mismatch",
                    "Card-selection alternative buttons fell back to model ordering because the live button list was shorter than the alternative model list.");
            }

            var alternative = alternatives[index];
            var button = index < buttons.Length ? buttons[index] : null;
            if (button is null)
            {
                continue;
            }

            var optionId = Normalize(Sts2LiveIntrospection.GetMemberValue(alternative, "OptionId")?.ToString());
            if (string.IsNullOrWhiteSpace(optionId))
            {
                AddNotice(
                    notices,
                    "card-selection-alternative-id-fallback",
                    "Card-selection alternative ids fell back to screen ordering because the live model did not expose option ids.");
                optionId = $"option-{index}";
            }

            var normalizedOptionId = optionId!.ToLowerInvariant();
            var isSkip = string.Equals(normalizedOptionId, "skip", StringComparison.Ordinal);
            var label = ResolveChoiceLabel(alternative, fallback: isSkip ? "Skip" : "Alternative");

            choices.Add(new ResolvedCardSelectionChoice(
                CreateChoiceSnapshot(
                    isSkip
                        ? Sts2CardSelectionIds.SkipChoiceId()
                        : Sts2CardSelectionIds.AlternativeChoiceId(normalizedOptionId, index),
                    label,
                    isSkip ? "card-selection-skip" : "card-selection-alternative",
                    defaultPlayerId,
                    isSkip ? "skip-card-selection" : "choose",
                    isSkip ? SemanticActionKind.SkipCardSelection : SemanticActionKind.Choose,
                    ResolveIsExecutable(button)),
                Target: button,
                ExecuteMethodName: null,
                ExecutionKind: CardSelectionChoiceExecutionKind.Alternative,
                Alternative: alternative,
                IsExecutable: ResolveIsExecutable(button)));
        }

        return choices;
    }

    private static IReadOnlyList<ResolvedCardSelectionChoice> ResolveChooseACardChoices(
        object screenObject,
        string? defaultPlayerId,
        ICollection<StateNoticeSnapshot>? notices)
    {
        var choices = new List<ResolvedCardSelectionChoice>();
        choices.AddRange(ResolveCardChoices(
            Sts2LiveIntrospection.GetMemberValue(screenObject, "_cardRow") as Control,
            executeMethodName: "SelectHolder",
            defaultPlayerId,
            notices));

        if (Sts2LiveIntrospection.GetMemberValue(screenObject, "_skipButton") is Control skipButton
            && skipButton.Visible)
        {
            choices.Add(new ResolvedCardSelectionChoice(
                CreateChoiceSnapshot(
                    Sts2CardSelectionIds.SkipChoiceId(),
                    "Skip",
                    "card-selection-skip",
                    defaultPlayerId,
                    "skip-card-selection",
                    SemanticActionKind.SkipCardSelection,
                    ResolveIsExecutable(skipButton)),
                Target: skipButton,
                ExecuteMethodName: "OnSkipButtonReleased",
                ExecutionKind: CardSelectionChoiceExecutionKind.SkipButton,
                Alternative: null,
                IsExecutable: ResolveIsExecutable(skipButton)));
        }

        return choices;
    }

    private static IReadOnlyList<ResolvedCardSelectionChoice> ResolvePartialCardGridChoices(
        object screenObject,
        string? defaultPlayerId,
        ICollection<StateNoticeSnapshot>? notices)
    {
        var previewVisible = IsDeckPreviewVisible(screenObject);
        var selectedCards = ResolveSelectedCards(screenObject);
        var maxSelectedCards = ResolveCardSelectionMaxSelect(screenObject);
        // Live NCardGridSelectionScreen builds (NSimpleCardSelectScreen and the
        // deck variants) keep their cards in an NCardGrid (`_grid`) rather than
        // a flat `_cardRow`; fall back to the grid's displayed holders so
        // game-initiated screens (e.g. Headbutt mid-combat) expose choices.
        var cardRow = Sts2LiveIntrospection.GetMemberValue(screenObject, "_cardRow") as Control;
        var holders = cardRow is not null
            ? EnumerateChildren(cardRow)
            : (Sts2LiveIntrospection.GetMemberValue(
                    Sts2LiveIntrospection.GetMemberValue(screenObject, "_grid"),
                    "CurrentlyDisplayedCardHolders") as IEnumerable)?.Cast<object>() ?? [];
        var choices = ResolveCardChoicesFromHolders(
            holders,
            executeMethodName: "OnCardClicked",
            defaultPlayerId,
            notices);

        return choices
            .Select(choice => choice with
            {
                Target = ResolveCardModel(choice.Target) ?? choice.Target,
                IsExecutable = ResolveIsPartialCardGridChoiceExecutable(
                    choice.Target,
                    ResolveCardModel(choice.Target),
                    selectedCards,
                    previewVisible,
                    maxSelectedCards),
            })
            .ToArray();
    }

    public static bool CanConfirmSelection(object? screenObject)
    {
        if (screenObject is null)
        {
            return false;
        }

        if (IsSimpleCardSelectionScreen(screenObject))
        {
            var simpleConfirmButton = Sts2LiveIntrospection.GetMemberValue(screenObject, "_confirmButton");
            return ResolveSelectedCardCount(screenObject) > 0
                && ResolveIsExecutable(simpleConfirmButton!, defaultValue: false);
        }

        if (!IsDeckCardSelectionScreen(screenObject))
        {
            return false;
        }

        if (IsDeckPreviewVisible(screenObject))
        {
            // Deck variants name their preview confirm button differently: NDeckCardSelect/NDeckUpgradeSelect
            // use _previewConfirmButton; NDeckEnchantSelect uses _single/_multiPreviewConfirmButton. Mirror
            // the fallback chain TryConfirmSelection already uses, so the enchant overlay (e.g. SELF_HELP_BOOK)
            // isn't gated to false while its single-preview confirm button is the armed one.
            var previewConfirmButton =
                Sts2LiveIntrospection.GetMemberValue(screenObject, "_previewConfirmButton")
                ?? Sts2LiveIntrospection.GetMemberValue(screenObject, "_singlePreviewConfirmButton")
                ?? Sts2LiveIntrospection.GetMemberValue(screenObject, "_multiPreviewConfirmButton");
            return ResolveSelectedCardCount(screenObject) > 0
                && ResolveIsExecutable(previewConfirmButton!, defaultValue: false);
        }

        var deckConfirmButton = Sts2LiveIntrospection.GetMemberValue(screenObject, "_confirmButton");
        return ResolveSelectedCardCount(screenObject) > 0
            && ResolveIsExecutable(deckConfirmButton!, defaultValue: false);
    }

    public static bool CanCancelSelection(object? screenObject)
    {
        if (screenObject is null)
        {
            return false;
        }

        if (IsSimpleCardSelectionScreen(screenObject))
        {
            return ResolveSelectedCardCount(screenObject) > 0;
        }

        if (!IsDeckCardSelectionScreen(screenObject))
        {
            return false;
        }

        if (IsDeckPreviewVisible(screenObject))
        {
            var previewCancelButton = Sts2LiveIntrospection.GetMemberValue(screenObject, "_previewCancelButton");
            return ResolveSelectedCardCount(screenObject) > 0
                && ResolveIsExecutable(previewCancelButton!, defaultValue: false);
        }

        return ResolveSelectedCardCount(screenObject) > 0;
    }

    public static bool TryConfirmSelection(object? screenObject)
    {
        if (screenObject is null)
        {
            return false;
        }

        if (IsSimpleCardSelectionScreen(screenObject))
        {
            return Sts2LiveIntrospection.TryInvokeParameterlessMethod(screenObject, "CompleteSelection");
        }

        if (!IsDeckCardSelectionScreen(screenObject))
        {
            return false;
        }

        // The deck variants are NOT uniform — they resolve the staged selection (set the completion source
        // gated on count in [MinSelect, MaxSelect]) under different names, and their preview confirm buttons
        // / containers differ (NDeckCardSelect/NDeckUpgradeSelect use _previewConfirmButton + _previewContainer;
        // NDeckEnchantSelect uses _single/_multiPreviewConfirmButton + _enchant*PreviewContainer; NDeckTransform
        // commits via CompleteSelection rather than CheckIfSelectionComplete). OnCardClicked already auto-opens
        // the preview once the selection is full; none of the commit methods require it to be open, so commit
        // directly. Prefer the parameterless CheckIfSelectionComplete (card/upgrade/enchant); fall back to the
        // preview confirm button + CompleteSelection/ConfirmSelection (transform).
        if (Sts2LiveIntrospection.TryInvokeParameterlessMethod(screenObject, "CheckIfSelectionComplete"))
        {
            return true;
        }

        var previewConfirmButton = Sts2LiveIntrospection.GetMemberValue(screenObject, "_previewConfirmButton")
            ?? Sts2LiveIntrospection.GetMemberValue(screenObject, "_singlePreviewConfirmButton")
            ?? Sts2LiveIntrospection.GetMemberValue(screenObject, "_multiPreviewConfirmButton");
        return previewConfirmButton is not null
            && (Sts2LiveIntrospection.TryInvokeMethod(screenObject, "CompleteSelection", previewConfirmButton)
                || Sts2LiveIntrospection.TryInvokeMethod(screenObject, "ConfirmSelection", previewConfirmButton));
    }

    public static bool TryCancelSelection(object? screenObject)
    {
        if (screenObject is null)
        {
            return false;
        }

        if (IsDeckCardSelectionScreen(screenObject) && IsDeckPreviewVisible(screenObject))
        {
            var previewCancelButton = Sts2LiveIntrospection.GetMemberValue(screenObject, "_previewCancelButton");
            return previewCancelButton is not null
                && Sts2LiveIntrospection.TryInvokeMethod(screenObject, "CancelSelection", previewCancelButton);
        }

        var selectedCards = ResolveSelectedCards(screenObject);
        if (selectedCards.Count == 0)
        {
            return false;
        }

        var cancelledAny = false;
        foreach (var selectedCard in selectedCards)
        {
            cancelledAny |= Sts2LiveIntrospection.TryInvokeMethod(screenObject, "OnCardClicked", selectedCard);
        }

        return cancelledAny;
    }

    public static int ResolveSelectedCardCount(object? screenObject)
        => ResolveSelectedCards(screenObject).Count;

    // Stage a CLIENT-staged deck selection (the deck-card-removal preview commits its
    // locally-staged ids in one call): map each stable card id
    // `card:<playerId>:deck-card-selection:<kind>:<index>` to the screen's `_cards[index]`
    // (the same array Sts2DeckCardSelectionOverlayHooks indexes to mint the ids) and click
    // it via OnCardClicked, skipping cards already staged on the host so re-clicks don't
    // toggle them off. Mirrors the confirm-hand-selection stage-then-confirm fold. Returns
    // false (with the offending id) when an id does not resolve to a card on this screen.
    public static bool TryStageDeckSelection(object? screenObject, IReadOnlyList<string> cardIds, out string? unresolvedCardId)
    {
        unresolvedCardId = null;
        if (!IsDeckCardSelectionScreen(screenObject))
        {
            // Non-deck screens (simple-grid etc.) carry no per-index ids; leave the host's
            // already-staged selection untouched and let the caller confirm it as-is.
            return true;
        }

        var cards = (Sts2LiveIntrospection.GetMemberValue(screenObject, "_cards") as IEnumerable)?
            .Cast<object>()
            .OfType<CardModel>()
            .ToArray() ?? [];
        var staged = new HashSet<object>(ResolveSelectedCards(screenObject), ReferenceEqualityComparer.Instance);

        foreach (var rawId in cardIds)
        {
            var id = rawId?.Trim() ?? string.Empty;
            if (id.Length == 0)
            {
                continue;
            }

            if (!TryParseDeckCardIndex(id, out var index) || index < 0 || index >= cards.Length)
            {
                unresolvedCardId = id;
                return false;
            }

            var model = cards[index];
            if (staged.Contains(model))
            {
                continue;
            }

            if (!Sts2LiveIntrospection.TryInvokeMethod(screenObject, "OnCardClicked", model))
            {
                unresolvedCardId = id;
                return false;
            }

            staged.Add(model);
        }

        return true;
    }

    // Parse the trailing index from a deck-card stable id
    // `card:<playerId>:deck-card-selection:<kind>:<index>`. The playerId itself contains a
    // colon (`p:<netId>`), so match the fixed `deck-card-selection` marker third-from-last
    // and parse the last segment.
    private static bool TryParseDeckCardIndex(string id, out int index)
    {
        index = -1;
        var parts = id.Split(':');
        return parts.Length >= 4
            && string.Equals(parts[^3], "deck-card-selection", StringComparison.Ordinal)
            && int.TryParse(parts[^1], out index);
    }

    private static IReadOnlyList<ResolvedCardSelectionChoice> ResolveBundleChoices(
        object screenObject,
        string? defaultPlayerId,
        ICollection<StateNoticeSnapshot>? notices)
    {
        var bundleRow = Sts2LiveIntrospection.GetMemberValue(screenObject, "_bundleRow");
        if (bundleRow is null || !IsVisible(bundleRow))
        {
            return [];
        }

        var choices = new List<ResolvedCardSelectionChoice>();
        var optionIndex = 0;
        foreach (var bundleControl in EnumerateChildren(bundleRow))
        {
            if (!IsVisible(bundleControl))
            {
                continue;
            }

            var bundleCards = ResolveBundleCards(bundleControl);
            if (bundleCards.Count == 0)
            {
                continue;
            }

            var stableBundleId = ResolveBundleStableId(bundleCards, optionIndex, notices);
            var bundleLabel = ResolveBundleLabel(bundleCards, optionIndex);
            choices.Add(new ResolvedCardSelectionChoice(
                CreateChoiceSnapshot(
                    Sts2CardSelectionIds.BundleChoiceId(stableBundleId, optionIndex),
                    bundleLabel,
                    "card-selection-bundle",
                    defaultPlayerId,
                    "select-bundle",
                    SemanticActionKind.SelectBundle,
                    ResolveIsExecutable(Sts2LiveIntrospection.GetMemberValue(bundleControl, "Hitbox") ?? bundleControl)),
                Target: bundleControl,
                ExecuteMethodName: null,
                ExecutionKind: CardSelectionChoiceExecutionKind.Bundle,
                Alternative: null,
                IsExecutable: ResolveIsExecutable(
                    Sts2LiveIntrospection.GetMemberValue(bundleControl, "Hitbox") ?? bundleControl)));
            optionIndex++;
        }

        return choices;
    }

    private static IReadOnlyList<ResolvedCardSelectionChoice> ResolveCardChoices(
        object? cardRow,
        string executeMethodName,
        string? defaultPlayerId,
        ICollection<StateNoticeSnapshot>? notices)
        => cardRow is null
            ? []
            : ResolveCardChoicesFromHolders(EnumerateChildren(cardRow), executeMethodName, defaultPlayerId, notices);

    private static IReadOnlyList<ResolvedCardSelectionChoice> ResolveCardChoicesFromHolders(
        IEnumerable<object> holders,
        string executeMethodName,
        string? defaultPlayerId,
        ICollection<StateNoticeSnapshot>? notices)
    {
        var choices = new List<ResolvedCardSelectionChoice>();
        var index = 0;
        foreach (var holder in holders)
        {
            if (!IsVisible(holder))
            {
                index++;
                continue;
            }

            var cardModel = ResolveCardModel(holder);
            if (cardModel is null)
            {
                index++;
                continue;
            }

            var stableCardId = ResolveModelId(cardModel);
            if (string.IsNullOrWhiteSpace(stableCardId))
            {
                AddNotice(
                    notices,
                    "card-selection-card-id-fallback",
                    "Card-selection card ids fell back to screen ordering because the live card model did not expose a stable id.");
                stableCardId = $"card-{index}";
            }

            choices.Add(new ResolvedCardSelectionChoice(
                CreateChoiceSnapshot(
                    Sts2CardSelectionIds.CardChoiceId(stableCardId!, index),
                    ResolveCardLabel(cardModel),
                    "card-selection-card",
                    defaultPlayerId,
                    "select-card",
                    SemanticActionKind.SelectCard,
                    ResolveIsExecutable(holder)),
                Target: holder,
                ExecuteMethodName: executeMethodName,
                ExecutionKind: CardSelectionChoiceExecutionKind.Card,
                Alternative: null,
                IsExecutable: ResolveIsExecutable(holder)));
            index++;
        }

        return choices;
    }

    private static ChoiceSnapshot CreateChoiceSnapshot(
        string id,
        string label,
        string kind,
        string? ownerPlayerId,
        string preferredAction,
        SemanticActionKind actionKind,
        bool enabled)
    {
        var intentKind = preferredAction;
        var values = new Dictionary<string, string>();
        string? choiceId = null;
        if (actionKind == SemanticActionKind.SelectCard)
        {
            values["cardId"] = id;
        }
        else if (actionKind == SemanticActionKind.SelectBundle)
        {
            values["bundleId"] = id;
        }
        else if (actionKind == SemanticActionKind.Choose)
        {
            choiceId = id;
        }

        var arguments = new ActionArgumentsSnapshot(
            ownerPlayerId,
            actionKind == SemanticActionKind.SelectCard ? id : null,
            null,
            choiceId,
            null,
            null,
            IntentKind: intentKind,
            Values: values.Count == 0 ? null : values);

        return new ChoiceSnapshot(
            Id: id,
            Label: label,
            Kind: kind,
            Provisional: false,
            OwnerPlayerId: ownerPlayerId,
            ChoiceKind: kind,
            IntentKind: intentKind,
            Perspective: ResolvePerspective(ownerPlayerId),
            PreferredAction: preferredAction,
            Arguments: arguments,
            Enabled: enabled,
            DisabledReason: enabled ? null : "not-enabled",
            PreferredActionRef: enabled && actionKind != SemanticActionKind.Choose
                ? new VisibleActionReferenceSnapshot(
                    Sts2ActionIds.Intent("card-selection", preferredAction, id),
                    label,
                    Enabled: true,
                    OwnerPlayerId: ownerPlayerId,
                    ActionKind: actionKind,
                    IntentKind: intentKind,
                    Arguments: arguments,
                    LegalityStatus: ActionLegalityKind.Legal,
                    Perspective: ResolvePerspective(ownerPlayerId),
                    CheckedHookPaths: CheckedHookPathsFor(kind))
                : null,
            LegalityStatus: enabled ? ActionLegalityKind.Legal : ActionLegalityKind.Illegal,
            CheckedHookPaths: CheckedHookPathsFor(kind));
    }

    private static IReadOnlyList<string> CheckedHookPathsFor(string kind)
        => kind switch
        {
            "card-selection-card" => ["cardSelectionScreen.SelectCard/SelectHolder/OnCardClicked"],
            "card-selection-skip" => ["cardSelectionScreen.OnSkipButtonReleased/rewardAlternative"],
            "card-selection-bundle" => ["bundleSelectionScreen.OnBundleSelected"],
            _ => [],
        };

    private static string? ResolvePerspective(string? ownerPlayerId)
        => string.IsNullOrWhiteSpace(ownerPlayerId) ? null : "local";

    private static IEnumerable<object> EnumerateChildren(object parent)
    {
        if (parent is Node node)
        {
            return node.GetChildren().Cast<object>();
        }

        if (Sts2LiveIntrospection.GetMemberValue(parent, "Children") is IEnumerable children)
        {
            return children.Cast<object>();
        }

        if (Sts2LiveIntrospection.InvokeMethod(parent, "GetChildren") is IEnumerable methodChildren)
        {
            return methodChildren.Cast<object>();
        }

        return [];
    }

    private static CardModel? ResolveCardModel(object holder)
    {
        return Sts2LiveIntrospection.GetMemberValue(holder, "CardModel") as CardModel
            ?? Sts2LiveIntrospection.GetMemberValue(
                Sts2LiveIntrospection.GetMemberValue(holder, "CardNode"),
                "Model") as CardModel;
    }

    private static string ResolveCardLabel(object cardModel)
        => ResolveChoiceLabel(
            Sts2LiveIntrospection.GetMemberValue(cardModel, "Title"),
            fallback: ResolveModelId(cardModel) ?? "Card");

    private static IReadOnlyList<object> ResolveBundleCards(object bundleControl)
    {
        return (Sts2LiveIntrospection.GetMemberValue(bundleControl, "Bundle") as IEnumerable)?
            .Cast<object>()
            .ToArray()
            ?? [];
    }

    private static string ResolveBundleStableId(
        IReadOnlyList<object> bundleCards,
        int optionIndex,
        ICollection<StateNoticeSnapshot>? notices)
    {
        var stableCardIds = bundleCards
            .Select(ResolveModelId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id!)
            .ToArray();
        if (stableCardIds.Length == bundleCards.Count)
        {
            return string.Join("+", stableCardIds);
        }

        AddNotice(
            notices,
            "bundle-selection-id-fallback",
            "Bundle-selection choice ids fell back to screen ordering because one or more visible bundle cards did not expose stable ids.");
        return $"bundle-{optionIndex}";
    }

    private static string ResolveBundleLabel(IReadOnlyList<object> bundleCards, int optionIndex)
    {
        var cardLabels = bundleCards
            .Select(ResolveCardLabel)
            .Where(static label => !string.IsNullOrWhiteSpace(label))
            .ToArray();
        if (cardLabels.Length == 0)
        {
            return $"Bundle {optionIndex + 1}";
        }

        return $"Bundle {optionIndex + 1}: {string.Join(", ", cardLabels)}";
    }

    private static string ResolveChoiceLabel(object? value, string fallback)
    {
        return Normalize(Sts2LiveIntrospection.InvokeMethod(value, "GetFormattedText")?.ToString())
            ?? Normalize(Sts2LiveIntrospection.InvokeMethod(value, "GetRawText")?.ToString())
            ?? Normalize(Sts2LiveIntrospection.GetMemberValue(value, "Text")?.ToString())
            ?? Normalize(value?.ToString())
            ?? fallback;
    }

    private static string? ResolveModelId(object? model)
    {
        var id = Sts2LiveIntrospection.GetMemberValue(model, "Id");
        return Normalize(Sts2LiveIntrospection.GetMemberValue(id, "Entry")?.ToString())
            ?? Normalize(id?.ToString());
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.ReplaceLineEndings(" ").Trim();

    private static bool ResolveIsExecutable(object? control, bool defaultValue = true)
    {
        if (control is null)
        {
            return false;
        }

        return Sts2LiveIntrospection.GetMemberValue(control, "Disabled") switch
        {
            bool disabled => !disabled,
            _ => Sts2LiveIntrospection.GetMemberValue(control, "IsEnabled") switch
            {
                bool isEnabled => isEnabled,
                _ => Sts2LiveIntrospection.GetMemberValue(control, "IsDisabled") switch
                {
                    bool isDisabled => !isDisabled,
                    _ => defaultValue,
                },
            },
        };
    }

    private static bool ResolveIsPartialCardGridChoiceExecutable(
        object holder,
        object? cardModel,
        IReadOnlyList<object> selectedCards,
        bool previewVisible,
        int? maxSelectedCards)
    {
        // The live card holder's engine Disabled/IsEnabled flag is unreliable in headless/browser mode
        // (it reports not-executable even for freely selectable cards — same wall as the treasure relic
        // holder and rest-site options), so don't gate on it here. The preview / already-selected /
        // max-select checks below are the real logical gates, and the OnCardClicked hook + the screen
        // itself still reject a genuinely invalid pick.
        if (previewVisible)
        {
            return false;
        }

        if (cardModel is not null && selectedCards.Contains(cardModel))
        {
            return false;
        }

        return !maxSelectedCards.HasValue || selectedCards.Count < maxSelectedCards.Value;
    }

    internal static IReadOnlyList<object> ResolveSelectedCards(object? screenObject)
    {
        return (Sts2LiveIntrospection.GetMemberValue(screenObject, "_selectedCards") as IEnumerable)?
            .Cast<object>()
            .ToArray()
            ?? [];
    }

    internal static int? ResolveCardSelectionMaxSelect(object? screenObject)
    {
        var prefs = Sts2LiveIntrospection.GetMemberValue(screenObject, "_prefs");
        return Sts2LiveIntrospection.GetMemberValue(prefs, "MaxSelect") as int?;
    }

    private static readonly string[] s_deckPreviewContainerNames =
    [
        "_previewContainer",
        "_enchantSinglePreviewContainer",
        "_enchantMultiPreviewContainer",
    ];

    internal static bool IsDeckPreviewVisible(object? screenObject)
    {
        // Deck variants name their staged-selection preview container differently: NDeckCardSelect/
        // NDeckUpgradeSelect/NDeckTransformSelect use `_previewContainer`; NDeckEnchantSelect splits it into
        // `_enchantSinglePreviewContainer` / `_enchantMultiPreviewContainer`. Treat any of them being visible
        // as the preview being open.
        foreach (var containerName in s_deckPreviewContainerNames)
        {
            var previewContainer = Sts2LiveIntrospection.GetMemberValue(screenObject, containerName);
            if (previewContainer is not null && IsVisible(previewContainer))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsVisible(object target)
    {
        return Sts2LiveIntrospection.GetMemberValue(target, "Visible") switch
        {
            bool visible => visible,
            _ => true,
        };
    }

    private static void AddNotice(
        ICollection<StateNoticeSnapshot>? notices,
        string code,
        string message)
    {
        if (notices is null || notices.Any(notice => notice.Code == code))
        {
            return;
        }

        Sts2StateNotice.AddPartialOnce(notices, code, message, "choices", nameof(Sts2CardSelectionScreenInspector));
    }

    private static bool IsSimpleCardSelectionScreen(object? screenObject)
        => Sts2LiveIntrospection.IsType(screenObject, SimpleCardSelectionScreenType);

    internal static bool IsDeckCardSelectionScreen(object? screenObject)
        => Sts2LiveIntrospection.IsType(screenObject, DeckCardSelectionScreenType)
            || Sts2LiveIntrospection.IsType(screenObject, DeckUpgradeSelectionScreenType)
            || Sts2LiveIntrospection.IsType(screenObject, DeckTransformSelectionScreenType)
            || Sts2LiveIntrospection.IsType(screenObject, DeckEnchantSelectionScreenType);

    private static bool IsBundleSelectionScreen(object? screenObject)
        => Sts2LiveIntrospection.IsType(screenObject, BundleSelectionScreenType);
}

internal sealed record ResolvedCardSelectionChoice(
    ChoiceSnapshot Snapshot,
    object Target,
    string? ExecuteMethodName,
    CardSelectionChoiceExecutionKind ExecutionKind,
    object? Alternative,
    bool IsExecutable);

internal enum CardSelectionChoiceExecutionKind
{
    Card,
    SkipButton,
    Alternative,
    Bundle,
}
