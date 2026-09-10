using System.Globalization;

namespace Spirectl.Sts2;

public static class Sts2MapIds
{
    public static string BackChoiceId()
        => "map:back";

    public static bool IsBackChoiceId(string? choiceId)
        => string.Equals(choiceId, BackChoiceId(), StringComparison.Ordinal);

    public static string NodeId(int row, int col)
        => string.Create(CultureInfo.InvariantCulture, $"map-node:{row}:{col}");

    public static bool TryParseNodeId(string? nodeId, out int row, out int col)
    {
        row = 0;
        col = 0;

        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return false;
        }

        var parts = nodeId.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 3
            || !string.Equals(parts[0], "map-node", StringComparison.Ordinal)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out row)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out col))
        {
            row = 0;
            col = 0;
            return false;
        }

        return true;
    }
}
