namespace Spirectl.Sts2.Live;

/// <summary>Resolves a streamed scene-node instance id to the stable reward choice it currently represents.</summary>
internal static class Sts2RewardElementChoice
{
    internal static string? Resolve(
        string? elementId,
        IEnumerable<(ulong InstanceId, string ChoiceId)> choices)
    {
        if (!ulong.TryParse(
                elementId,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var requested))
        {
            return null;
        }

        foreach (var (instanceId, choiceId) in choices)
        {
            if (instanceId == requested)
            {
                return choiceId;
            }
        }

        return null;
    }
}
