using Spirectl.Sts2;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Live;
using Spirectl.Sts2.Live.EncounterVisuals;
using Spirectl.Proto.V0;
using Google.Protobuf;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

extern alias GodotLive;
#if ENABLE_STS2_LIVE_HOST && ENABLE_STALE_STS2_SCREEN_FIXTURE_TESTS
extern alias Sts2Live;
#endif

using GodotLive::Godot;
#if ENABLE_STS2_LIVE_HOST && ENABLE_STALE_STS2_SCREEN_FIXTURE_TESTS
using Sts2Live::MegaCrit.Sts2.Core.Entities.Players;
using Sts2Live::MegaCrit.Sts2.Core.Map;
using Sts2Live::MegaCrit.Sts2.Core.Models;
using Sts2Live::MegaCrit.Sts2.Core.Models.Cards.Mocks;
using Sts2Live::MegaCrit.Sts2.Core.Rooms;
using Sts2Live::MegaCrit.Sts2.Core.Runs;
using Sts2Live::MegaCrit.Sts2.Core.Runs.History;
#endif

public sealed partial class Sts2HostTests
{
    private static IReadOnlyList<TestTreeNode> InvokeFindDescendants(
        TestTreeNode root,
        Func<TestTreeNode, IEnumerable<TestTreeNode>> getChildren,
        Func<TestTreeNode, bool> predicate)
    {
        var helperType = typeof(Sts2ActionCatalog).Assembly.GetType("Spirectl.Sts2.Sts2TreeSearch");
        Assert.NotNull(helperType);

        var method = helperType!.GetMethod(
            "FindDescendants",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var genericMethod = method!.MakeGenericMethod(typeof(TestTreeNode));
        var result = genericMethod.Invoke(null, [root, getChildren, predicate]);

        return Assert.IsAssignableFrom<IReadOnlyList<TestTreeNode>>(result);
    }

    private static IReadOnlyList<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return [.. ex.Types.Where(type => type is not null).Cast<Type>()];
        }
    }

    private static string? InvokeResolveLobbyPlayerName(
        object? platform,
        ulong playerId,
        ICollection<StateNoticeSnapshot> notices,
        Func<object?, ulong, string?>? lookup)
    {
        var helperType = GetLoadableTypes(typeof(Sts2ActionCatalog).Assembly)
            .SingleOrDefault(type => string.Equals(type.Name, "Sts2LobbyNameResolver", StringComparison.Ordinal));
        Assert.NotNull(helperType);

        var method = helperType!.GetMethod(
            "Resolve",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [platform, playerId, notices, lookup]);
        return result as string;
    }

    private static string? InvokeResolveLobbyHostPlayerId(
        object? netService,
        string? localPlayerId,
        ICollection<StateNoticeSnapshot> notices)
    {
        var helperType = GetLoadableTypes(typeof(Sts2ActionCatalog).Assembly)
            .SingleOrDefault(type => string.Equals(type.Name, "Sts2LobbyHostResolver", StringComparison.Ordinal));
        Assert.NotNull(helperType);

        var method = helperType!.GetMethod(
            "Resolve",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [netService, localPlayerId, notices]);
        return result as string;
    }

    private static ActionExecutionResult InvokeOwnershipGuard(
        SemanticActionRequest request,
        string? requestedPlayerId,
        string? resolvedOwnerPlayerId,
        string? localPlayerId,
        string? hostPlayerId,
        string screen,
        string action,
        IReadOnlyList<string>? hostLocalPlayerIds = null,
        bool expectRejected = true)
    {
        var sts2HostAssemblyPath = Path.Combine(
            Path.GetDirectoryName(typeof(Sts2ActionCatalog).Assembly.Location) ?? string.Empty,
            "Spirectl.Sts2.dll");
        if (File.Exists(sts2HostAssemblyPath))
        {
            Assembly.LoadFrom(sts2HostAssemblyPath);
        }

        var handlerType = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(GetLoadableTypes)
            .SingleOrDefault(type => string.Equals(type.FullName, "Spirectl.Sts2.Live.Sts2ActionHandler", StringComparison.Ordinal));
        Assert.NotNull(handlerType);
        var contextType = handlerType!.GetNestedType("ActionOwnershipContext", BindingFlags.NonPublic);
        Assert.NotNull(contextType);
        var context = Activator.CreateInstance(
            contextType!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [requestedPlayerId, resolvedOwnerPlayerId, localPlayerId, hostPlayerId, hostLocalPlayerIds ?? [], screen, action],
            culture: null);
        Assert.NotNull(context);

        var method = handlerType.GetMethod("BuildOwnershipFailure", BindingFlags.NonPublic | BindingFlags.Static, [typeof(SemanticActionRequest), contextType!, typeof(ActionExecutionResult).MakeByRefType()]);
        Assert.NotNull(method);
        var args = new object?[] { request, context, null };
        var rejected = Assert.IsType<bool>(method!.Invoke(null, args));

        Assert.Equal(expectRejected, rejected);
        return rejected
            ? Assert.IsType<ActionExecutionResult>(args[2])
            : ActionExecutionResult.Success($"action:{request.RequestId}", request.Kind, "accepted");
    }

    private static void AssertGeometry(
        LobbyPresentationGeometrySnapshot geometry,
        string key,
        double x,
        double y,
        double width,
        double height,
        string checkedPath)
    {
        Assert.True(geometry.RectsByKey.TryGetValue(key, out var entry), $"Missing geometry key {key}.");
        Assert.Equal(x, entry.Rect.X);
        Assert.Equal(y, entry.Rect.Y);
        Assert.Equal(width, entry.Rect.Width);
        Assert.Equal(height, entry.Rect.Height);
        Assert.Contains(checkedPath, entry.CheckedPaths);
    }

