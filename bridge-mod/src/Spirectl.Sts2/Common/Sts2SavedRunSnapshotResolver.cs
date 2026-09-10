using System.Collections;
using System.Reflection;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2;

internal static class Sts2SavedRunSnapshotResolver
{
    public static RunStateSnapshot? Resolve(object? savedRun)
    {
        if (savedRun is null)
        {
            return null;
        }

        var players = ResolvePlayers(savedRun);
        return new RunStateSnapshot(
            Seed: ResolveSeed(savedRun),
            Floor: ResolveFloor(savedRun),
            Act: ResolveAct(savedRun),
            Players: players,
            PlayersById: players
                .GroupBy(player => player.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal));
    }

    private static IReadOnlyList<PlayerStateSnapshot> ResolvePlayers(object savedRun)
    {
        if (GetMemberValue(savedRun, "Players") is not IEnumerable players)
        {
            return [];
        }

        var snapshots = new List<PlayerStateSnapshot>();
        var index = 0;
        foreach (var player in players)
        {
            snapshots.Add(new PlayerStateSnapshot(
                Id: ResolvePlayerId(player, index),
                Character: ResolveCharacterId(player),
                Hp: ResolveInt(GetMemberValue(player, "CurrentHp")) ?? 0,
                MaxHp: ResolveInt(GetMemberValue(player, "MaxHp")) ?? 0));
            index++;
        }

        return snapshots;
    }

    private static string ResolveSeed(object savedRun)
        => Normalize(GetMemberValue(GetMemberValue(savedRun, "SerializableRng"), "Seed")?.ToString()) ?? "unknown";

    private static int ResolveFloor(object savedRun)
    {
        if (GetMemberValue(savedRun, "MapPointHistory") is not IEnumerable histories)
        {
            return 0;
        }

        var total = 0;
        foreach (var history in histories)
        {
            if (history is IEnumerable entries)
            {
                total += entries.Cast<object?>().Count();
            }
        }

        return total;
    }

    private static int ResolveAct(object savedRun)
    {
        var actIndex = ResolveInt(GetMemberValue(savedRun, "CurrentActIndex")) ?? 0;
        return actIndex + 1;
    }

    private static string ResolvePlayerId(object? player, int index)
    {
        var netId = ResolveULong(GetMemberValue(player, "NetId"));
        return netId.HasValue ? $"p:{netId.Value}" : $"p:unknown:{index}";
    }

    private static string ResolveCharacterId(object? player)
        => Normalize(GetMemberValue(GetMemberValue(player, "CharacterId"), "Entry")?.ToString()) ?? "unknown";

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? ResolveInt(object? value)
    {
        return value switch
        {
            int intValue => intValue,
            uint uintValue when uintValue <= int.MaxValue => (int)uintValue,
            long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
            ulong ulongValue when ulongValue <= int.MaxValue => (int)ulongValue,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => null,
        };
    }

    private static ulong? ResolveULong(object? value)
    {
        return value switch
        {
            ulong ulongValue when ulongValue > 0 => ulongValue,
            uint uintValue when uintValue > 0 => uintValue,
            int intValue when intValue > 0 => (ulong)intValue,
            long longValue when longValue > 0 => (ulong)longValue,
            string text when ulong.TryParse(text, out var parsed) && parsed > 0 => parsed,
            _ => null,
        };
    }

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
}
