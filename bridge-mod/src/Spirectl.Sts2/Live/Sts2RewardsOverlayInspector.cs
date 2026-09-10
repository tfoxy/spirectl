using System.Collections;
using System.Globalization;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live;

internal static class Sts2RewardsOverlayInspector
{
    private const string Scene = "screens/rewards_screen";
    private const string RewardsScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen";

    public static StateRunOverlaySnapshot? ResolveVisibleRewardsOverlay(string playerId)
    {
        var screenObject = Sts2LiveIntrospection.ResolveCurrentScreenObject();
        if (screenObject is null || !Sts2LiveIntrospection.IsType(screenObject, RewardsScreenType))
        {
            return null;
        }

        return ResolveVisibleRewardsOverlay(playerId, screenObject);
    }

    internal static string CapturedRewardsOverlayId(string playerId)
        => $"overlay:{playerId}:rewards:captured";

    // Build a rewards overlay snapshot directly from a list of LIVE Reward objects (no NRewardsScreen),
    // reusing the same per-reward projection + stable visible-id scheme as the live-screen path. Used to
    // surface a non-host (browser-only) seat's captured RewardsSet, which has no host reward buttons.
    internal static StateRunOverlaySnapshot? BuildCapturedRewardsOverlay(string playerId, IReadOnlyList<object> rewards)
    {
        var notices = new List<StateNoticeSnapshot>();
        var items = new List<StateRewardItemSnapshot>();
        foreach (var reward in rewards)
        {
            if (reward is null)
            {
                continue;
            }

            var itemId = Sts2RewardIds.VisibleChoiceId(playerId, items.Count);
            if (ResolveRewardItem(reward, itemId, playerId, notices) is { } item)
            {
                items.Add(item);
            }
        }

        if (items.Count == 0)
        {
            return null;
        }

        return new StateRunOverlaySnapshot(
            Id: CapturedRewardsOverlayId(playerId),
            ScreenType: "Rewards",
            ScreenId: Sts2SupportedScreenIds.RewardsScreenId,
            Scene: Scene,
            ChooseACard: null,
            Rewards: new StateRewardsOverlaySnapshot(
                Flow: null,
                Items: items,
                Notices: notices));
    }

    internal static StateRunOverlaySnapshot? ResolveVisibleRewardsOverlay(string playerId, object rewardScreen)
    {
        var notices = new List<StateNoticeSnapshot>();
        var items = ResolveRewardItems(rewardScreen, playerId, notices);
        var flow = ResolveFlow(rewardScreen);
        // Keep the overlay alive whenever the rewards screen is active and still has EITHER claimable items
        // OR a visible Proceed/Skip flow. Returning null once items hit zero (the prior behavior) hid the
        // Proceed control from the browser the instant the last reward was claimed — but the live
        // NRewardsScreen stays open waiting for Proceed, so the browser would show the map underneath, the
        // bot would "select" map nodes that the still-open reward screen swallows, and the run wedged at the
        // first combat. Surfacing the flow lets the bot press Proceed to actually finish the combat → travel.
        if (items.Count == 0 && flow is null)
        {
            return null;
        }

        return new StateRunOverlaySnapshot(
            Id: $"overlay:{playerId}:rewards:visible",
            ScreenType: "Rewards",
            ScreenId: Sts2SupportedScreenIds.RewardsScreenId,
            Scene: Scene,
            ChooseACard: null,
            Rewards: new StateRewardsOverlaySnapshot(
                Flow: flow,
                Items: items,
                Notices: notices));
    }

    private static IReadOnlyList<StateRewardItemSnapshot> ResolveRewardItems(
        object rewardScreen,
        string playerId,
        ICollection<StateNoticeSnapshot> notices)
    {
        var buttons = Enumerate(Sts2LiveIntrospection.GetMemberValue(rewardScreen, "_rewardButtons"));
        var items = new List<StateRewardItemSnapshot>();
        foreach (var button in buttons)
        {
            // Do NOT gate on the button's Visible flag: in the headless/browser render the live reward
            // buttons report Visible==false even while the rewards screen is handing out claimable items, so
            // this dropped EVERY reward (gold/card/relic/potion) from the browser overlay and the client only
            // saw the Proceed button — the bot then skipped past combats taking nothing. Surface the item by
            // structure (a Reward exists) and let the live GetReward() claim hook arbitrate. (This is the
            // overlay twin of the same fix in Sts2RewardScreenInspector; same headless wall as treasure/rest.)
            var reward = Sts2LiveIntrospection.GetMemberValue(button, "Reward")
                ?? Sts2LiveIntrospection.GetMemberValue(button, "LinkedRewardSet");
            if (reward is null)
            {
                continue;
            }

            var ownerPlayerId = ResolveRewardPlayerId(reward);
            if (!string.IsNullOrWhiteSpace(ownerPlayerId)
                && !string.Equals(ownerPlayerId, playerId, StringComparison.Ordinal))
            {
                continue;
            }

            var itemId = Sts2RewardIds.VisibleChoiceId(playerId, items.Count);
            if (ResolveRewardItem(reward, itemId, playerId, notices) is { } item)
            {
                items.Add(item);
            }
        }

        return items;
    }

