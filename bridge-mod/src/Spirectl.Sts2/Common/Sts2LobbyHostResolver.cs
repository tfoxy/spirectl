using System.Reflection;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2;

internal static class Sts2LobbyHostResolver
{
    public static string? Resolve(
        object? netService,
        string? localPlayerId,
        ICollection<StateNoticeSnapshot>? notices = null)
    {
        var serviceType = GetMemberValue(netService, "Type")?.ToString();
        if (string.Equals(serviceType, "Host", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(localPlayerId))
        {
            return localPlayerId;
        }

        if (!string.Equals(serviceType, "Client", StringComparison.Ordinal))
        {
            return null;
        }

        var hostNetId = NormalizeNetId(GetMemberValue(netService, "HostNetId"));
        if (hostNetId.HasValue)
        {
            return $"p:{hostNetId.Value}";
        }

        AddUnavailableNotice(notices);
        return null;
    }

    public static string? ResolveRunHostPlayerId(
        object? netService,
        string? localPlayerId,
        ICollection<StateNoticeSnapshot>? notices = null)
        => Resolve(netService, localPlayerId, notices);

    private static object? GetMemberValue(object? target, string memberName)
    {
        if (target is null)
        {
            return null;
        }

        var type = target.GetType();
        return type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target)
            ?? type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target);
    }

    private static ulong? NormalizeNetId(object? value)
    {
        return value switch
        {
            ulong hostNetId when hostNetId > 0 => hostNetId,
            long hostNetId when hostNetId > 0 => (ulong)hostNetId,
            int hostNetId when hostNetId > 0 => (ulong)hostNetId,
            uint hostNetId when hostNetId > 0 => hostNetId,
            string hostNetId when ulong.TryParse(hostNetId, out var parsed) && parsed > 0 => parsed,
            _ => null,
        };
    }

    private static void AddUnavailableNotice(ICollection<StateNoticeSnapshot>? notices)
    {
        if (notices is null || notices.Any(notice => notice.Code == "lobby-host-player-unknown"))
        {
            return;
        }

        notices.Add(Sts2StateNotice.Partial(
            "lobby-host-player-unknown",
            "The active client net service did not expose a remote host player id.",
            "lobby.hostPlayerId",
            nameof(Sts2LobbyHostResolver)));
    }
}
