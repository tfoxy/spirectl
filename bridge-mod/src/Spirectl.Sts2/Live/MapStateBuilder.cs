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

internal static class MapStateBuilder
{
    internal static StateRunMapSnapshot? ResolveRunMap(
        RunState runState,
        IReadOnlyList<StateMapCoordSnapshot> visitedMapCoords,
        ICollection<StateNoticeSnapshot> notices,
        string? viewPlayerId,
        string? localPlayerId)
    {
        var map = Sts2LiveIntrospection.GetMemberValue(runState, "Map");
        if (map is null)
        {
            return null;
        }

        var rowCount = StateProjectionValues.ToInt32(StateProjectionValues.InvokeParameterless(map, "GetRowCount"));
        var startingId = ResolveMapPointId(Sts2LiveIntrospection.GetMemberValue(map, "StartingMapPoint"));
        var bossId = ResolveMapPointId(Sts2LiveIntrospection.GetMemberValue(map, "BossMapPoint"));
        var secondBossId = ResolveMapPointId(Sts2LiveIntrospection.GetMemberValue(map, "SecondBossMapPoint"));

        var pointList = StateProjectionValues.EnumerateCollection(StateProjectionValues.InvokeParameterless(map, "GetAllMapPoints"))
            .Select(ResolveMapPoint)
            .Where(point => point is not null)
            .Cast<StateMapPointSnapshot>()
            .ToList();

        // Boss / starting / second-boss points live OUTSIDE the Grid: ActMap.GetAllMapPoints()
        // only walks the Grid, and BossMapPoint sits at row == GetRowCount() (beyond grid bounds).
        // NMapScreen.SetMap adds them to _mapPointDictionary separately, so without this the browser
        // map renders no boss/start icon and the pre-boss floor has NO travelable node to click —
        // the run wedges with "select-map-node all disabled, no boss". Surface them here (deduped)
        // so the boss icon renders and ResolveTravelablePointIds (which already knows bossId/startingId)
        // can mark it clickable.
        var seenPointIds = new HashSet<string>(pointList.Select(point => point.Id), StringComparer.Ordinal);
        var specialPoints = new[]
        {
            Sts2LiveIntrospection.GetMemberValue(map, "StartingMapPoint"),
            Sts2LiveIntrospection.GetMemberValue(map, "BossMapPoint"),
            Sts2LiveIntrospection.GetMemberValue(map, "SecondBossMapPoint"),
        };
        foreach (var special in specialPoints)
        {
            if (ResolveMapPoint(special) is { } resolved && seenPointIds.Add(resolved.Id))
            {
                pointList.Add(resolved);
            }
        }

        var points0 = pointList
            .OrderBy(point => point.Coord.Row)
            .ThenBy(point => point.Coord.Col)
            .ToArray();

        // Travelable + visited are run-global (one shared party position). Derive them
        // structurally from topology + visited coords so they hold even when the host's
        // NMapScreen isn't the active screen — mirrors NMapScreen.RecalculateTravelability.
        var visitedIds = new HashSet<string>(visitedMapCoords.Select(MapPointId), StringComparer.Ordinal);
        var travelableIds = ResolveTravelablePointIds(runState, points0, visitedMapCoords, startingId, bossId, secondBossId, rowCount);
        var revealedRoomTypeByRow = ResolveRevealedRoomTypesByRow(runState);
        var points = points0
            .Select(point => point with
            {
                Visited = visitedIds.Contains(point.Id),
                Travelable = travelableIds.Contains(point.Id),
                // The game reveals an Unknown (`?`) node's category once travelled (NNormalMapPoint
                // .UpdateIcon reads the visited node's resolved RoomType from MapPointHistory). Surface
                // it so the renderer can show the revealed-category icon; empty for non-Unknown points,
                // an un-travelled `?`, or when no history row is recorded.
                RevealedRoomType = point.PointType == "Unknown" && visitedIds.Contains(point.Id)
                    && revealedRoomTypeByRow.TryGetValue(point.Coord.Row, out var revealedRoomType)
                        ? revealedRoomType
                        : string.Empty,
            })
            .ToArray();

        var view = ResolveRunMapView(notices, viewPlayerId, localPlayerId);
        return new StateRunMapSnapshot(
            SourceType: map.GetType().FullName ?? map.GetType().Name,
            RowCount: rowCount,
            ColumnCount: StateProjectionValues.ToInt32(StateProjectionValues.InvokeParameterless(map, "GetColumnCount")),
            StartingMapPointId: startingId,
            BossMapPointId: bossId,
            SecondBossMapPointId: secondBossId,
            MapPointHistory: ResolveMapPointHistory(Sts2LiveIntrospection.GetMemberValue(runState, "MapPointHistory"), visitedMapCoords),
            Points: points,
            View: view,
            Votes: ResolveMapVotes(runState));
    }

