using System.Collections;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live;

internal static class Sts2ChooseACardOverlayHooks
{
    private const string Scene = "screens/card_selection/choose_a_card_selection_screen";
    private static readonly object Sync = new();
    private static bool _installed;

    public static void Install(ILogStream logStream)
    {
        lock (Sync)
        {
            if (_installed)
            {
                return;
            }

            Sts2MonoModNativeDependencies.EnsureLoaded(logStream);

            var target = typeof(CardSelectCmd)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .SingleOrDefault(method =>
                    string.Equals(method.Name, "FromChooseACardScreen", StringComparison.Ordinal)
                    && method.GetParameters() is { Length: 4 } parameters
                    && typeof(IReadOnlyList<CardModel>).IsAssignableFrom(parameters[1].ParameterType)
                    && parameters[2].ParameterType == typeof(Player)
                    && parameters[3].ParameterType == typeof(bool));
            var postfix = typeof(Sts2ChooseACardOverlayHooks).GetMethod(
                nameof(RegisterChooseACardPostfix),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (target is null || postfix is null)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.state.overlays",
                    "Unable to install choose-a-card overlay hook; CardSelectCmd.FromChooseACardScreen was not found.");
                return;
            }

            try
            {
                new Harmony("spirectl.state.choose-a-card-overlays")
                    .Patch(target, postfix: new HarmonyMethod(postfix));
                _installed = true;
                logStream.Write(
                    BridgeLogLevel.Info,
                    "bridge.state.overlays",
                    "Installed choose-a-card overlay hook.");
            }
            catch (Exception ex)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.state.overlays",
                    $"Choose-a-card overlay hook was not installed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void RegisterChooseACardPostfix(
        IReadOnlyList<CardModel> cards,
        Player player,
        bool canSkip,
        Task<CardModel?> __result)
    {
        if (cards.Count == 0)
        {
            return;
        }

        var playerNetId = player.NetId;
        var localNetId = ToUInt64(Sts2LiveIntrospection.GetMemberValue(RunManager.Instance?.NetService, "NetId"));
        if (!Sts2RunOverlayRegistry.IsObservableLocalOwner(playerNetId, localNetId))
        {
            return;
        }

        var playerId = $"p:{playerNetId}";
        var choiceId = ResolveLastChoiceId(player) ?? 0u;
        var overlay = CreateOverlaySnapshot(playerId, choiceId.ToString(), cards, canSkip);
        Sts2RunOverlayRegistry.Register(overlay);
        _ = __result.ContinueWith(
            _ => Sts2RunOverlayRegistry.Unregister(overlay.Id),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public static StateRunOverlaySnapshot? ResolveVisibleChooseACardOverlay(string playerId)
    {
        var screenObject = Sts2LiveIntrospection.ResolveCurrentScreenObject();
        if (screenObject is null)
        {
            return null;
        }

        var isChoose = Sts2LiveIntrospection.IsType(screenObject, "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NChooseACardSelectionScreen");
        var isReward = Sts2LiveIntrospection.IsType(screenObject, "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen");
        if (!isChoose && !isReward)
        {
            return null;
        }

        // Event picker keeps cards in a flat `_cards` list; the combat reward keeps them in `_cardRow`
        // holders. Surfacing BOTH as a chooseACard overlay lets the existing catalog scene render the
        // pickable cards (select-card), and the pick routes through Sts2CardSelectionScreenInspector,
        // which already handles both screen types. This is what makes combat card rewards pickable.
        var cards = isChoose
            ? Enumerate(Sts2LiveIntrospection.GetMemberValue(screenObject, "_cards")).OfType<CardModel>().ToArray()
            : Sts2CardSelectionScreenInspector.ResolveCardRewardCardModels(screenObject).ToArray();
        if (cards.Length == 0)
        {
            return null;
        }

        var ownerNetId = cards
            .Select(card => ToUInt64(Sts2LiveIntrospection.GetMemberValue(Sts2LiveIntrospection.GetMemberValue(card, "Owner"), "NetId")))
            .FirstOrDefault(netId => netId.HasValue);
        if (ownerNetId is null || !string.Equals(playerId, $"p:{ownerNetId.Value}", StringComparison.Ordinal))
        {
            return null;
        }

        var canSkip = isChoose
            ? ToBoolean(Sts2LiveIntrospection.GetMemberValue(screenObject, "_canSkip"))
            : Sts2CardSelectionScreenInspector.ResolveCardRewardCanSkip(screenObject);

        return CreateOverlaySnapshot(playerId, "visible", cards, canSkip);
    }

    private static StateRunOverlaySnapshot CreateOverlaySnapshot(
        string playerId,
        string instanceId,
        IReadOnlyList<CardModel> cards,
        bool canSkip)
    {
        return new StateRunOverlaySnapshot(
            Id: $"overlay:{playerId}:choose-a-card:{instanceId}",
            ScreenType: "CardSelection",
            ScreenId: Sts2SupportedScreenIds.ChooseACardSelectionScreenId,
            Scene: Scene,
            ChooseACard: new StateChooseACardOverlaySnapshot(
                CanSkip: canSkip,
                Cards: cards.Select((card, index) => ResolveCard(card, $"card:{playerId}:choose-a-card:{instanceId}:{index}")).ToArray()));
    }

    private static StateCardSnapshot ResolveCard(CardModel card, string id)
        => Sts2CardStateSnapshotFactory.Create(card, id);

    private static uint? ResolveLastChoiceId(Player player)
    {
        var synchronizer = RunManager.Instance?.PlayerChoiceSynchronizer;
        if (synchronizer is null)
        {
            return null;
        }

        var slot = player.RunState?.GetPlayerSlotIndex(player) ?? -1;
        if (slot < 0)
        {
            return null;
        }

        var choiceIds = Enumerate(Sts2LiveIntrospection.GetMemberValue(synchronizer, "ChoiceIds"))
            .Select(ToUInt64)
            .ToArray();
        if (slot >= choiceIds.Length || choiceIds[slot] is not { } nextChoiceId || nextChoiceId == 0)
        {
            return null;
        }

        return checked((uint)(nextChoiceId - 1));
    }

    private static IEnumerable<object?> Enumerate(object? value)
    {
        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                yield return item;
            }
        }
    }

