using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Spirectl.Sts2.Live;

public sealed class Sts2ScreenLocator
{
    public ScreenLocatorResult Locate()
    {
        try
        {
            var openMapScreen = NMapScreen.Instance;
            var overlay = TryLocateOverlayObject();
            var retainedScreen = TryLocateRetainedScreen();
            var overlayScreen = TryLocateOverlay(overlay, retainedScreen);
            if (overlayScreen is not null
                && Sts2ScreenPriority.ShouldOverlayOverrideScreen(overlayScreen.OverlayPolicy, retainedScreen?.ScreenType))
            {
                return overlayScreen;
            }

            if (Sts2ScreenPriority.ShouldOpenMapOverrideScreen(
                retainedScreen?.ScreenType,
                openMapScreen?.IsOpen == true,
                overlayScreen?.OverlayPolicy))
            {
                return MapScreenResult(openMapScreen!);
            }

            if (overlayScreen is not null)
            {
                return retainedScreen?.WithOverlay(overlayScreen) ?? overlayScreen;
            }

            return retainedScreen ?? new ScreenLocatorResult("unknown", "Unknown", "screen:unknown:live", "fallback", "fallback:unknown", "fallback:unknown");
        }
        catch
        {
            return new ScreenLocatorResult("unknown", "Unknown", "screen:unknown:error", "error", "error", "error");
        }
    }

