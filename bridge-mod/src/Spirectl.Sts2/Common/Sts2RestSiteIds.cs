using System.Globalization;

namespace Spirectl.Sts2;

public static class Sts2RestSiteIds
{
    public const string ProceedChoiceIdValue = "rest-site:proceed";

    public static string ChoiceId(string playerId, string optionId, int optionIndex)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"rest-site:{playerId}:{optionId.ToLowerInvariant()}:{optionIndex}");

    public static string ProceedChoiceId()
        => ProceedChoiceIdValue;

    public static bool IsProceedChoiceId(string? choiceId)
        => string.Equals(choiceId, ProceedChoiceIdValue, StringComparison.Ordinal);

    public static bool TryParseOptionChoiceId(
        string? choiceId,
        out string playerId,
        out string optionId,
        out int optionIndex)
    {
        playerId = string.Empty;
        optionId = string.Empty;
        optionIndex = -1;

        if (string.IsNullOrWhiteSpace(choiceId))
        {
            return false;
        }

        var parts = choiceId.Split(':');
        if (parts.Length != 4
            || !string.Equals(parts[0], "rest-site", StringComparison.Ordinal)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out optionIndex))
        {
            return false;
        }

        playerId = parts[1];
        optionId = parts[2];
        return !string.IsNullOrWhiteSpace(playerId)
            && !string.IsNullOrWhiteSpace(optionId);
    }

    public static bool IsRestSiteChoiceId(string? choiceId)
        => IsProceedChoiceId(choiceId)
           || TryParseOptionChoiceId(choiceId, out _, out _, out _);
}
