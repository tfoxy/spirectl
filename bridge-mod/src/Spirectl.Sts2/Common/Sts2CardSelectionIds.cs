using System.Globalization;

namespace Spirectl.Sts2;

public static class Sts2CardSelectionIds
{
    public const string SkipChoiceIdValue = "card-selection:skip";
    private const string CardPrefix = "card-selection:card:";
    private const string BundlePrefix = "card-selection:bundle:";
    private const string AlternativePrefix = "card-selection:alternative:";

    public static string CardChoiceId(string stableCardId, int optionIndex)
        => string.Create(CultureInfo.InvariantCulture, $"card-selection:card:{stableCardId}:{optionIndex}");

    public static string BundleChoiceId(string stableBundleId, int optionIndex)
        => string.Create(CultureInfo.InvariantCulture, $"card-selection:bundle:{stableBundleId}:{optionIndex}");

    public static string SkipChoiceId()
        => SkipChoiceIdValue;

    public static string AlternativeChoiceId(string optionId, int optionIndex)
        => string.Create(CultureInfo.InvariantCulture, $"card-selection:alternative:{optionId}:{optionIndex}");

    public static bool TryParseCardChoiceId(string? choiceId, out string stableCardId, out int optionIndex)
        => TryParseIndexedId(choiceId, CardPrefix, out stableCardId, out optionIndex);

    public static bool TryParseBundleChoiceId(string? choiceId, out string stableBundleId, out int optionIndex)
        => TryParseIndexedId(choiceId, BundlePrefix, out stableBundleId, out optionIndex);

    public static bool TryParseAlternativeChoiceId(string? choiceId, out string optionId, out int optionIndex)
        => TryParseIndexedId(choiceId, AlternativePrefix, out optionId, out optionIndex);

    public static bool IsSkipChoiceId(string? choiceId)
        => string.Equals(choiceId, SkipChoiceIdValue, StringComparison.Ordinal);

    private static bool TryParseIndexedId(string? choiceId, string prefix, out string stableId, out int optionIndex)
    {
        stableId = string.Empty;
        optionIndex = -1;
        if (string.IsNullOrWhiteSpace(choiceId) || !choiceId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var lastSeparator = choiceId.LastIndexOf(':');
        if (lastSeparator <= prefix.Length
            || !int.TryParse(choiceId[(lastSeparator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out optionIndex))
        {
            return false;
        }

        stableId = choiceId[prefix.Length..lastSeparator];
        return !string.IsNullOrWhiteSpace(stableId);
    }
}
