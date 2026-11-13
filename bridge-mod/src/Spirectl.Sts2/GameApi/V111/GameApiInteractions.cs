using System.Collections;

namespace Spirectl.Sts2.Live.GameApi;

#if !SPIRECTL_PROFILE_EMBEDDED
/// <summary>The purchase entry point and cancellation policy for this lane.</summary>
internal static class GameApiMerchantPurchase
{
    internal const string PurchaseMember = "OnTryPurchaseWrapper";

    internal static object? Purchase(object? entry, object inventory, bool cardRemoval)
        => cardRemoval
            ? Sts2LiveIntrospection.InvokeMethod(entry, PurchaseMember, inventory, false, true)
            : Sts2LiveIntrospection.InvokeMethod(entry, PurchaseMember, inventory, false);
}

/// <summary>Treasure holders available to semantic actions and fixture automation.</summary>
internal static class GameApiTreasure
{
    internal const string ActiveHoldersMember = "_holdersInUse";
    internal const string SingleplayerHolderMember = "SingleplayerRelicHolder";
    internal const string MultiplayerHoldersMember = "_multiplayerHolders";

    internal static object? SingleplayerHolder(object collection)
        => Sts2LiveIntrospection.GetMemberValue(collection, SingleplayerHolderMember);

    internal static IReadOnlyList<object> Holders(object collection)
    {
        var active = ReadHolders(collection, ActiveHoldersMember);
        if (active.Count > 0) return active;
        if (SingleplayerHolder(collection) is { } singleplayer) return [singleplayer];
        return ReadHolders(collection, MultiplayerHoldersMember);
    }

    private static IReadOnlyList<object> ReadHolders(object collection, string member)
        => Sts2LiveIntrospection.GetMemberValue(collection, member) is IEnumerable holders
            ? holders.OfType<object>().ToArray()
            : [];
}

/// <summary>Optional smithing progress exposed by this lane.</summary>
internal static class GameApiRestSite
{
    internal const string SmithCountMember = "SmithCount";

    internal static object? SmithCount(object option)
        => Sts2LiveIntrospection.GetMemberValue(option, SmithCountMember);
}
#endif