    // Mirrors NMapScreen.RecalculateTravelability over already-resolved topology: the
    // selectable next nodes from the party's current position.
    private static IReadOnlySet<string> ResolveTravelablePointIds(
        RunState runState,
        IReadOnlyList<StateMapPointSnapshot> points,
        IReadOnlyList<StateMapCoordSnapshot> visited,
        string? startingId,
        string? bossId,
        string? secondBossId,
        int rowCount)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        // Authoritative when the host's map screen is live — which is exactly when seats can
        // vote. Reuse the screen's own per-node travelability (the same source the
        // select-map-node handler and host availableActions use), which already handles the
        // starting/boss nodes that GetAllMapPoints' child graph doesn't surface.
        var screen = NMapScreen.Instance;
        if (screen is not null)
        {
            foreach (var node in Sts2MapScreenInspector.ResolveNodes(screen))
            {
                if (node.IsTravelable)
                {
                    result.Add(MapPointId(new StateMapCoordSnapshot(node.Row, node.Col)));
                }
            }

            return result;
        }

        // Fallback (map screen not instantiated): derive structurally from topology.
        if (visited.Count == 0)
        {
            if (startingId is not null)
            {
                result.Add(startingId);
            }

            return result;
        }

        var last = visited[^1];
        var lastId = MapPointId(last);
        if (secondBossId is not null && bossId is not null && string.Equals(lastId, bossId, StringComparison.Ordinal))
        {
            result.Add(secondBossId);
            return result;
        }

        if (last.Row == rowCount - 1)
        {
            if (bossId is not null)
            {
                result.Add(bossId);
            }

            return result;
        }

        var freeTravel = false;
        try
        {
            freeTravel = Hook.ShouldAllowFreeTravel(runState);
        }
        catch
        {
            // Free-travel relics/modifiers are optional; default to the standard child-only rule.
        }

        if (freeTravel)
        {
            foreach (var point in points)
            {
                if (point.Coord.Row == last.Row + 1)
                {
                    result.Add(point.Id);
                }
            }

            return result;
        }

        var lastPoint = points.FirstOrDefault(point => string.Equals(point.Id, lastId, StringComparison.Ordinal));
        if (lastPoint is not null)
        {
            foreach (var childId in lastPoint.ChildIds)
            {
                result.Add(childId);
            }
        }

