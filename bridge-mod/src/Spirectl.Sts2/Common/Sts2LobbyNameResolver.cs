using System.Reflection;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2;

internal static class Sts2LobbyNameResolver
{
    public static string? Resolve(
        object? platform,
        ulong playerId,
        ICollection<StateNoticeSnapshot> notices,
        Func<object?, ulong, string?>? lookup = null)
    {
        try
        {
            var syntheticName = Sts2HostLocalSeatRegistry.ResolveSyntheticName(playerId);
            if (!string.IsNullOrWhiteSpace(syntheticName))
            {
                return syntheticName;
            }

            var candidate = lookup is not null
                ? lookup(platform, playerId)
                : InvokePlatformLookup(platform, playerId);
            var name = Normalize(candidate);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }
        catch
        {
            // Fall through to the shared notice path when platform lookups fail.
        }

        AddUnavailableNotice(notices);
        return null;
    }

    private static string? InvokePlatformLookup(object? platform, ulong playerId)
    {
        if (platform is null)
        {
            return null;
        }

        var platformType = platform.GetType();
        var platformUtilType = platformType.Assembly.GetType("MegaCrit.Sts2.Core.Platform.PlatformUtil");
        var method = platformUtilType?.GetMethod(
            "GetPlayerName",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            [platformType, typeof(ulong)],
            modifiers: null);
        return method?.Invoke(null, [platform, playerId]) as string;
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.ReplaceLineEndings(" ").Trim();

    private static void AddUnavailableNotice(ICollection<StateNoticeSnapshot> notices)
    {
        if (notices.Any(notice => notice.Code == "lobby-player-names-unavailable"))
        {
            return;
        }

        notices.Add(Sts2StateNotice.Partial(
            "lobby-player-names-unavailable",
            "One or more lobby player display names could not be resolved from platform metadata.",
            "lobby.players.name",
            nameof(Sts2LobbyNameResolver)));
    }
}
