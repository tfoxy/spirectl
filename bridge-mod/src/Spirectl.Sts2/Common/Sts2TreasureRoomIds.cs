using System.Globalization;

namespace Spirectl.Sts2;

public static class Sts2TreasureRoomIds
{
    public const string OpenChestChoiceIdValue = "treasure-room:open-chest";
    public const string ProceedChoiceIdValue = "treasure-room:proceed";

    public static string OpenChestChoiceId()
        => OpenChestChoiceIdValue;

    public static string RelicChoiceId(string relicId, int relicIndex)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"treasure-room:relic:{relicId.ToLowerInvariant()}:{relicIndex}");

    public static string ProceedChoiceId()
        => ProceedChoiceIdValue;

    public static bool IsOpenChestChoiceId(string? choiceId)
        => string.Equals(choiceId, OpenChestChoiceIdValue, StringComparison.Ordinal);

    public static bool IsProceedChoiceId(string? choiceId)
        => string.Equals(choiceId, ProceedChoiceIdValue, StringComparison.Ordinal);

    public static bool TryParseRelicChoiceId(
        string? choiceId,
        out string relicId,
        out int relicIndex)
    {
        relicId = string.Empty;
        relicIndex = -1;

        if (string.IsNullOrWhiteSpace(choiceId))
        {
            return false;
        }

        var parts = choiceId.Split(':');
        if (parts.Length != 4
            || !string.Equals(parts[0], "treasure-room", StringComparison.Ordinal)
            || !string.Equals(parts[1], "relic", StringComparison.Ordinal)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out relicIndex))
        {
            return false;
        }

        relicId = parts[2];
        return !string.IsNullOrWhiteSpace(relicId);
    }

    public static bool IsTreasureRoomChoiceId(string? choiceId)
        => IsOpenChestChoiceId(choiceId)
           || IsProceedChoiceId(choiceId)
           || TryParseRelicChoiceId(choiceId, out _, out _);
}
