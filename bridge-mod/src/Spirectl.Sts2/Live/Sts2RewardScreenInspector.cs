using System.Collections;
using Godot;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live;

internal static class Sts2RewardScreenInspector
{
    public static IReadOnlyList<ResolvedRewardChoice> ResolveChoices(
        object rewardScreen,
        string? defaultPlayerId,
        ICollection<StateNoticeSnapshot>? notices = null)
    {
        var choices = new List<ResolvedRewardChoice>();
        var seenChoiceIds = new HashSet<string>(StringComparer.Ordinal);
        var rewardButtons = Sts2LiveIntrospection.GetMemberValue(rewardScreen, "_rewardButtons") as IEnumerable;
        var index = 0;
        foreach (var entry in rewardButtons ?? Array.Empty<object>())
        {
            // NOTE: do NOT gate on control.Visible here. In the headless/browser render the live reward
            // buttons report Visible==false even when the rewards screen is logically active and handing
            // out claimable items, so gating on it dropped EVERY reward (gold/card/relic/potion) and left
            // only the Proceed button — the bot then "proceeded" past combats without taking any reward.
            // Same headless wall as the treasure relic holder / rest-site options / card-selection grid;
            // surface the item by structure (a Reward exists) and let the live claim hook arbitrate.
            if (entry is not Control control)
            {
                index++;
                continue;
            }

            var reward = Sts2LiveIntrospection.GetMemberValue(control, "Reward")
                ?? Sts2LiveIntrospection.GetMemberValue(control, "LinkedRewardSet");
            if (reward is null)
            {
                index++;
                continue;
            }

            var ownerPlayerId = ResolveOwnerPlayerId(reward, defaultPlayerId, notices);
            var choiceId = ResolveChoiceId(reward, ownerPlayerId, index, notices);
            if (!seenChoiceIds.Add(choiceId))
            {
                index++;
                continue;
            }

            choices.Add(new ResolvedRewardChoice(
                CreateRewardChoiceSnapshot(
                    choiceId,
                    ResolveLabel(reward),
                    ownerPlayerId,
                    ResolveIsExecutable(control)),
                control,
                ownerPlayerId,
                IsExecutable: ResolveIsExecutable(control),
                RewardKind: ResolveRewardKind(reward),
                RewardId: ResolveRewardId(reward),
                DisplayIndex: index,
                IsFlowChoice: false,
                PreferredAction: "claim-reward"));
            index++;
        }

        if (TryResolveFlowChoice(rewardScreen, defaultPlayerId, out var flowChoice)
            && seenChoiceIds.Add(flowChoice.Snapshot.Id))
        {
            choices.Add(flowChoice);
        }

        return choices;
    }

    private static ChoiceSnapshot CreateRewardChoiceSnapshot(
        string choiceId,
        string label,
        string ownerPlayerId,
        bool enabled)
    {
        var arguments = new ActionArgumentsSnapshot(
            ownerPlayerId,
            null,
            null,
            null,
            null,
            null,
            IntentKind: "claim-reward",
            Values: new Dictionary<string, string> { ["rewardId"] = choiceId });
        return new ChoiceSnapshot(
                    Id: choiceId,
                    Label: label,
                    Kind: "reward",
                    Provisional: false,
                    OwnerPlayerId: ownerPlayerId,
                    ChoiceKind: "reward",
                    IntentKind: "claim-reward",
                    Perspective: ResolvePerspective(ownerPlayerId),
                    PreferredAction: "claim-reward",
                    Arguments: arguments,
                    Enabled: enabled,
                    DisabledReason: enabled ? null : "not-enabled",
                    PreferredActionRef: enabled
                        ? new VisibleActionReferenceSnapshot(
                            Sts2ActionIds.Intent("rewards", "claim-reward", choiceId),
                            $"Claim {label}.",
                            Enabled: true,
                            OwnerPlayerId: ownerPlayerId,
                            ActionKind: SemanticActionKind.ClaimReward,
                            IntentKind: "claim-reward",
                            Arguments: arguments,
                            LegalityStatus: ActionLegalityKind.Legal,
                            Perspective: ResolvePerspective(ownerPlayerId))
                        : null,
                    LegalityStatus: enabled ? ActionLegalityKind.Legal : ActionLegalityKind.Illegal,
                    CheckedHookPaths: ["rewardButton.GetReward"]);
    }

    private static bool TryResolveFlowChoice(
        object rewardScreen,
        string? defaultPlayerId,
        out ResolvedRewardChoice choice)
    {
        choice = null!;

        // Do NOT gate on control.Visible / IsEnabled here. In the headless/browser render the live proceed
        // button reports Visible/IsEnabled==false even while the rewards screen is active and waiting for
        // Proceed — especially right after the last reward is claimed — so this gate dropped the flow choice
        // and skip-rewards execution failed ("not visible"), wedging the run at the first combat (the
        // rewards screen stayed open, the browser showed the map underneath, and map clicks were swallowed).
        // Surface the flow whenever the proceed button exists; the live OnProceedButtonPressed hook arbitrates.
        if (Sts2LiveIntrospection.GetMemberValue(rewardScreen, "_proceedButton") is not Control control)
        {
            return false;
        }

        var isSkip = Sts2LiveIntrospection.GetMemberValue(control, "IsSkip") is bool skip && skip;
        var id = Sts2RewardIds.FlowChoiceId(isSkip);
        var label = isSkip ? "Skip Rewards" : "Proceed";
        var arguments = new ActionArgumentsSnapshot(
            defaultPlayerId,
            null,
            null,
            null,
            null,
            null,
            IntentKind: "skip-rewards");
        choice = new ResolvedRewardChoice(
            new ChoiceSnapshot(
                Id: id,
                Label: label,
                Kind: "reward-flow",
                Provisional: false,
                OwnerPlayerId: defaultPlayerId,
                ChoiceKind: "reward-flow",
                IntentKind: "skip-rewards",
                Perspective: ResolvePerspective(defaultPlayerId),
                PreferredAction: "skip-rewards",
                Arguments: arguments,
                PreferredActionRef: new VisibleActionReferenceSnapshot(
                    Sts2ActionIds.Intent("rewards", "skip-rewards", id),
                    label,
                    Enabled: true,
                    OwnerPlayerId: defaultPlayerId,
                    ActionKind: SemanticActionKind.SkipRewards,
                    IntentKind: "skip-rewards",
                    Arguments: arguments,
                    LegalityStatus: ActionLegalityKind.Legal,
                    Perspective: ResolvePerspective(defaultPlayerId)),
                LegalityStatus: ActionLegalityKind.Legal,
                CheckedHookPaths: ["rewardsScreen.OnProceedButtonPressed"]),
            control,
            defaultPlayerId,
            IsExecutable: true,
            RewardKind: isSkip ? "skip" : "proceed",
            RewardId: null,
            DisplayIndex: null,
            IsFlowChoice: true,
            PreferredAction: "skip-rewards");
        return true;
    }

