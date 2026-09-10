using System.Collections;
using System.Globalization;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;
using Spirectl.Sts2.Core.Models;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Core.Perspective;
using System.Reflection;

namespace Spirectl.Sts2.Live;

internal static class StateProjectionValues
{
    internal static object? ResolveHostNetService(object? runNetService)
    {
        if (runNetService is not null)
        {
            return runNetService;
        }

        var screenObject = Sts2LiveIntrospection.ResolveCurrentScreenObject();
        if (Sts2SupportedScreenIds.IsStartRunLobbyScreen(screenObject)
            && Sts2LiveIntrospection.GetMemberValue(screenObject, "Lobby") is { } startRunLobby)
        {
            return Sts2LiveIntrospection.GetMemberValue(startRunLobby, "NetService");
        }

        return null;
    }

    internal static IReadOnlyList<string> ResolveCreatureIds(
        object? creatures,
        ICollection<StateNoticeSnapshot>? notices,
        string path,
        string fallbackPrefix)
        => EnumerateCollection(creatures)
            .Select((creature, index) => creature is null ? null : ResolveCreatureId(creature, notices, path, $"{fallbackPrefix}:{index}"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToArray();

    internal static string ResolveCreatureId(
        object creature,
        ICollection<StateNoticeSnapshot>? notices,
        string path,
        string fallbackId)
    {
        var combatId = ToUInt64(Sts2LiveIntrospection.GetMemberValue(creature, "CombatId"));
        if (combatId.HasValue)
        {
            return $"creature:{combatId.Value}";
        }

        notices?.Add(PartialNotice(path, "state-creature-id-fallback", $"A combat creature did not expose Creature.CombatId; using fallback id {fallbackId}."));
        return fallbackId;
    }

    internal static string? ResolveRoomModelId(object? room)
    {
        var modelId = Sts2LiveIntrospection.GetMemberValue(room, "ModelId");
        return NormalizeNullable(Sts2LiveIntrospection.GetMemberValue(modelId, "Entry")?.ToString())
            ?? NormalizeNullable(modelId?.ToString());
    }

    internal static string? ResolveModelOrId(object? value)
        => ResolveModelId(value)
            ?? NormalizeNullable(Sts2LiveIntrospection.GetMemberValue(value, "Entry")?.ToString())
            ?? NormalizeNullable(value?.ToString());

    internal static StateLocRefSnapshot? ResolveLocRef(
        object? locString,
        ICollection<StateNoticeSnapshot> notices,
        string path)
    {
        if (locString is null)
        {
            return null;
        }

        try
        {
            var table = NormalizeNullable(Sts2LiveIntrospection.GetMemberValue(locString, "LocTable")?.ToString());
            var key = NormalizeNullable(Sts2LiveIntrospection.GetMemberValue(locString, "LocEntryKey")?.ToString());
            return table is null || key is null ? null : new StateLocRefSnapshot(table, key);
        }
        catch (Exception ex)
        {
            notices.Add(PartialNotice(path, "state-loc-ref-unavailable", $"The localization reference could not be read: {ex.Message}"));
            return null;
        }
    }

    internal static string ResolveRunPlayerId(object player, int fallbackIndex)
    {
        var netId = ToUInt64(Sts2LiveIntrospection.GetMemberValue(player, "NetId"));
        return netId.HasValue ? $"p:{netId.Value}" : $"p:unknown:{fallbackIndex}";
    }

    internal static object? InvokeParameterless(object target, string methodName)
    {
        try
        {
            return target.GetType().GetMethod(methodName, Type.EmptyTypes)?.Invoke(target, null);
        }
        catch
        {
            return null;
        }
    }

    internal static IEnumerable<object?> EnumerateCollection(object? value)
    {
        if (value is IEnumerable enumerable && value is not string)
        {
            foreach (var item in enumerable)
            {
                yield return item;
            }
        }
    }

    internal static StateNoticeSnapshot PartialNotice(string path, string code, string message)
        => new(code, message, Provisional: true, Path: path, Severity: "partial", Source: nameof(Sts2StateProvider));

    internal static string Slug(string? value)
        => NormalizeIdentifier(value) ?? "unknown";

    internal static IReadOnlyList<string> ResolveModelIds(object? value)
    {
        if (value is not IEnumerable values)
        {
            return [];
        }

        return values
            .Cast<object?>()
            .Select(ResolveModelId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToArray();
    }

    internal static string? ResolveModelId(object? model)
    {
        if (model is null)
        {
            return null;
        }

        return NormalizeNullable(Sts2LiveIntrospection.GetMemberValue(Sts2LiveIntrospection.GetMemberValue(model, "Id"), "Entry")?.ToString())
            ?? NormalizeNullable(Sts2LiveIntrospection.GetMemberValue(model, "Id")?.ToString())
            ?? NormalizeNullable(Sts2LiveIntrospection.GetMemberValue(model, "ModelId")?.ToString());
    }

    internal static bool ResolveVisible(Node node)
        => node is CanvasItem canvasItem
            ? canvasItem.Visible
            : ToBoolean(Sts2LiveIntrospection.GetMemberValue(node, "Visible"));

    internal static string ResolveNetGameType(object? value)
        => NormalizeIdentifier(value?.ToString()) switch
        {
            "singleplayer" => "singleplayer",
            "host" => "host",
            "client" => "client",
            var normalized => normalized ?? string.Empty,
        };

    internal static string? ResolvePlayerId(object? value)
        => ToUInt64(value) is { } netId ? $"p:{netId}" : null;

    internal static uint ResolveCollectionCount(object? value)
        => value switch
        {
            ICollection collection => (uint)collection.Count,
            IEnumerable enumerable => (uint)enumerable.Cast<object?>().Count(),
            _ => 0,
        };

    internal static ulong? ToUInt64(object? value)
        => value switch
        {
            ulong typed => typed,
            uint typed => typed,
            long typed when typed >= 0 => (ulong)typed,
            int typed when typed >= 0 => (ulong)typed,
            string text when ulong.TryParse(text, out var parsed) => parsed,
            _ => null,
        };

    internal static int ToInt32(object? value)
        => value switch
        {
            int typed => typed,
            uint typed => checked((int)typed),
            long typed => checked((int)typed),
            ulong typed => checked((int)typed),
            short typed => typed,
            ushort typed => typed,
            byte typed => typed,
            sbyte typed => typed,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => 0,
        };

    internal static double ToDouble(object? value)
        => value switch
        {
            double typed => typed,
            float typed => typed,
            decimal typed => (double)typed,
            int typed => typed,
            uint typed => typed,
            long typed => typed,
            ulong typed => typed,
            string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };

    internal static bool ToBoolean(object? value)
        => value switch
        {
            bool typed => typed,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => false,
        };

    internal static string CssColor(Color color)
        => color.A >= 0.999f
            ? $"rgb({ColorChannel(color.R)} {ColorChannel(color.G)} {ColorChannel(color.B)})"
            : $"rgba({ColorChannel(color.R)} {ColorChannel(color.G)} {ColorChannel(color.B)} / {Math.Clamp(color.A, 0, 1):0.###})";

    private static int ColorChannel(float value)
        => Math.Clamp((int)Math.Round(value * 255, MidpointRounding.AwayFromZero), 0, 255);

    internal static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static string? NormalizeIdentifier(string? value)
        => NormalizeNullable(value)?.Replace('_', '-').ToLowerInvariant();

    internal static string? CurrentLanguage()
        => NormalizeNullable(LocManager.Instance?.Language);}