    private static ScreenLocatorResult? TryLocateRetainedScreen()
    {
        try
        {
            var combatManager = CombatManager.Instance;
            if (combatManager is not null && combatManager.IsInProgress)
            {
                var playerHand = NPlayerHand.Instance;
                if (playerHand is not null && playerHand.IsInCardSelection)
                {
                    return new ScreenLocatorResult(
                        "combat",
                        "Combat",
                        "screen:combat:hand-select",
                        "player-hand",
                        playerHand.GetType().Name,
                        playerHand.GetType().FullName ?? playerHand.GetType().Name);
                }

                var combatManagerType = combatManager.GetType();
                return new ScreenLocatorResult(
                    "combat",
                    "Combat",
                    "screen:combat:active",
                    "combat-manager",
                    combatManagerType.Name,
                    combatManagerType.FullName ?? combatManagerType.Name);
            }

            var activeScreen = TryGetActiveScreen();
            if (activeScreen is not null)
            {
                return activeScreen;
            }

            var runManager = RunManager.Instance;
            if (runManager is null || !runManager.IsInProgress)
            {
                return new ScreenLocatorResult(
                    "main-menu",
                    "Main Menu",
                    "screen:main-menu:live",
                    "fallback",
                    "fallback:main-menu",
                    "fallback:main-menu");
            }

            var eventRoom = NEventRoom.Instance;
            if (eventRoom is not null && eventRoom.Visible)
            {
                var embeddedShop = Sts2ShopScreenInspector.ResolveActiveShopScreenObject(eventRoom);
                if (embeddedShop is not null)
                {
                    var embeddedShopType = embeddedShop.GetType();
                    var embeddedShopTypeName = embeddedShopType.Name;
                    return new ScreenLocatorResult(
                        Sts2SupportedScreenIds.ScreenIdFor(embeddedShopType),
                        "Shop",
                        $"screen:shop:{embeddedShopTypeName}",
                        "embedded-shop",
                        embeddedShopTypeName,
                        embeddedShopType.FullName ?? embeddedShopTypeName);
                }

                var eventRoomType = eventRoom.GetType();
                return new ScreenLocatorResult(
                    Sts2SupportedScreenIds.ScreenIdFor(eventRoomType),
                    "Event",
                    "screen:event-room:live",
                    "event-room",
                    eventRoomType.Name,
                    eventRoomType.FullName ?? eventRoomType.Name);
            }

            var mapScreen = NMapScreen.Instance;
            if (mapScreen is not null && mapScreen.IsOpen)
            {
                return MapScreenResult(mapScreen);
            }

            var merchantRoom = NMerchantRoom.Instance;
            if (merchantRoom is not null && merchantRoom.Visible)
            {
                return new ScreenLocatorResult(
                    Sts2SupportedScreenIds.ShopScreenId,
                    "Shop",
                    "screen:shop:NMerchantInventory",
                    "merchant-room",
                    merchantRoom.GetType().Name,
                    merchantRoom.GetType().FullName ?? merchantRoom.GetType().Name);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static ScreenLocatorResult? TryLocateOverlay(object? overlay, ScreenLocatorResult? retainedScreen)
    {
        if (overlay is null)
        {
            return null;
        }

        var overlayType = overlay.GetType();
        var overlayTypeName = overlayType.Name;
        var overlayFullTypeName = overlayType.FullName ?? overlayTypeName;
        if (Sts2OverlayScreenFamilies.TryResolve(overlay, out var match))
        {
            var instanceScope = Sts2SupportedScreenIds.IsLobbyScreenType(match!.ScreenType)
                ? "lobby"
                : match.ScreenType;
            return new ScreenLocatorResult(
                match.ScreenType,
                match.ScreenTitle,
                $"screen:{instanceScope}:{overlayTypeName}",
                "overlay",
                overlayTypeName,
                overlayFullTypeName,
                OverlaySource: "overlay",
                OverlayRawType: overlayTypeName,
                OverlayClassName: overlayFullTypeName,
                OverlayPolicy: match.OverlayPolicy,
                RetainedSourceFamily: retainedScreen?.ScreenType);
        }

        return new ScreenLocatorResult(
            "unknown-overlay",
            "Unknown Overlay",
            $"screen:unknown-overlay:{overlayTypeName}",
            "overlay",
            overlayTypeName,
            overlayFullTypeName,
            OverlaySource: "overlay",
            OverlayRawType: overlayTypeName,
            OverlayClassName: overlayFullTypeName,
            OverlayPolicy: "unsupported",
            RetainedSourceFamily: retainedScreen?.ScreenType);
    }

    private static object? TryLocateOverlayObject()
    {
        var overlayStack = NOverlayStack.Instance;
        if (overlayStack is null)
        {
            return null;
        }

        var peek = overlayStack.Peek();
        if (peek is not null)
        {
            return peek;
        }

        var children = overlayStack.GetChildren();
        for (var index = children.Count - 1; index >= 0; index -= 1)
        {
            var child = children[index];
            if (Sts2OverlayScreenFamilies.TryResolve(child, out var match)
                && match!.IsSupported
                && match.IsBlocking)
            {
                return child;
            }
        }

        return null;
    }

    private static ScreenLocatorResult MapScreenResult(NMapScreen mapScreen)
    {
        return new ScreenLocatorResult(
            Sts2SupportedScreenIds.ScreenIdFor(mapScreen.GetType()),
            "Map",
            "screen:map:live",
            "map-screen",
            mapScreen.GetType().Name,
            mapScreen.GetType().FullName ?? mapScreen.GetType().Name);
    }

    private static ScreenLocatorResult? TryGetActiveScreen()
    {
        try
        {
            var screen = Sts2LiveIntrospection.ResolveCurrentScreenObject();
            var embeddedShop = Sts2ShopScreenInspector.ResolveActiveShopScreenObject(screen);
            if (embeddedShop is not null && !ReferenceEquals(embeddedShop, screen))
            {
                var embeddedShopType = embeddedShop.GetType();
                var embeddedShopTypeName = embeddedShopType.Name;
                return new ScreenLocatorResult(
                    Sts2SupportedScreenIds.ScreenIdFor(embeddedShopType),
                    "Shop",
                    $"screen:shop:{embeddedShopTypeName}",
                    "embedded-shop",
                    embeddedShopTypeName,
                    embeddedShopType.FullName ?? embeddedShopTypeName);
            }

            if (screen is null || !Sts2SupportedScreenIds.TryResolve(screen, out var match))
            {
                return null;
            }

            var type = screen.GetType();
            var typeName = type.Name;
            var typeFullName = type.FullName ?? typeName;
            var instanceScope = Sts2SupportedScreenIds.IsLobbyScreenType(match!.ScreenType)
                ? "lobby"
                : match.ScreenType;
            return new ScreenLocatorResult(
                match.ScreenType,
                match.ScreenTitle,
                $"screen:{instanceScope}:{typeName}",
                "active-screen",
                typeName,
                typeFullName);
        }
        catch
        {
            return null;
        }
    }
}

public sealed record ScreenLocatorResult(
    string ScreenType,
    string ScreenTitle,
    string ScreenInstanceId,
    string Source,
    string ScreenRawType,
    string ScreenClassName,
    string? OverlaySource = null,
    string? OverlayRawType = null,
    string? OverlayClassName = null,
    string? OverlayPolicy = null,
    string? RetainedSourceFamily = null)
{
    public ScreenLocatorResult WithOverlay(ScreenLocatorResult overlay)
        => this with
        {
            OverlaySource = overlay.OverlaySource ?? overlay.Source,
            OverlayRawType = overlay.OverlayRawType ?? overlay.ScreenRawType,
            OverlayClassName = overlay.OverlayClassName ?? overlay.ScreenClassName,
            OverlayPolicy = overlay.OverlayPolicy,
            RetainedSourceFamily = overlay.RetainedSourceFamily ?? ScreenType,
        };
}
