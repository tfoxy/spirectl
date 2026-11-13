using System.Collections;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Runs;

namespace Spirectl.Sts2.Live.GameApi;

#if !SPIRECTL_PROFILE_EMBEDDED
/// <summary>The purchase entry point and cancellation policy for this lane.</summary>
internal static class GameApiMerchantPurchase
{
    internal const string PurchaseMember = "OnTryPurchaseUnsynchronized";

    internal static object? Purchase(object? entry, object inventory, bool cardRemoval)
    {
        _ = cardRemoval;
        if (entry is MerchantEntry merchantEntry)
        {
            return PurchaseForPlayer(
                merchantEntry.Player,
                LocalContext.GetMe(merchantEntry.Player.RunState),
                () => RunManager.Instance.MerchantSynchronizer.PurchaseLocalEntry(merchantEntry),
                () => Sts2LiveIntrospection.InvokeMethod(entry, PurchaseMember, inventory, false));
        }

        return Sts2LiveIntrospection.InvokeMethod(entry, PurchaseMember, inventory, false);
    }

    internal static object? PurchaseForPlayer(
        object? entryPlayer,
        object? localPlayer,
        Func<object?> synchronizedPurchase,
        Func<object?> unsynchronizedPurchase)
        // Synthetic seats retain the unsynchronized path; synchronization buys for the local player only.
        => entryPlayer is not null && ReferenceEquals(entryPlayer, localPlayer)
            ? synchronizedPurchase()
            : unsynchronizedPurchase();
}

/// <summary>Treasure holders available to semantic actions and fixture automation.</summary>
internal static class GameApiTreasure
{
    internal const string ActiveHoldersMember = "HoldersInUse";

    internal static object? SingleplayerHolder(object collection)
    {
        var holders = Holders(collection);
        return holders.Count == 1 ? holders[0] : null;
    }

    internal static IReadOnlyList<object> Holders(object collection)
        => Sts2LiveIntrospection.GetMemberValue(collection, ActiveHoldersMember) is IEnumerable holders
            ? holders.OfType<object>().ToArray()
            : [];
}

/// <summary>Optional smithing progress exposed by this lane.</summary>
internal static class GameApiRestSite
{
    // This lane exposes no smithing-progress counter; preserve absence in the state payload.
    internal static object? SmithCount(object option)
    {
        _ = option;
        return null;
    }
}
#endif