    private static string? ResolveModelId(object? model)
    {
        if (model is null)
        {
            return null;
        }

        var id = Sts2LiveIntrospection.GetMemberValue(model, "Id");
        return NormalizeIdentifier(Sts2LiveIntrospection.GetMemberValue(id, "Entry")?.ToString()
            ?? id?.ToString()
            ?? Sts2LiveIntrospection.GetMemberValue(model, "ID")?.ToString());
    }

    private static string? NormalizeIdentifier(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string Slug(string? value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();

    private static int ToInt32(object? value)
    {
        try
        {
            return value switch
            {
                null => 0,
                int typed => typed,
                uint typed => checked((int)typed),
                long typed => checked((int)typed),
                ulong typed => checked((int)typed),
                short typed => typed,
                ushort typed => typed,
                byte typed => typed,
                sbyte typed => typed,
                _ when int.TryParse(value.ToString(), out var parsed) => parsed,
                _ => 0,
            };
        }
        catch
        {
            return 0;
        }
    }

    private static ulong? ToUInt64(object? value)
    {
        try
        {
            return value switch
            {
                null => null,
                ulong typed => typed,
                long typed when typed >= 0 => checked((ulong)typed),
                uint typed => typed,
                int typed when typed >= 0 => checked((ulong)typed),
                ushort typed => typed,
                short typed when typed >= 0 => checked((ulong)typed),
                byte typed => typed,
                sbyte typed when typed >= 0 => checked((ulong)typed),
                _ when ulong.TryParse(value.ToString(), out var parsed) => parsed,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool ToBoolean(object? value)
        => value switch
        {
            bool typed => typed,
            _ when bool.TryParse(value?.ToString(), out var parsed) => parsed,
            _ => false,
        };
}
