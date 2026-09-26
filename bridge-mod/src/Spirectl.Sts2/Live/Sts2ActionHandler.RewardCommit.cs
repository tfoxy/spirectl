using System.Collections;
using System.Globalization;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live;

public sealed partial class Sts2ActionHandler
{
    // The members an embedded-profile dispatch arm reaches. The rest of this area is in
    // Sts2ActionHandler.RewardCommit.Full.cs, moved verbatim, which the embedded profile does not compile.

    // Immediate (gold/relic/potion/special-card) reward claim for a browser seat. SelectUnsynchronized runs the
    // reward's own per-player obtain (GainGold / RelicCmd.Obtain /
    // PotionCmd.TryToProcure, keyed on Reward.Player) plus AfterRewardTaken + ParentRewardSet pruning,
    // none of which touch LocalContext or a screen. Undoable kinds (which would open a screen) are
    // rejected — they commit via select-card / confirm-selection instead.
    //
    // That obtain is allowed to REFUSE, and the bool SelectUnsynchronized returns is the only thing that says
    // so, so the row's retire has to wait on it — see ClaimImmediateRewardThenRetire. The synchronous result
    // below is NOT that answer: it reports only that the command was valid and dispatched, which is settled
    // here and now. Whether the seat ends up holding the reward is decided later, on the game's terms.
    private ActionExecutionResult ExecuteRewardSeatClaimCommit(SemanticActionRequest request, string rewardId)
    {
        if (!TryResolveRewardCommit(request, rewardId, out var commit, out var failure))
        {
            return failure!;
        }

        if (IsUndoableReward(commit.Reward))
        {
            return RewardCommitFailure(request, "reward_id", rewardId, "Undoable rewards (card choice / removal) commit via select-card / confirm-selection, not claim-reward.", ActionFailureCode.InvalidAction);
        }

        TaskHelper.RunSafely(ClaimImmediateRewardThenRetire(commit, rewardId));
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:claim-reward:{request.RequestId}",
            kind: request.Kind,
            message: $"Committed reward for {commit.PlayerId}.");
    }

    // Select an immediate reward, then retire its row ONLY if the game says the seat actually received it.
    //
    // A reward can be REFUSED. Taking a potion with no free belt slot is the case that happens in practice, and
    // on screen the refusal is plain: the potion bar plays its no-room animation and the row stays, still
    // claimable once a slot frees up. Reward.SelectUnsynchronized's bool is the only thing that tells this code
    // which of the two outcomes it got, so on false we touch neither the native row nor the captured overlay
    // and the browser lands where a mouse click on the same row lands.
    //
    // Deliberately NOT a capacity pre-check here. Whether an obtain succeeds is a game rule that hooks and
    // relics take part in, so re-deciding it in the bridge would be a second copy of that rule, free to drift.
    // The return value is the authority.
    //
    // Cleanup split, unchanged: SelectUnsynchronized already ran AfterRewardTaken + ParentRewardSet pruning on
    // its own success branch, so FinalizeRewardCommit must NOT be called here — it would double both. Only the
    // row lifecycle and the deck badge are ours.
    private async Task ClaimImmediateRewardThenRetire(RewardCommit commit, string rewardId)
    {
        var received = await commit.Reward.SelectUnsynchronized();
        if (!received)
        {
            _logStream.Write(BridgeLogLevel.Info, "bridge.action", $"The game refused immediate reward '{rewardId}' for seat {commit.PlayerId} (in practice: a potion with no free belt slot); leaving the row claimable.");
            return;
        }

        // SpecialCardReward.SelectUnsynchronized adds a card to the deck, and this headless commit skips the
        // card-fly VFX that normally raises the pile's add-finished signal, so re-raise it for the deck-count
        // badge (no-op for gold/relic/potion). See CommitPileOpThenRefreshDeck.
        RefreshDeckBadge(commit.Player, added: true);
        if (commit.Button is { } button && commit.ScreenObject is { } screen)
        {
            // The direct host-local commit bypassed the button's normal synchronization callback, so retire the
            // row explicitly now that the reward has been received. SelectUnsynchronized already performed
            // reward cleanup; this call is only the live screen's row lifecycle.
            Sts2LiveIntrospection.TryInvokeMethod(screen, "RewardCollectedFrom", button);
        }
        else
        {
            Sts2RewardCaptureRegistry.Consume(commit.PlayerId, commit.Reward);
        }

        _logStream.Write(BridgeLogLevel.Info, "bridge.action", $"Committed immediate reward '{rewardId}' for seat {commit.PlayerId}.");
    }

    private bool IsImmediateLiveReward(string rewardId)
    {
        if (!TryResolveRewardContext(out var rewardContext))
        {
            return false;
        }

        var resolvedId = ResolveRewardChoiceIdAlias(rewardId, rewardContext);
        return rewardContext.ChoicesById.TryGetValue(resolvedId, out var rewardChoice)
            && !rewardChoice.IsFlowChoice
            && Sts2LiveIntrospection.GetMemberValue(rewardChoice.Control, "Reward") is Reward reward
            && !IsUndoableReward(reward);
    }

    // True when a claim-reward for the given player id targets a NON-host (captured) seat — i.e. there is
    // no native NRewardsscreen button for it, so the legacy GetReward path can't apply it.
    private bool IsCapturedSeatReward(string rewardId)
        => TryParseRewardOwnerNetId(rewardId, out var playerId, out _)
            && Sts2RewardIds.TryParseVisibleRewardChoiceId(rewardId, out _, out var visibleIndex)
            && Sts2RewardCaptureRegistry.TryResolve(playerId, visibleIndex, out _, out _);

    private bool TryResolveRewardCommit(
        SemanticActionRequest request,
        string rewardId,
        out RewardCommit commit,
        out ActionExecutionResult? failure)
    {
        commit = default;
        failure = null;

        if (!TryParseRewardOwnerNetId(rewardId, out var playerId, out var seatNetId))
        {
            failure = RewardCommitFailure(request, "reward_id", rewardId, "Expected reward:<player-id>:visible:<index> or reward:<player-id>:<reward-set-index>.", ActionFailureCode.StaleId);
            return false;
        }

        // Host-local "me" seat: the live NRewardsScreen still owns these buttons. Resolve + retire there.
        if (TryResolveRewardContext(out var rewardContext))
        {
            var resolvedId = ResolveRewardChoiceIdAlias(rewardId, rewardContext);
            if (rewardContext.ChoicesById.TryGetValue(resolvedId, out var rewardChoice)
                && !rewardChoice.IsFlowChoice
                && Sts2LiveIntrospection.GetMemberValue(rewardChoice.Control, "Reward") is Reward nativeReward)
            {
                commit = new RewardCommit(nativeReward, nativeReward.Player, playerId, seatNetId, rewardChoice.Control, rewardContext.ScreenObject);
                return true;
            }
        }

        // Non-host browser seat: resolve the captured live Reward and its run Player.
        if (Sts2RewardIds.TryParseVisibleRewardChoiceId(rewardId, out _, out var visibleIndex)
            && Sts2RewardCaptureRegistry.TryResolve(playerId, visibleIndex, out var capturedReward, out var capturedNetId)
            && RunManager.Instance?.DebugOnlyGetState()?.GetPlayer(capturedNetId) is { } capturedPlayer)
        {
            commit = new RewardCommit(capturedReward, capturedPlayer, playerId, capturedNetId, null, null);
            return true;
        }

        failure = RewardCommitFailure(request, "reward_id", rewardId, "The reward is not visible on the active rewards screen or any captured seat.", ActionFailureCode.NotVisible);
        return false;
    }

    // Re-raise the deck pile's add/remove-finished signal so the game's deck-count badge re-reads. Shared
    // by the reward commits and the screen-independent shop card buy (both mutate the deck via CardPileCmd
    // but skip the card-fly VFX that normally raises it).
    internal static void RefreshDeckBadge(Player player, bool added)
    {
        var deck = Sts2LiveIntrospection.GetMemberValue(player, "Deck");
        if (deck is not null)
        {
            Sts2LiveIntrospection.TryInvokeParameterlessMethod(
                deck,
                added ? "InvokeCardAddFinished" : "InvokeCardRemoveFinished",
                "InvokeCardAddFinished");
        }
    }

    private static bool IsUndoableReward(Reward reward)
        => reward is CardReward or CardRemovalReward;

    private ActionExecutionResult RewardCommitFailure(
        SemanticActionRequest request,
        string field,
        string value,
        string note,
        ActionFailureCode code = ActionFailureCode.InvalidAction)
        => ActionExecutionResult.Failure(
            kind: request.Kind,
            code: code,
            message: note,
            details: [new ActionFailureDetail(field, value, note)]);

    private static bool TryParseRewardOwnerNetId(string rewardId, out string playerId, out ulong seatNetId)
    {
        seatNetId = 0;
        if (!Sts2RewardIds.TryParseVisibleRewardChoiceId(rewardId, out playerId, out _)
            && !Sts2RewardIds.TryParseRewardChoiceId(rewardId, out playerId, out _))
        {
            playerId = string.Empty;
            return false;
        }

        return playerId.StartsWith("p:", StringComparison.Ordinal)
            && ulong.TryParse(playerId.AsSpan(2), out seatNetId);
    }

    private readonly record struct RewardCommit(
        Reward Reward,
        Player Player,
        string PlayerId,
        ulong SeatNetId,
        Control? Button,
        object? ScreenObject);
}
