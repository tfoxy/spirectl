using System.Collections;
using System.Globalization;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live;

public sealed partial class Sts2ActionHandler
{
    // Full profile only: the members of Sts2ActionHandler.RewardCommit.cs that no embedded dispatch arm
    // reaches, moved here verbatim so the embedded profile can leave them out (see Sts2Profile in the project file).

    // === Per-player reward COMMIT (couch-coop: one host engine, browser-only non-host seats) ===
    //
    // Phase 1 made undoable rewards (cardReward choice / cardRemoval) open a per-player CLIENT overlay
    // (run.view.rewardSelection, sendToHost:false) instead of driving the native NRewardButton.GetReward
    // (which opens an interactive sub-screen we cannot feed headlessly). Commit the client's pick directly
    // on the host engine, keyed on the reward's OWNING player — the game reward API is per-player.
    //
    // Reward source differs by seat, but the commit is uniform:
    //   * HOST-local "me" seat  -> the live NRewardsScreen buttons (Reward + retire button via RewardCollectedFrom).
    //   * non-host browser seat -> Sts2RewardCaptureRegistry (captured RewardsSet; drop from the surfaced overlay).
    // Commit primitives match the game's own local-pick / remote-apply: choice = CardPileCmd.Add(card, Deck)
    // (NOT RunState.AddCard — the offered model is immutable, AssertMutable would throw); removal =
    // CardPileCmd.RemoveFromDeck(card); immediate (gold/relic/potion/special) = Reward.SelectUnsynchronized()
    // (non-interactive + does its own cleanup). Cleanup mirrors Reward.SelectUnsynchronized: Hook.AfterRewardTaken
    // + ParentRewardSet pruning. Mirrors ExecuteHostLocalSeatEventRoomIntent.

    private ActionExecutionResult ExecuteRewardSeatChoiceCommit(
        SemanticActionRequest request,
        string rewardId,
        string cardId)
    {
        if (!TryResolveRewardCommit(request, rewardId, out var commit, out var failure))
        {
            return failure!;
        }

        if (!TryParseRewardOfferCardIndex(cardId, out var offerIndex))
        {
            return RewardCommitFailure(request, "card_id", cardId, "Expected a reward choice card id shaped like '<reward-id>:card:<index>'.");
        }

        var offered = EnumerateRewardObjects(Sts2LiveIntrospection.GetMemberValue(commit.Reward, "Cards"))
            .OfType<CardModel>()
            .ToArray();
        if (offerIndex < 0 || offerIndex >= offered.Length)
        {
            return RewardCommitFailure(request, "card_id", cardId, $"Reward choice card index {offerIndex} is out of range (the reward offered {offered.Length} cards).");
        }

        // CardReward.OnSelect's local-pick primitive: CardPileCmd.Add owns registration + mutability.
        // Re-raise the deck pile's add-finished signal afterwards so the game's deck-count badge reacts
        // (the headless commit skips the card-fly VFX that normally raises it). See CommitPileOpThenRefreshDeck.
        TaskHelper.RunSafely(CommitPileOpThenRefreshDeck(CardPileCmd.Add(offered[offerIndex], PileType.Deck), commit.Player, added: true));
        FinalizeRewardCommit(commit);
        _logStream.Write(BridgeLogLevel.Info, "bridge.action", $"Committed reward card choice '{rewardId}' (offer {offerIndex}) for seat {commit.PlayerId}.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:select-card:{request.RequestId}",
            kind: request.Kind,
            message: $"Committed reward card choice for {commit.PlayerId}.");
    }

