using System.Globalization;

namespace Spirectl.Sts2;

public static class Sts2RewardIds
{
    public const string ProceedFlowChoiceId = "reward-flow:proceed";
    public const string SkipFlowChoiceId = "reward-flow:skip";
    private const string RewardPrefix = "reward:";

    public static string ChoiceId(string playerId, int rewardsSetIndex)
        => string.Create(CultureInfo.InvariantCulture, $"reward:{playerId}:{rewardsSetIndex}");

    public static string VisibleChoiceId(string playerId, int visibleIndex)
        => string.Create(CultureInfo.InvariantCulture, $"reward:{playerId}:visible:{visibleIndex}");

    public static string FlowChoiceId(bool isSkip)
        => isSkip ? SkipFlowChoiceId : ProceedFlowChoiceId;

    public static bool IsFlowChoiceId(string? choiceId)
        => string.Equals(choiceId, ProceedFlowChoiceId, StringComparison.Ordinal)
            || string.Equals(choiceId, SkipFlowChoiceId, StringComparison.Ordinal);

    public static bool TryParseRewardChoiceId(string? choiceId, out string playerId, out int rewardsSetIndex)
    {
        playerId = string.Empty;
        rewardsSetIndex = -1;
        if (string.IsNullOrWhiteSpace(choiceId) || !choiceId.StartsWith(RewardPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var lastSeparator = choiceId.LastIndexOf(':');
        if (lastSeparator <= RewardPrefix.Length
            || !int.TryParse(choiceId[(lastSeparator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out rewardsSetIndex))
        {
            return false;
        }

        playerId = choiceId[RewardPrefix.Length..lastSeparator];
        return !string.IsNullOrWhiteSpace(playerId);
    }

    public static bool TryParseVisibleRewardChoiceId(string? choiceId, out string playerId, out int visibleIndex)
    {
        playerId = string.Empty;
        visibleIndex = -1;
        if (string.IsNullOrWhiteSpace(choiceId) || !choiceId.StartsWith(RewardPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var visibleSegment = choiceId.LastIndexOf(":visible:", StringComparison.Ordinal);
        if (visibleSegment <= RewardPrefix.Length
            || !int.TryParse(choiceId[(visibleSegment + ":visible:".Length)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out visibleIndex))
        {
            return false;
        }

        playerId = choiceId[RewardPrefix.Length..visibleSegment];
        return !string.IsNullOrWhiteSpace(playerId);
    }

    public static bool TryParseFlowChoiceId(string? choiceId, out bool isSkip)
    {
        isSkip = false;
        if (string.Equals(choiceId, SkipFlowChoiceId, StringComparison.Ordinal))
        {
            isSkip = true;
            return true;
        }

        return string.Equals(choiceId, ProceedFlowChoiceId, StringComparison.Ordinal);
    }
}
