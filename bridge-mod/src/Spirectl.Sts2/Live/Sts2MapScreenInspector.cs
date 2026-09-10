using System.Collections;
using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live;

internal static class Sts2MapScreenInspector
{
    public static IReadOnlyList<ResolvedMapChoice> ResolveFlowChoices(
        object mapScreen,
        string? defaultPlayerId)
    {
        var choices = new List<ResolvedMapChoice>();
        if (TryResolveBackChoice(mapScreen, defaultPlayerId, out var backChoice))
        {
            choices.Add(backChoice);
        }

        return choices;
    }

    public static IReadOnlyList<ResolvedMapNode> ResolveNodes(NMapScreen mapScreen)
    {
        if (Sts2LiveIntrospection.GetMemberValue(mapScreen, "_mapPointDictionary") is not IEnumerable entries)
        {
            return [];
        }

        var nodes = new List<ResolvedMapNode>();
        foreach (var entry in entries)
        {
            if (Sts2LiveIntrospection.GetMemberValue(entry, "Key") is not MapCoord coord
                || Sts2LiveIntrospection.GetMemberValue(entry, "Value") is not NMapPoint node)
            {
                continue;
            }

            var pointType = node.Point?.PointType ?? MapPointType.Unknown;
            var label = $"{PointTypeLabel(pointType)} ({coord.row},{coord.col})";
            var travelable = node.State == MapPointState.Travelable
                || Sts2LiveIntrospection.GetMemberValue(node, "IsTravelable") is bool isTravelable && isTravelable;

            nodes.Add(new ResolvedMapNode(
                new Sts2MapNodeSnapshot(
                    Id: Sts2MapIds.NodeId(coord.row, coord.col),
                    Label: label,
                    Kind: "map-node",
                    Travelable: travelable),
                node,
                coord.row,
                coord.col,
                travelable,
                OwnerPlayerId: null,
                PreferredAction: "select-map-node"));
        }

        return nodes
            .OrderBy(node => node.Row)
            .ThenBy(node => node.Col)
            .ToArray();
    }

    private static bool TryResolveBackChoice(
        object mapScreen,
        string? defaultPlayerId,
        out ResolvedMapChoice choice)
    {
        choice = default!;

        var control = ResolveBackButton(mapScreen);
        if (control is null
            || !ResolveVisible(control)
            || !ResolveIsExecutable(control, defaultValue: false))
        {
            return false;
        }

        choice = new ResolvedMapChoice(
            new ChoiceSnapshot(
                Id: Sts2MapIds.BackChoiceId(),
                Label: "Back",
                Kind: "map-flow",
                Provisional: false,
                OwnerPlayerId: defaultPlayerId),
            Control: control,
            PlayerId: defaultPlayerId,
            IsExecutable: true,
            PreferredAction: "back-from-map");
        return true;
    }

    private static object? ResolveBackButton(object mapScreen)
    {
        var memberButton = Sts2LiveIntrospection.GetMemberValue(mapScreen, "_backButton")
            ?? Sts2LiveIntrospection.GetMemberValue(mapScreen, "BackButton");
        if (memberButton is not null)
        {
            return memberButton;
        }

        return FindDescendant(mapScreen, static child =>
            string.Equals(Sts2LiveIntrospection.GetMemberValue(child, "Name")?.ToString(), "Back", StringComparison.Ordinal)
            || string.Equals(Sts2LiveIntrospection.GetMemberValue(child, "Name")?.ToString(), "BackButton", StringComparison.Ordinal)
            || Sts2LiveIntrospection.IsType(child, "MegaCrit.Sts2.Core.Nodes.CommonUi.NBackButton"));
    }

    private static object? FindDescendant(object root, Func<object, bool> predicate)
    {
        foreach (var child in EnumerateChildren(root))
        {
            if (predicate(child))
            {
                return child;
            }

            var match = FindDescendant(child, predicate);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static IEnumerable<object> EnumerateChildren(object? target)
    {
        var children = Sts2LiveIntrospection.GetMemberValue(target, "Children") as IEnumerable;
        if (children is not null)
        {
            foreach (var child in children)
            {
                if (child is not null)
                {
                    yield return child;
                }
            }

            yield break;
        }

        if (target is Node node)
        {
            foreach (var child in node.GetChildren())
            {
                if (child is not null)
                {
                    yield return child;
                }
            }
        }
    }

    private static bool ResolveVisible(object target)
        => Sts2LiveIntrospection.GetMemberValue(target, "Visible") switch
        {
            bool visible => visible,
            _ => true,
        };

    private static bool ResolveIsExecutable(object control, bool defaultValue = true)
    {
        return Sts2LiveIntrospection.GetMemberValue(control, "Disabled") switch
        {
            bool disabled => !disabled,
            _ => Sts2LiveIntrospection.GetMemberValue(control, "IsEnabled") switch
            {
                bool isEnabled => isEnabled,
                _ => Sts2LiveIntrospection.GetMemberValue(control, "IsDisabled") switch
                {
                    bool isDisabled => !isDisabled,
                    _ => defaultValue,
                },
            },
        };
    }

    private static string PointTypeLabel(MapPointType pointType)
    {
        return pointType switch
        {
            MapPointType.RestSite => "Rest Site",
            MapPointType.Unassigned => "Unassigned",
            _ => pointType.ToString(),
        };
    }
}

internal sealed record ResolvedMapNode(
    Sts2MapNodeSnapshot Snapshot,
    NMapPoint Node,
    int Row,
    int Col,
    bool IsTravelable,
    string? OwnerPlayerId,
    string PreferredAction);

internal sealed record ResolvedMapChoice(
    ChoiceSnapshot Snapshot,
    object Control,
    string? PlayerId,
    bool IsExecutable,
    string PreferredAction);