    private ActionExecutionResult ExecuteRewardSeatRemovalCommit(
        SemanticActionRequest request,
        string rewardId,
        IReadOnlyList<string> cardIds)
    {
        if (!TryResolveRewardCommit(request, rewardId, out var commit, out var failure))
        {
            return failure!;
        }

        var cardId = cardIds.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id))?.Trim();
        if (string.IsNullOrWhiteSpace(cardId) || !TryParseDeckCardIndex(cardId, out var deckIndex))
        {
            return RewardCommitFailure(request, "card_id", cardId ?? string.Empty, "card-removal commit requires one deck card id shaped like 'card:<player-id>:deck:<index>'.");
        }

        // Enumerate the owning player's deck in the same order the state provider projects
        // run.players[].deck.cards (card:<player-id>:deck:<index>), so the client index resolves
        // to the same CardModel the browser showed.
        var deck = Sts2LiveIntrospection.GetMemberValue(commit.Player, "Deck");
        var deckCards = EnumerateRewardObjects(Sts2LiveIntrospection.GetMemberValue(deck, "Cards"))
            .OfType<CardModel>()
            .ToArray();
        if (deckIndex < 0 || deckIndex >= deckCards.Length)
        {
            return RewardCommitFailure(request, "card_id", cardId, $"Deck card index {deckIndex} is out of range (the seat's deck has {deckCards.Length} cards).");
        }

        // The non-interactive removal primitive (NOT DoCardRemoval, which opens a picker). Re-raise the
        // deck pile's remove-finished signal afterwards so the game's deck-count badge reacts.
        TaskHelper.RunSafely(CommitPileOpThenRefreshDeck(CardPileCmd.RemoveFromDeck(deckCards[deckIndex]), commit.Player, added: false));
        FinalizeRewardCommit(commit);
        _logStream.Write(BridgeLogLevel.Info, "bridge.action", $"Committed card removal '{rewardId}' (deck index {deckIndex}) for seat {commit.PlayerId}.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:confirm-selection:{request.RequestId}",
            kind: request.Kind,
            message: $"Committed card removal for {commit.PlayerId}.");
    }

    // Replicates Reward.SelectUnsynchronized's success branch (Hook.AfterRewardTaken + ParentRewardSet pruning;
    // LinkedRewardSet.OnSelect is a no-op so we only prune), then drops the reward from its source overlay.
    private void FinalizeRewardCommit(RewardCommit commit)
    {
        TaskHelper.RunSafely(Hook.AfterRewardTaken(commit.Player.RunState, commit.Player, commit.Reward));
        commit.Reward.ParentRewardSet?.RemoveReward(commit.Reward);
        if (commit.Button is { } button && commit.ScreenObject is { } screen)
        {
            // Host seat: retire the native button so NRewardsScreen state drops the reward + Proceed re-enables.
            Sts2LiveIntrospection.TryInvokeMethod(screen, "RewardCollectedFrom", button);
        }
        else
        {
            // Non-host seat: drop from the captured overlay (re-indexes the remaining rewards).
            Sts2RewardCaptureRegistry.Consume(commit.PlayerId, commit.Reward);
        }
    }

    // The headless commit mutates the deck pile via CardPileCmd but skips CardReward.OnSelect's card-fly
    // VFX (NCardFlyVfx), which is the only thing that raises CardPile.CardAddFinished — the signal the
    // game's deck-count badge (NTopBarDeckButton.OnPileContentsChanged) listens to. Re-raise it once the
    // pile op completes so the game UI reacts (the deck-icon count) without us forcing any reward overlay
    // open. CardAddFinished and CardRemoveFinished route to the same handler, so re-raising is just an
    // idempotent re-read.
    //
    // The two undoable commits (choice / removal) use this; the immediate claim does not, because it has to
    // read the bool its own op returns rather than discard it into this Task parameter — it awaits and calls
    // RefreshDeckBadge itself. See ClaimImmediateRewardThenRetire.
    private static async Task CommitPileOpThenRefreshDeck(Task pileOp, Player player, bool added)
    {
        await pileOp;
        RefreshDeckBadge(player, added);
    }

    // reward:<player-id>:visible:<n>:card:<index> (or ...:<reward-set-index>:card:<index>)
    private static bool TryParseRewardOfferCardIndex(string cardId, out int index)
    {
        index = -1;
        var parts = cardId.Split(':');
        return parts.Length >= 2
            && string.Equals(parts[^2], "card", StringComparison.Ordinal)
            && int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out index);
    }

    // card:<player-id>:deck:<index> (the run.players[].deck.cards id scheme)
    private static bool TryParseDeckCardIndex(string id, out int index)
    {
        index = -1;
        var parts = id.Split(':');
        return parts.Length >= 4
            && string.Equals(parts[^2], "deck", StringComparison.Ordinal)
            && int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out index);
    }

    private static IEnumerable<object?> EnumerateRewardObjects(object? value)
    {
        if (value is IEnumerable enumerable and not string)
        {
            foreach (var item in enumerable)
            {
                yield return item;
            }
        }
    }
}
