using System.Globalization;
using System.Text;

namespace Spirectl.Sts2;

public static class Sts2EventRoomIds
{
    public enum OptionIdFallbackKind
    {
        None,
        Label,
        Order,
    }

    public static string FakeMerchantOpenShopChoiceId()
        => "event-room:fake-merchant:open-shop";

    public static string ProceedChoiceId()
        => "event-room:proceed";

    public static string ChoiceId(string optionId, int optionIndex)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"event-room:{optionId.ToLowerInvariant()}:{optionIndex}");

    public static string ResolveOptionId(
        string? textKey,
        bool isProceed,
        string? labelFallback,
        int optionIndex,
        out OptionIdFallbackKind fallbackKind)
    {
        fallbackKind = OptionIdFallbackKind.None;
        var slug = Slugify(Normalize(textKey));
        if (!string.IsNullOrWhiteSpace(slug))
        {
            return slug!;
        }

        if (isProceed)
        {
            return "proceed";
        }

        slug = Slugify(Normalize(labelFallback));
        if (!string.IsNullOrWhiteSpace(slug))
        {
            fallbackKind = OptionIdFallbackKind.Label;
            return slug!;
        }

        fallbackKind = OptionIdFallbackKind.Order;
        return $"option-{optionIndex}";
    }

    public static bool IsFakeMerchantOpenShopChoiceId(string? choiceId)
        => string.Equals(choiceId, FakeMerchantOpenShopChoiceId(), StringComparison.Ordinal);

    public static bool IsProceedChoiceId(string? choiceId)
        => string.Equals(choiceId, ProceedChoiceId(), StringComparison.Ordinal);

    public static bool TryParseChoiceId(string? choiceId, out string optionId, out int optionIndex)
    {
        optionId = string.Empty;
        optionIndex = -1;
        if (string.IsNullOrWhiteSpace(choiceId))
        {
            return false;
        }

        var parts = choiceId.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 3
            || !string.Equals(parts[0], "event-room", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(parts[1])
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out optionIndex))
        {
            optionIndex = -1;
            return false;
        }

        optionId = parts[1];
        return true;
    }

    private static string? Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        var previousWasSeparator = false;
        foreach (var ch in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                previousWasSeparator = false;
                continue;
            }

            if (ch is '-' or '_')
            {
                builder.Append(ch);
                previousWasSeparator = false;
                continue;
            }

            if (previousWasSeparator)
            {
                continue;
            }

            builder.Append('-');
            previousWasSeparator = true;
        }

        var normalized = builder.ToString().Trim('-', '_');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.ReplaceLineEndings(" ").Trim();
}
