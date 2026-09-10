using System.Globalization;

namespace Spirectl.Sts2;

public static class Sts2CrystalSphereIds
{
    private const string CellPrefix = "crystal-sphere:cell:";

    public static string BigToolChoiceId()
        => "crystal-sphere:tool:big";

    public static string SmallToolChoiceId()
        => "crystal-sphere:tool:small";

    public static string CellChoiceId(int x, int y)
        => string.Create(CultureInfo.InvariantCulture, $"crystal-sphere:cell:{x}:{y}");

    public static string ProceedChoiceId()
        => "crystal-sphere:proceed";

    public static bool IsBigToolChoiceId(string? choiceId)
        => string.Equals(choiceId, BigToolChoiceId(), StringComparison.Ordinal);

    public static bool IsSmallToolChoiceId(string? choiceId)
        => string.Equals(choiceId, SmallToolChoiceId(), StringComparison.Ordinal);

    public static bool IsToolChoiceId(string? choiceId)
        => IsBigToolChoiceId(choiceId) || IsSmallToolChoiceId(choiceId);

    public static bool IsProceedChoiceId(string? choiceId)
        => string.Equals(choiceId, ProceedChoiceId(), StringComparison.Ordinal);

    public static bool TryParseCellChoiceId(string? choiceId, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (string.IsNullOrWhiteSpace(choiceId) || !choiceId.StartsWith(CellPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var coordinateText = choiceId[CellPrefix.Length..];
        var separator = coordinateText.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == coordinateText.Length - 1)
        {
            return false;
        }

        return int.TryParse(coordinateText[..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out x)
            && int.TryParse(coordinateText[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out y);
    }
}