    private static string? ResolvePerspective(string? ownerPlayerId)
        => string.IsNullOrWhiteSpace(ownerPlayerId) ? null : "local";

    private static string ResolveRewardKind(object reward)
    {
        var rewardType = Normalize(Sts2LiveIntrospection.GetMemberValue(reward, "RewardType")?.ToString());
        return string.IsNullOrWhiteSpace(rewardType) ? "reward" : rewardType!.ToLowerInvariant();
    }

    private static string? ResolveRewardId(object reward)
        => ResolveModelId(Sts2LiveIntrospection.GetMemberValue(reward, "Id"))
            ?? ResolveModelId(Sts2LiveIntrospection.GetMemberValue(reward, "Model"))
            ?? ResolveModelId(Sts2LiveIntrospection.GetMemberValue(reward, "RewardId"));

    private static bool ResolveIsExecutable(object control, bool defaultValue = true)
    {
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

    private static string ResolveOwnerPlayerId(
        object reward,
        string? defaultPlayerId,
        ICollection<StateNoticeSnapshot>? notices)
    {
        if (Sts2LiveIntrospection.GetMemberValue(reward, "Player") is { } player)
        {
            return Sts2CombatIds.PlayerId(player);
        }

        if (!string.IsNullOrWhiteSpace(defaultPlayerId))
        {
            AddNotice(
                notices,
                "reward-choice-player-fallback",
                "Reward choice owners fall back to the local player id when the reward model does not expose a player.");
            return defaultPlayerId!;
        }

        AddNotice(
            notices,
            "reward-choice-player-fallback",
            "Reward choice owners fall back to an unknown player id when the reward model does not expose a player.");
        return "p:unknown";
    }

    private static string ResolveChoiceId(
        object reward,
        string ownerPlayerId,
        int fallbackIndex,
        ICollection<StateNoticeSnapshot>? notices)
    {
        var rewardsSetIndex = TryResolveRewardsSetIndex(reward);
        if (rewardsSetIndex.HasValue)
        {
            return Sts2RewardIds.ChoiceId(ownerPlayerId, rewardsSetIndex.Value);
        }

        AddNotice(
            notices,
            "reward-choice-id-fallback",
            "Reward choice ids fall back to screen-instance ordering when reward set indexes are unavailable.");
        return Sts2RewardIds.ChoiceId(ownerPlayerId, fallbackIndex);
    }

    private static int? TryResolveRewardsSetIndex(object reward)
    {
        return Sts2LiveIntrospection.GetMemberValue(reward, "RewardsSetIndex") switch
        {
            int value => value,
            long value when value is >= int.MinValue and <= int.MaxValue => (int)value,
            short value => value,
            byte value => value,
            string value when int.TryParse(value, out var parsed) => parsed,
            _ => null,
        };
    }

    private static string ResolveLabel(object reward)
    {
        var description = Normalize(Sts2LiveIntrospection.GetMemberValue(reward, "Description")?.ToString());
        if (!string.IsNullOrWhiteSpace(description))
        {
            return description;
        }

        var rewardType = Normalize(Sts2LiveIntrospection.GetMemberValue(reward, "RewardType")?.ToString());
        if (!string.IsNullOrWhiteSpace(rewardType) && !string.Equals(rewardType, "None", StringComparison.Ordinal))
        {
            return $"{rewardType} Reward";
        }

        return reward.GetType().Name;
    }

    private static string? ResolveModelId(object? model)
    {
        if (model is null)
        {
            return null;
        }

        var entry = Normalize(Sts2LiveIntrospection.GetMemberValue(model, "Entry")?.ToString());
        if (!string.IsNullOrWhiteSpace(entry))
        {
            return entry;
        }

        return Normalize(model.ToString());
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.ReplaceLineEndings(" ").Trim();

    private static void AddNotice(
        ICollection<StateNoticeSnapshot>? notices,
        string code,
        string message)
    {
        if (notices is null || notices.Any(notice => notice.Code == code))
        {
            return;
        }

        Sts2StateNotice.AddPartialOnce(notices, code, message, "choices", nameof(Sts2RewardScreenInspector));
    }
}

internal sealed record ResolvedRewardChoice(
    ChoiceSnapshot Snapshot,
    Control Control,
    string? PlayerId,
    bool IsExecutable,
    string RewardKind,
    string? RewardId,
    int? DisplayIndex,
    bool IsFlowChoice,
    string PreferredAction);