    private static StateRewardFlowSnapshot? ResolveFlow(object rewardScreen)
    {
        var proceedButton = Sts2LiveIntrospection.GetMemberValue(rewardScreen, "_proceedButton");
        if (proceedButton is null || !IsVisible(proceedButton))
        {
            return null;
        }

        return new StateRewardFlowSnapshot(
            Mode: ToBoolean(Sts2LiveIntrospection.GetMemberValue(proceedButton, "IsSkip")) ? "skip" : "proceed",
            Enabled: ToBoolean(Sts2LiveIntrospection.GetMemberValue(proceedButton, "IsEnabled")));
    }

    private static StateRewardItemSnapshot? ResolveRewardItem(
        object reward,
        string itemId,
        string playerId,
        ICollection<StateNoticeSnapshot> notices)
    {
        // Resolve the type-specific payload, then attach the icon + description
        // variables that every reward kind exposes through the game's Reward base
        // (IconPath) and LocString (Variables). Doing it here keeps the per-type
        // branches focused and applies the same metadata to linked children.
        var core = ResolveRewardItemCore(reward, itemId, playerId, notices);
        if (core is null)
        {
            return null;
        }

        return core with
        {
            IconAssetKey = ResolveIconPath(reward),
            DescriptionArgs = ResolveLocArgs(Sts2LiveIntrospection.GetMemberValue(reward, "Description")),
        };
    }

    private static StateRewardItemSnapshot? ResolveRewardItemCore(
        object reward,
        string itemId,
        string playerId,
        ICollection<StateNoticeSnapshot> notices)
    {
        var sourceType = reward.GetType().FullName ?? reward.GetType().Name;
        var description = ResolveLocRef(
            Sts2LiveIntrospection.GetMemberValue(reward, "Description"),
            notices,
            "run.players[].overlays[].rewards.items[].description");

        if (IsRewardType(reward, "MegaCrit.Sts2.Core.Rewards.LinkedRewardSet", "LinkedRewardSet"))
        {
            return new StateRewardItemSnapshot(
                itemId,
                sourceType,
                description,
                Linked: Enumerate(Sts2LiveIntrospection.GetMemberValue(reward, "Rewards"))
                    .Select((child, index) => child is null
                        || IsRewardOwnedByDifferentPlayer(child, playerId)
                        ? null
                        : ResolveRewardItem(child, $"{itemId}:linked:{index}", playerId, notices))
                    .Where(item => item is not null)
                    .Cast<StateRewardItemSnapshot>()
                    .ToArray());
        }

        if (IsRewardType(reward, "MegaCrit.Sts2.Core.Rewards.GoldReward", "GoldReward"))
        {
            return new StateRewardItemSnapshot(
                itemId,
                sourceType,
                description,
                Gold: ToNullableInt32(Sts2LiveIntrospection.GetMemberValue(reward, "Amount")));
        }

        if (IsRewardType(reward, "MegaCrit.Sts2.Core.Rewards.RelicReward", "RelicReward"))
        {
            return new StateRewardItemSnapshot(
                itemId,
                sourceType,
                description,
                Relic: ResolveModelId(Sts2LiveIntrospection.GetMemberValue(reward, "_relic")
                    ?? Sts2LiveIntrospection.GetMemberValue(reward, "ClaimedRelic")));
        }

        if (IsRewardType(reward, "MegaCrit.Sts2.Core.Rewards.PotionReward", "PotionReward"))
        {
            return new StateRewardItemSnapshot(
                itemId,
                sourceType,
                description,
                Potion: ResolveModelId(Sts2LiveIntrospection.GetMemberValue(reward, "Potion")
                    ?? Sts2LiveIntrospection.GetMemberValue(reward, "ClaimedPotion")));
        }

        if (IsRewardType(reward, "MegaCrit.Sts2.Core.Rewards.SpecialCardReward", "SpecialCardReward"))
        {
            return new StateRewardItemSnapshot(
                itemId,
                sourceType,
                description,
                Card: ResolveCard(Sts2LiveIntrospection.GetMemberValue(reward, "_card"), $"{itemId}:card"));
        }

        if (IsRewardType(reward, "MegaCrit.Sts2.Core.Rewards.CardReward", "CardReward"))
        {
            return new StateRewardItemSnapshot(
                itemId,
                sourceType,
                description,
                CardReward: new StateCardRewardSnapshot(
                    Cards: Enumerate(Sts2LiveIntrospection.GetMemberValue(reward, "Cards"))
                        .Select((card, index) => ResolveCard(card, $"{itemId}:card:{index}"))
                        .Where(card => card is not null)
                        .Cast<StateCardSnapshot>()
                        .ToArray(),
                    CanReroll: ToBoolean(Sts2LiveIntrospection.GetMemberValue(reward, "CanReroll")),
                    CanSkip: ToBoolean(Sts2LiveIntrospection.GetMemberValue(reward, "CanSkip"))));
        }

        if (IsRewardType(reward, "MegaCrit.Sts2.Core.Rewards.CardRemovalReward", "CardRemovalReward"))
        {
            return new StateRewardItemSnapshot(
                itemId,
                sourceType,
                description,
                CardRemoval: true);
        }

        notices.Add(PartialNotice(
            "run.players[].overlays[].rewards.items",
            "state-reward-item-unmodeled",
            $"Reward item {sourceType} is visible but does not have a state typed payload."));
        return new StateRewardItemSnapshot(itemId, sourceType, description);
    }

