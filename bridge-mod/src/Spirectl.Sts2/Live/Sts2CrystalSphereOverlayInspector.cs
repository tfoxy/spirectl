using System.Collections;
using System.Globalization;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live;

internal static class Sts2CrystalSphereOverlayInspector
{
    private const string ScreenType = "MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen";

    public static StateRunOverlaySnapshot? ResolveVisibleCrystalSphereOverlay(string playerId)
    {
        var screen = ResolveCurrentCrystalSphereScreenObject();
        if (screen is null)
        {
            return null;
        }

        return BuildVisibleCrystalSphereOverlay(screen, playerId);
    }

    internal static StateRunOverlaySnapshot? BuildVisibleCrystalSphereOverlay(object screen, string playerId)
    {
        var minigame = Sts2LiveIntrospection.GetMemberValue(screen, "_entity");
        if (minigame is null)
        {
            return null;
        }

        var divinationCount = ResolveInt(Sts2LiveIntrospection.GetMemberValue(minigame, "DivinationCount"));
        var isFinished = ResolveBool(Sts2LiveIntrospection.GetMemberValue(minigame, "IsFinished"));
        var canDivine = !isFinished && divinationCount > 0;
        var cells = ResolveMinigameCells(minigame)
            .Select(cell =>
            {
                var x = ResolveInt(Sts2LiveIntrospection.GetMemberValue(cell, "X"));
                var y = ResolveInt(Sts2LiveIntrospection.GetMemberValue(cell, "Y"));
                var isHidden = ResolveBool(Sts2LiveIntrospection.GetMemberValue(cell, "IsHidden"), defaultValue: true);
                return new StateCrystalSphereCellSnapshot(
                    Id: Sts2CrystalSphereIds.CellChoiceId(x, y),
                    X: x,
                    Y: y,
                    IsHidden: isHidden,
                    IsHighlighted: ResolveBool(Sts2LiveIntrospection.GetMemberValue(cell, "IsHighlighted")),
                    IsHovered: ResolveBool(Sts2LiveIntrospection.GetMemberValue(cell, "IsHovered")),
                    Enabled: canDivine && isHidden);
            })
            .OrderBy(cell => cell.Y)
            .ThenBy(cell => cell.X)
            .ToArray();

        var revealedItems = ResolveRevealedItems(screen, minigame, cells).ToArray();

        if (cells.Length == 0)
        {
            return null;
        }

        return new StateRunOverlaySnapshot(
            Id: $"overlay:{playerId}:crystal-sphere:visible",
            ScreenType: "CrystalSphere",
            ScreenId: Sts2SupportedScreenIds.CrystalSphereScreenId,
            Scene: "screens/crystal_sphere_screen",
            ChooseACard: null,
            CrystalSphere: new StateCrystalSphereOverlaySnapshot(
                DivinationsRemaining: divinationCount,
                SelectedTool: ResolveSelectedTool(minigame),
                IsFinished: isFinished,
                Cells: cells,
                RevealedItems: revealedItems));
    }

    private static object? ResolveCurrentCrystalSphereScreenObject()
    {
        var current = Sts2LiveIntrospection.ResolveCurrentScreenObject();
        if (Sts2LiveIntrospection.IsType(current, ScreenType))
        {
            return current;
        }

        var overlayStack = NOverlayStack.Instance;
        var overlay = overlayStack?.Peek();
        if (Sts2LiveIntrospection.IsType(overlay, ScreenType))
        {
            return overlay;
        }

        if (overlayStack is null)
        {
            return null;
        }

        var children = overlayStack.GetChildren();
        for (var index = children.Count - 1; index >= 0; index -= 1)
        {
            var child = children[index];
            if (Sts2LiveIntrospection.IsType(child, ScreenType))
            {
                return child;
            }
        }

        return null;
    }

    private static IEnumerable<object> ResolveMinigameCells(object minigame)
    {
        if (Sts2LiveIntrospection.GetMemberValue(minigame, "cells") is not IEnumerable cells)
        {
            yield break;
        }

        foreach (var cell in cells)
        {
            if (cell is not null)
            {
                yield return cell;
            }
        }
    }