        return result;
    }

    // Per-player map votes from MapSelectionSynchronizer._votes (slot-indexed List<MapVote?>).
    private static IReadOnlyList<StateMapVoteSnapshot> ResolveMapVotes(RunState runState)
    {
        var synchronizer = RunManager.Instance?.MapSelectionSynchronizer;
        if (synchronizer is null
            || Sts2LiveIntrospection.GetMemberValue(synchronizer, "_votes") is not IEnumerable rawVotes)
        {
            return [];
        }

        var votes = rawVotes.Cast<object?>().ToArray();
        var players = StateProjectionValues.EnumerateCollection(Sts2LiveIntrospection.GetMemberValue(runState, "Players")).ToArray();
        var result = new List<StateMapVoteSnapshot>();
        for (var index = 0; index < players.Length; index++)
        {
            var playerId = StateProjectionValues.ResolveRunPlayerId(players[index], index);
            StateMapCoordSnapshot? coord = null;
            if (index < votes.Length && votes[index] is { } voteObject)
            {
                coord = ResolveMapCoord(Sts2LiveIntrospection.GetMemberValue(voteObject, "coord"));
            }

            result.Add(new StateMapVoteSnapshot(playerId, coord));
        }

        return result;
    }

    private static StateRunMapViewSnapshot? ResolveRunMapView(
        ICollection<StateNoticeSnapshot> notices,
        string? viewPlayerId,
        string? localPlayerId)
    {
        if (!string.IsNullOrWhiteSpace(viewPlayerId)
            && !string.IsNullOrWhiteSpace(localPlayerId)
            && !string.Equals(viewPlayerId, localPlayerId, StringComparison.Ordinal))
        {
            notices.Add(StateProjectionValues.PartialNotice(
                "run.map.view",
                "state-run-map-view-local-only",
                "Run map view is attached-client transient UI and is omitted for the requested remote presentation perspective."));
            return null;
        }

        return new StateRunMapViewSnapshot(
            IsOpen: NMapScreen.Instance?.IsOpen == true,
            IsAcceptingVotes: NMapScreen.Instance?.IsTravelEnabled == true);
    }

    private static StateMapPointSnapshot? ResolveMapPoint(object? point)
    {
        if (point is null || ResolveMapCoord(Sts2LiveIntrospection.GetMemberValue(point, "coord")) is not { } coord)
        {
            return null;
        }

        return new StateMapPointSnapshot(
            Id: MapPointId(coord),
            SourceType: point.GetType().FullName ?? point.GetType().Name,
            Coord: coord,
            PointType: Sts2LiveIntrospection.GetMemberValue(point, "PointType")?.ToString() ?? string.Empty,
            CanBeModified: StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(point, "CanBeModified")),
            ParentIds: StateProjectionValues.EnumerateCollection(Sts2LiveIntrospection.GetMemberValue(point, "parents"))
                .Select(ResolveMapPointId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray(),
            ChildIds: StateProjectionValues.EnumerateCollection(Sts2LiveIntrospection.GetMemberValue(point, "Children"))
                .Select(ResolveMapPointId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray());
    }

    private static IReadOnlyList<StateMapPointHistoryActSnapshot> ResolveMapPointHistory(
        object? historyObject,
        IReadOnlyList<StateMapCoordSnapshot> visitedMapCoords)
    {
        if (historyObject is not IEnumerable acts)
        {
            return [];
        }

        var floor = 1;
        var visitedIndex = 0;
        var result = new List<StateMapPointHistoryActSnapshot>();
        foreach (var act in acts)
        {
            var entries = new List<StateMapPointHistoryEntrySnapshot>();
            foreach (var entry in StateProjectionValues.EnumerateCollection(act))
            {
                entries.Add(new StateMapPointHistoryEntrySnapshot(
                    Floor: floor,
                    Coord: visitedIndex < visitedMapCoords.Count ? visitedMapCoords[visitedIndex] : null,
                    MapPointType: Sts2LiveIntrospection.GetMemberValue(entry, "MapPointType")?.ToString() ?? string.Empty));
                floor++;
                visitedIndex++;
            }

            result.Add(new StateMapPointHistoryActSnapshot(entries));
        }

        return result;
    }

    // For the CURRENT act, the resolved room type behind each travelled node, keyed by row. The game
    // reveals an Unknown (`?`) node's category from MapPointHistory[CurrentActIndex][row].Rooms.First()
    // .RoomType (NNormalMapPoint.UpdateIcon); we surface it on visited Unknown points so the renderer
    // shows the revealed-category icon instead of a plain `?`. Within an act, history entries are
    // appended in floor/row order, so entry i corresponds to the node at row i.
    private static IReadOnlyDictionary<int, string> ResolveRevealedRoomTypesByRow(RunState runState)
    {
        var result = new Dictionary<int, string>();
        if (Sts2LiveIntrospection.GetMemberValue(runState, "MapPointHistory") is not IEnumerable acts)
        {
            return result;
        }

        var currentActIndex = StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(runState, "CurrentActIndex"));
        var actIndex = 0;
        foreach (var act in acts)
        {
            if (actIndex++ != currentActIndex)
            {
                continue;
            }

            var row = 0;
            foreach (var entry in StateProjectionValues.EnumerateCollection(act))
            {
                var firstRoom = StateProjectionValues.EnumerateCollection(Sts2LiveIntrospection.GetMemberValue(entry, "Rooms")).FirstOrDefault();
                var roomType = firstRoom is null
                    ? null
                    : Sts2LiveIntrospection.GetMemberValue(firstRoom, "RoomType")?.ToString();
                if (!string.IsNullOrEmpty(roomType))
                {
                    result[row] = roomType;
                }

                row++;
            }

            break;
        }

        return result;
    }

    internal static IReadOnlyList<StateMapCoordSnapshot> ResolveMapCoords(object? value)
        => StateProjectionValues.EnumerateCollection(value)
            .Select(ResolveMapCoord)
            .Where(coord => coord is not null)
            .Cast<StateMapCoordSnapshot>()
            .ToArray();

    internal static StateMapCoordSnapshot? ResolveMapCoord(object? coord)
        => coord is null
            ? null
            : new StateMapCoordSnapshot(
                Row: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(coord, "row")),
                Col: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(coord, "col")));

    internal static int? ToNullableInt32(object? value)
        => value is null ? null : StateProjectionValues.ToInt32(value);

    internal static string? ResolveMapPointId(object? point)
        => ResolveMapCoord(Sts2LiveIntrospection.GetMemberValue(point, "coord")) is { } coord
            ? MapPointId(coord)
            : null;

    private static string MapPointId(StateMapCoordSnapshot coord)
        => string.Create(CultureInfo.InvariantCulture, $"map-point:{coord.Row}:{coord.Col}");

}
