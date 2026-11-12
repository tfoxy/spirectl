using Spirectl.Sts2.Core.Actions;

namespace Spirectl.Sts2;

public static class Sts2ActionFailureClassifier
{
    public static ActionFailureCode InferLegalityFailureCode(string field, string note)
    {
        if (string.Equals(field, "player_id", StringComparison.Ordinal))
        {
            return ActionFailureCode.WrongPlayer;
        }

        if (string.Equals(field, "screen", StringComparison.Ordinal)
            || note.Contains("state.screen.id", StringComparison.OrdinalIgnoreCase)
            || note.Contains("screen=", StringComparison.OrdinalIgnoreCase))
        {
            return ActionFailureCode.WrongScreen;
        }

        if (note.Contains("current target ids", StringComparison.OrdinalIgnoreCase)
            || note.Contains("currently valid targets", StringComparison.OrdinalIgnoreCase)
            || note.Contains("currently playable", StringComparison.OrdinalIgnoreCase)
            || note.Contains("currently usable", StringComparison.OrdinalIgnoreCase)
            || note.Contains("currently executable", StringComparison.OrdinalIgnoreCase)
            || note.Contains("currently travelable", StringComparison.OrdinalIgnoreCase)
            || note.Contains("not already queued", StringComparison.OrdinalIgnoreCase)
            || note.Contains("does not currently have enough gold", StringComparison.OrdinalIgnoreCase))
        {
            return ActionFailureCode.NotEnabled;
        }

        if (note.Contains("no longer", StringComparison.OrdinalIgnoreCase)
            || note.Contains("state likely changed", StringComparison.OrdinalIgnoreCase))
        {
            return ActionFailureCode.StaleId;
        }

        if (note.Contains("visible", StringComparison.OrdinalIgnoreCase)
            || (!string.Equals(field, "node_id", StringComparison.Ordinal)
                && note.Contains("stable", StringComparison.OrdinalIgnoreCase)))
        {
            return ActionFailureCode.NotVisible;
        }

        return ActionFailureCode.InvalidAction;
    }
}