#if ENABLE_STS2_LIVE_HOST && ENABLE_STALE_STS2_SCREEN_FIXTURE_TESTS
    private static bool InvokeIsSupportedCardSelectionScreen(object screenObject)
    {
        var inspectorType = typeof(Sts2ActionCatalog).Assembly
            .GetType("Spirectl.Sts2.Live.Sts2CardSelectionScreenInspector");
        Assert.NotNull(inspectorType);

        var method = inspectorType!.GetMethod(
            "IsSupportedScreen",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        return Assert.IsType<bool>(method!.Invoke(null, [screenObject]));
    }

    private static IReadOnlyList<object> InvokeResolveCardSelectionChoices(
        object screenObject,
        string? defaultPlayerId,
        ICollection<StateNoticeSnapshot>? notices)
    {
        var inspectorType = typeof(Sts2ActionCatalog).Assembly
            .GetType("Spirectl.Sts2.Live.Sts2CardSelectionScreenInspector");
        Assert.NotNull(inspectorType);

        var method = inspectorType!.GetMethod(
            "ResolveChoices",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [screenObject, defaultPlayerId, notices]);
        return Assert.IsAssignableFrom<IReadOnlyList<object>>(result);
    }

    private static IReadOnlyList<object> InvokeResolveEventRoomChoices(
        object eventRoom,
        string? defaultPlayerId,
        ICollection<StateNoticeSnapshot>? notices)
    {
        var inspectorType = typeof(Sts2ActionCatalog).Assembly
            .GetType("Spirectl.Sts2.Live.Sts2EventRoomScreenInspector");
        Assert.NotNull(inspectorType);

        var method = inspectorType!.GetMethod(
            "ResolveChoices",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [eventRoom, defaultPlayerId, notices]);
        return Assert.IsAssignableFrom<IReadOnlyList<object>>(result);
    }

    private static IReadOnlyList<object> InvokeResolveCrystalSphereChoices(
        object screenObject,
        string? defaultPlayerId,
        ICollection<StateNoticeSnapshot>? notices)
    {
        var inspectorType = typeof(Sts2ActionCatalog).Assembly
            .GetType("Spirectl.Sts2.Live.Sts2CrystalSphereScreenInspector");
        Assert.NotNull(inspectorType);

        var method = inspectorType!.GetMethod(
            "ResolveChoices",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [screenObject, defaultPlayerId, notices]);
        return Assert.IsAssignableFrom<IReadOnlyList<object>>(result);
    }

    private static StateRunOverlaySnapshot InvokeBuildCrystalSphereOverlay(object screenObject, string playerId)
    {
        var inspectorType = typeof(Sts2ActionCatalog).Assembly
            .GetType("Spirectl.Sts2.Live.Sts2CrystalSphereOverlayInspector");
        Assert.NotNull(inspectorType);

        var method = inspectorType!.GetMethod(
            "BuildVisibleCrystalSphereOverlay",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [screenObject, playerId]);
        return Assert.IsType<StateRunOverlaySnapshot>(result);
    }

    private static bool InvokeExecuteBundleSelectionChoice(object screenObject, object choice)
    {
        var handlerType = typeof(Sts2ActionCatalog).Assembly
            .GetType("Spirectl.Sts2.Live.Sts2ActionHandler");
        Assert.NotNull(handlerType);

        var method = handlerType!.GetMethod(
            "TryExecuteBundleSelectionChoice",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        return Assert.IsType<bool>(method!.Invoke(null, [screenObject, choice]));
    }

    private static bool InvokeExecuteCrystalSphereChoice(object screenObject, object choice)
    {
        var handlerType = typeof(Sts2ActionCatalog).Assembly
            .GetType("Spirectl.Sts2.Live.Sts2ActionHandler");
        Assert.NotNull(handlerType);

        var method = handlerType!.GetMethod(
            "TryExecuteCrystalSphereChoice",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        return Assert.IsType<bool>(method!.Invoke(null, [screenObject, choice]));
    }

    private static IReadOnlyList<object> InvokeResolveMapFlowChoices(
        object mapScreen,
        string? defaultPlayerId)
    {
        var inspectorType = typeof(Sts2ActionCatalog).Assembly
            .GetType("Spirectl.Sts2.Live.Sts2MapScreenInspector");
        Assert.NotNull(inspectorType);

        var method = inspectorType!.GetMethod(
            "ResolveFlowChoices",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);

        return Assert.IsAssignableFrom<IReadOnlyList<object>>(method!.Invoke(null, [mapScreen, defaultPlayerId]));
    }

    private static object CreateResolvedMapChoice(string choiceId, object control, bool isExecutable)
    {
        var choiceType = typeof(Sts2ActionCatalog).Assembly
            .GetType("Spirectl.Sts2.Live.ResolvedMapChoice");
        Assert.NotNull(choiceType);

        return Activator.CreateInstance(
            choiceType!,
            new ChoiceSnapshot(choiceId, "Back", "map-flow", Provisional: false, OwnerPlayerId: "p1"),
            control,
            "p1",
            isExecutable)!;
    }

    private static bool InvokeTryExecuteMapChoice(object choice)
    {
        var method = typeof(Sts2ActionHandler).GetMethod(
            "TryExecuteMapChoice",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        return Assert.IsType<bool>(method!.Invoke(null, [choice]));
    }

    private static string? ReadChoiceSnapshotString(object choice, string propertyName)
    {
        var snapshot = ReadObjectProperty(choice, "Snapshot");
        return snapshot is null ? null : ReadObjectProperty(snapshot, propertyName) as string;
    }

    private static bool ReadChoiceBool(object choice, string propertyName)
        => Assert.IsType<bool>(ReadObjectProperty(choice, propertyName));

    private static object? ReadObjectProperty(object target, string propertyName)
        => target.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target);
#endif

    private sealed record TestTreeNode(string Name, params TestTreeNode[] Children);

    private sealed record TestLobbyNetService(string Type, ulong? HostNetId);

    private sealed class TestCharacterSelectButton
    {
        public bool OnPressCalled { get; private set; }

        public bool SelectCalled { get; private set; }

        public void OnPress()
        {
            OnPressCalled = true;
        }

        public void Select()
        {
            SelectCalled = true;
        }
    }

    private sealed class TestLobbyPresentationScreen
    {
        private readonly Control _selectedCharacter;
        private readonly Control _background;
        private readonly Control _selectedCharacterStats;
        private readonly Control _readyButton;
        private readonly Control _backButton;
        private readonly Node _playerContainer;

        public TestLobbyPresentationScreen(bool ironcladSecondGenericMarker = false)
        {
            _background = Control("Background", 0, 0, 1280, 720);
            _selectedCharacter = Control("SelectedCharacter", 700, 120, 220, 320);
            _selectedCharacterStats = Control("SelectedCharacterStats", 520, 240, 180, 120);
            _readyButton = Control("ReadyButton", 840, 490, 120, 52);
            _backButton = Control("BackButton", 30, 490, 110, 52);
            _playerContainer = new Node { Name = "Players" };
            _playerContainer.AddChild(new TestLobbyPlayerControl("p:100", 40, 80, 170, 56));
            _playerContainer.AddChild(new TestLobbyPlayerControl("p:200", 40, 150, 170, 56));
            IroncladButton = Control("Ironclad", 100, 400, 80, 90);
            AddCharacterTileChildren(
                IroncladButton,
                10,
                10,
                25,
                "p:100",
                extraGenericMarkerX: ironcladSecondGenericMarker ? 47 : null);
            SilentButton = Control("Silent", 190, 400, 80, 90);
            AddCharacterTileChildren(SilentButton, 10, 10, 25, "p:200", useContainerMarker: true);
        }

        public Control IroncladButton { get; }

        public Control SilentButton { get; }

        private static Control Control(string name, float x, float y, float width, float height)
            => new()
            {
                Name = name,
                Position = new Vector2(x, y),
                Size = new Vector2(width, height),
                Visible = true,
            };

        private static void AddCharacterTileChildren(
            Control tile,
            float portraitX,
            float portraitY,
            float markerX,
            string playerId,
            bool useContainerMarker = false,
            float? extraGenericMarkerX = null)
        {
            var margin = new Control { Name = "MarginContainer", Visible = true };
            var mask = new Control { Name = "Mask", Visible = true };
            mask.AddChild(Control("Icon", portraitX, portraitY, 60, 55));
            margin.AddChild(mask);
            tile.AddChild(margin);

            Control markers = useContainerMarker
                ? new TextureRect
                {
                    Name = "PlayerIconContainer",
                    Position = new Vector2(markerX, 74),
                    Size = new Vector2(24, 24),
                    Visible = true,
                    Modulate = new Color(0x87 / 255f, 0xce / 255f, 0xeb / 255f, 1),
                }
                : new HBoxContainer { Name = "PlayerIconContainer", Visible = true };
            var marker = Control("CharSelectPlayerIcon", markerX, 74, 24, 24);
            marker.SetMeta("PlayerId", playerId);
            if (!useContainerMarker)
            {
                markers.AddChild(marker);
                if (extraGenericMarkerX is { } genericMarkerX)
                {
                    markers.AddChild(new TextureRect
                    {
                        Name = "@TextureRect@541",
                        Position = new Vector2(genericMarkerX, 74),
                        Size = new Vector2(24, 24),
                        Visible = true,
                        Modulate = new Color(0x87 / 255f, 0xce / 255f, 0xeb / 255f, 1),
                    });
                }
            }

            tile.AddChild(markers);
        }
    }

    private sealed class TestLobbyPlayerControl : Control
    {
        public TestLobbyPlayerControl(string playerId, float x, float y, float width, float height)
        {
            PlayerId = playerId;
            Name = $"Player{playerId}";
            Position = new Vector2(x, y);
            Size = new Vector2(width, height);
            Visible = true;

            var characterIcon = new TextureRect
            {
                Name = "CharacterIcon",
                Position = new Vector2(8, 8),
                Size = new Vector2(40, 40),
                Visible = true,
            };
            characterIcon.AddChild(new TextureRect
            {
                Name = "ReadyIndicator",
                Position = new Vector2(0, 0),
                Size = new Vector2(28, 28),
                Visible = true,
                Modulate = new Color(0x7f / 255f, 1, 0, 1),
            });
            AddChild(characterIcon);
        }

        public string PlayerId { get; }
    }

    private sealed class TestSavedRun
    {
        public int CurrentActIndex { get; init; }

        public IReadOnlyList<TestSavedPlayer> Players { get; init; } = [];

        public TestSavedRunRng SerializableRng { get; init; } = new();

        public IReadOnlyList<IReadOnlyList<TestMapPointHistoryEntry>> MapPointHistory { get; init; } = [];
    }

    private sealed class TestSavedPlayer
    {
        public ulong NetId { get; init; }

        public TestModelId? CharacterId { get; init; }

        public int CurrentHp { get; init; }

        public int MaxHp { get; init; }
    }

    private sealed class TestModelId(string entry)
    {
        public string Entry { get; } = entry;
    }

    private sealed class TestSavedRunRng
    {
        public string? Seed { get; init; }
    }

    private sealed class TestMapPointHistoryEntry;

    private class BaseInventoryForReflectionTest
    {
        public bool WasClosed { get; private set; }

        private void Close()
        {
            WasClosed = true;
        }
    }

    private sealed class DerivedInventoryForReflectionTest : BaseInventoryForReflectionTest;

    private sealed class BlockingActionHandler : IActionHandler
    {
        public ManualResetEventSlim Entered { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public ActionExecutionResult Execute(SemanticActionRequest request)
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(5));
            return ActionExecutionResult.Success(
                "action:test:blocking",
                request.Kind,
                "Action released.");
        }
    }
}