    private static StateCardSnapshot? ResolveCard(object? card, string id)
        => card is null
            ? null
            : Sts2CardStateSnapshotFactory.CreateFallback(card, id);

    private static string? ResolveRewardPlayerId(object reward)
    {
        var player = Sts2LiveIntrospection.GetMemberValue(reward, "Player");
        var netId = ToUInt64(Sts2LiveIntrospection.GetMemberValue(player, "NetId"));
        return netId.HasValue ? $"p:{netId.Value}" : null;
    }

    private static bool IsRewardOwnedByDifferentPlayer(object reward, string playerId)
    {
        var ownerPlayerId = ResolveRewardPlayerId(reward);
        return !string.IsNullOrWhiteSpace(ownerPlayerId)
            && !string.Equals(ownerPlayerId, playerId, StringComparison.Ordinal);
    }

    // The game's Reward.IconPath (e.g. GoldReward -> res://images/ui/reward_screen/
    // reward_icon_money.png) is the per-reward-kind icon texture. Exposed for every
    // reward so the UI binds one field instead of special-casing gold.
    private static string? ResolveIconPath(object reward)
    {
        try
        {
            return NormalizeNullable(Sts2LiveIntrospection.GetMemberValue(reward, "IconPath")?.ToString());
        }
        catch
        {
            return null;
        }
    }

    // A LocString bundles its substitution variables (LocString.Variables), e.g. the
    // gold reward's description adds {"gold": Amount}. The renderer needs them to fill
    // the localized template's {placeholder}s; without them it shows the raw token.
    private static IReadOnlyDictionary<string, string>? ResolveLocArgs(object? locString)
    {
        if (locString is null)
        {
            return null;
        }

        try
        {
            if (Sts2LiveIntrospection.GetMemberValue(locString, "Variables") is not IDictionary variables
                || variables.Count == 0)
            {
                return null;
            }

            var args = new Dictionary<string, string>(variables.Count, StringComparer.Ordinal);
            foreach (DictionaryEntry entry in variables)
            {
                var name = entry.Key?.ToString();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                args[name] = Convert.ToString(entry.Value, CultureInfo.InvariantCulture) ?? string.Empty;
            }

            return args.Count == 0 ? null : args;
        }
        catch
        {
            return null;
        }
    }

    private static StateLocRefSnapshot? ResolveLocRef(
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
            notices.Add(PartialNotice(path, "state-reward-loc-ref-unavailable", $"The reward localization reference could not be read: {ex.Message}"));
            return null;
        }
    }

    private static string? ResolveModelId(object? model)
    {
        if (model is null)
        {
            return null;
        }

        var id = Sts2LiveIntrospection.GetMemberValue(model, "Id")
            ?? Sts2LiveIntrospection.GetMemberValue(model, "ID")
            ?? Sts2LiveIntrospection.GetMemberValue(model, "ModelId");
        return NormalizeNullable(Sts2LiveIntrospection.GetMemberValue(id, "Entry")?.ToString())
            ?? NormalizeNullable(id?.ToString());
    }

    private static bool IsRewardType(object value, string fullTypeName, string fallbackName)
        => Sts2LiveIntrospection.IsType(value, fullTypeName)
            || string.Equals(value.GetType().Name, fallbackName, StringComparison.Ordinal);

    private static bool IsVisible(object? value)
        => Sts2LiveIntrospection.GetMemberValue(value, "Visible") is not bool visible || visible;

    private static IEnumerable<object?> Enumerate(object? value)
    {
        if (value is IEnumerable enumerable && value is not string)
        {
            foreach (var item in enumerable)
            {
                yield return item;
            }
        }
    }

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Slug(string? value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();

    private static int ToInt32(object? value)
        => ToNullableInt32(value) ?? 0;

    private static int? ToNullableInt32(object? value)
    {
        try
        {
            return value switch
            {
                null => null,
                int typed => typed,
                uint typed => checked((int)typed),
                long typed => checked((int)typed),
                ulong typed => checked((int)typed),
                short typed => typed,
                ushort typed => typed,
                byte typed => typed,
                sbyte typed => typed,
                string typed when int.TryParse(typed, out var parsed) => parsed,
                _ => null,
            };
        }
        catch
        {
            return null;
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
                uint typed => typed,
                int typed when typed >= 0 => (ulong)typed,
                long typed when typed >= 0 => (ulong)typed,
                string typed when ulong.TryParse(typed, out var parsed) => parsed,
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
            string typed when bool.TryParse(typed, out var parsed) => parsed,
            _ => false,
        };

    private static StateNoticeSnapshot PartialNotice(string path, string code, string message)
        => new(code, message, true, Path: path, Severity: "partial", Source: nameof(Sts2RewardsOverlayInspector));
}