    private static IEnumerable<StateCrystalSphereItemSnapshot> ResolveRevealedItems(
        object screen,
        object minigame,
        IReadOnlyList<StateCrystalSphereCellSnapshot> cells)
    {
        var cellsByCoord = cells.ToDictionary(cell => (cell.X, cell.Y));
        var itemNodes = ResolveItemNodes(screen).ToArray();
        var items = ResolveMinigameItems(minigame).ToArray();
        if (items.Length == 0)
        {
            items = ResolveCellItems(minigame).ToArray();
        }

        var index = 0;
        var emittedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var node = index < itemNodes.Length ? itemNodes[index] : null;
            index += 1;

            if (!TryResolveItemGridRect(item, node, out var x, out var y, out var widthCells, out var heightCells))
            {
                continue;
            }

            if (!FootprintIntersectsRevealedCell(cellsByCoord, x, y, widthCells, heightCells))
            {
                continue;
            }

            var showsCard = ResolveShowsCard(item, node);
            var iconAssetKey = showsCard ? null : ResolveItemIconAssetKey(item, node);
            var cardRarity = showsCard ? ResolveCardRarity(item) : null;
            var cardBannerMaterialKey = showsCard ? ResolveCardBannerMaterialKey(item, node, cardRarity) : null;
            var cardFrameMaterialKey = showsCard ? ResolveCardFrameMaterialKey(item, node) : null;
            var id = $"crystal-sphere:item:{x}:{y}:{widthCells}:{heightCells}";
            if (!emittedIds.Add(id))
            {
                id = $"{id}:{emittedIds.Count.ToString(CultureInfo.InvariantCulture)}";
                emittedIds.Add(id);
            }

            yield return new StateCrystalSphereItemSnapshot(
                Id: id,
                X: x,
                Y: y,
                WidthCells: widthCells,
                HeightCells: heightCells,
                IconAssetKey: iconAssetKey,
                ShowsCard: showsCard,
                CardRarity: cardRarity,
                CardBannerMaterialKey: cardBannerMaterialKey,
                CardFrameMaterialKey: cardFrameMaterialKey);
        }
    }

    private static IEnumerable<object> ResolveCellItems(object minigame)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var cell in ResolveMinigameCells(minigame))
        {
            var item = Sts2LiveIntrospection.GetMemberValue(cell, "Item");
            if (item is not null && seen.Add(item))
            {
                yield return item;
            }
        }
    }

    private static IEnumerable<object> ResolveMinigameItems(object minigame)
    {
        if (Sts2LiveIntrospection.GetMemberValue(minigame, "Items") is not IEnumerable items)
        {
            yield break;
        }

        foreach (var item in items)
        {
            if (item is not null)
            {
                yield return item;
            }
        }
    }

    private static IEnumerable<Control> ResolveItemNodes(object screen)
    {
        var container = Sts2LiveIntrospection.GetMemberValue(screen, "_itemsContainer") as Node
            ?? (screen as Node)?.GetNodeOrNull<Node>(new NodePath("Bg/Sphere/Items"))
            ?? (screen as Node)?.GetNodeOrNull<Node>(new NodePath("%Items"));
        if (container is null)
        {
            yield break;
        }

        foreach (var child in container.GetChildren())
        {
            if (child is Control control)
            {
                yield return control;
            }
        }
    }

    private static bool TryResolveItemGridRect(
        object item,
        Control? node,
        out int x,
        out int y,
        out int widthCells,
        out int heightCells)
    {
        x = 0;
        y = 0;
        widthCells = 0;
        heightCells = 0;

        var position = Sts2LiveIntrospection.GetMemberValue(item, "Position");
        var size = Sts2LiveIntrospection.GetMemberValue(item, "Size");
        if (TryResolveVector(position, out var itemX, out var itemY)
            && TryResolveVector(size, out var itemWidth, out var itemHeight))
        {
            x = RoundToInt(itemX);
            y = RoundToInt(itemY);
            widthCells = Math.Max(1, RoundToInt(itemWidth));
            heightCells = Math.Max(1, RoundToInt(itemHeight));
            return true;
        }

        if (node is null)
        {
            return false;
        }

        x = RoundToInt((node.Position.X + 313.5f) / 57f);
        y = RoundToInt((node.Position.Y + 313.5f) / 57f);
        widthCells = Math.Max(1, RoundToInt(node.Size.X / 57f));
        heightCells = Math.Max(1, RoundToInt(node.Size.Y / 57f));
        return true;
    }

    private static bool FootprintIntersectsRevealedCell(
        IReadOnlyDictionary<(int X, int Y), StateCrystalSphereCellSnapshot> cellsByCoord,
        int x,
        int y,
        int widthCells,
        int heightCells)
    {
        for (var dy = 0; dy < heightCells; dy += 1)
        {
            for (var dx = 0; dx < widthCells; dx += 1)
            {
                if (cellsByCoord.TryGetValue((x + dx, y + dy), out var cell)
                    && !cell.IsHidden)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ResolveShowsCard(object item, Control? node)
    {
        if (Sts2LiveIntrospection.GetMemberValue(item, "ShowsCard") is bool showsCard)
        {
            return showsCard;
        }

        var typeName = item.GetType().FullName ?? item.GetType().Name;
        if (typeName.Contains("CrystalSphereCardReward", StringComparison.Ordinal))
        {
            return true;
        }

        var card = node?.GetNodeOrNull<Control>(new NodePath("Card"))
            ?? node?.GetNodeOrNull<Control>(new NodePath("%Card"));
        return card?.Visible == true;
    }

    private static string? ResolveItemIconAssetKey(object item, Control? node)
        => ResolveResourcePath(Sts2LiveIntrospection.GetMemberValue(item, "Texture"))
            ?? ResolveResourcePath(Sts2LiveIntrospection.GetMemberValue(node?.GetNodeOrNull<TextureRect>(new NodePath("Icon")), "Texture"));

    private static string? ResolveCardRarity(object item)
    {
        var rarity = Sts2LiveIntrospection.GetMemberValue(item, "Rarity")
            ?? Sts2LiveIntrospection.GetMemberValue(item, "_rarity");
        var value = rarity?.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? ResolveCardBannerMaterialKey(object item, Control? node, string? rarity)
        => ResolveResourcePath(Sts2LiveIntrospection.GetMemberValue(item, "BannerMaterial"))
            ?? ResolveResourcePath(Sts2LiveIntrospection.GetMemberValue(
                node?.GetNodeOrNull<TextureRect>(new NodePath("Card/CardBanner")),
                "Material"))
            ?? CardBannerMaterialPath(rarity);

    private static string? ResolveCardFrameMaterialKey(object item, Control? node)
        => ResolveResourcePath(Sts2LiveIntrospection.GetMemberValue(item, "FrameMaterial"))
            ?? ResolveResourcePath(Sts2LiveIntrospection.GetMemberValue(
                node?.GetNodeOrNull<TextureRect>(new NodePath("Card/CardFrame")),
                "Material"));

    private static string? CardBannerMaterialPath(string? rarity)
        => rarity?.Trim().ToLowerInvariant() switch
        {
            "uncommon" => "res://materials/cards/banners/card_banner_uncommon_mat.tres",
            "rare" => "res://materials/cards/banners/card_banner_rare_mat.tres",
            "curse" => "res://materials/cards/banners/card_banner_curse_mat.tres",
            "status" => "res://materials/cards/banners/card_banner_status_mat.tres",
            "event" => "res://materials/cards/banners/card_banner_event_mat.tres",
            "quest" => "res://materials/cards/banners/card_banner_quest_mat.tres",
            "ancient" => "res://materials/cards/banners/card_banner_ancient_mat.tres",
            null or "" => null,
            _ => "res://materials/cards/banners/card_banner_common_mat.tres",
        };

    private static string? ResolveResourcePath(object? resource)
        => resource switch
        {
            string stringValue when !string.IsNullOrWhiteSpace(stringValue) => stringValue,
            Resource typed when !string.IsNullOrWhiteSpace(typed.ResourcePath) => typed.ResourcePath,
            _ => Sts2LiveIntrospection.GetMemberValue(resource, "ResourcePath")?.ToString() is { Length: > 0 } path
                ? path
                : null,
        };

    private static string ResolveSelectedTool(object minigame)
        => Sts2LiveIntrospection.GetMemberValue(minigame, "CrystalSphereTool")?.ToString() switch
        {
            "Big" => "big",
            "Small" => "small",
            _ => "none",
        };

    private static bool ResolveBool(object? value, bool defaultValue = false)
        => value is bool flag ? flag : defaultValue;

    private static int ResolveInt(object? value)
        => value switch
        {
            int intValue => intValue,
            long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
            short shortValue => shortValue,
            byte byteValue => byteValue,
            string stringValue when int.TryParse(stringValue, out var parsed) => parsed,
            _ => 0,
        };

    private static bool TryResolveVector(object? value, out double x, out double y)
    {
        x = 0;
        y = 0;
        switch (value)
        {
            case Vector2 vector:
                x = vector.X;
                y = vector.Y;
                return true;
            case Vector2I vector:
                x = vector.X;
                y = vector.Y;
                return true;
        }

        var rawX = Sts2LiveIntrospection.GetMemberValue(value, "X");
        var rawY = Sts2LiveIntrospection.GetMemberValue(value, "Y");
        if (!TryResolveDouble(rawX, out x) || !TryResolveDouble(rawY, out y))
        {
            x = 0;
            y = 0;
            return false;
        }

        return true;
    }

    private static bool TryResolveDouble(object? value, out double result)
    {
        switch (value)
        {
            case double doubleValue:
                result = doubleValue;
                return true;
            case float floatValue:
                result = floatValue;
                return true;
            case int intValue:
                result = intValue;
                return true;
            case long longValue:
                result = longValue;
                return true;
            case string stringValue when double.TryParse(stringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                result = parsed;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static int RoundToInt(double value)
        => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
