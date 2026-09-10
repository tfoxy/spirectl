using Spirectl.Sts2;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.ConsoleCommands;
using Spirectl.Sts2.Core.Debugging;
using Spirectl.Sts2.Core.Fixtures;
using Spirectl.Sts2.Core.HotReload;
using Spirectl.Sts2.Core.Lifecycle;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.Scenarios;
using Spirectl.Sts2.Core.SceneInspection;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Core.Transport;
using Spirectl.BridgeMod.Services;
using Spirectl.Sts2.Common;
using Spirectl.Proto.V0;
using Google.Protobuf;
using System.Text.Json;
using Xunit;
using RestoreLobbyCharacterSnapshot = Spirectl.Sts2.Core.Restore.LobbyCharacterSnapshot;
using RestoreMultiplayerLobbySnapshot = Spirectl.Sts2.Core.Restore.MultiplayerLobbySnapshot;
using RestoreMultiplayerPlayerSnapshot = Spirectl.Sts2.Core.Restore.MultiplayerPlayerSnapshot;
using RestoreMultiplayerRestoreModeSnapshot = Spirectl.Sts2.Core.Restore.MultiplayerRestoreModeSnapshot;
using RestoreMultiplayerRestoreResultSnapshot = Spirectl.Sts2.Core.Restore.MultiplayerRestoreResultSnapshot;
using RestoreMultiplayerRestoreSnapshot = Spirectl.Sts2.Core.Restore.MultiplayerRestoreSnapshot;

namespace Spirectl.BridgeMod.Tests;

public sealed partial class GrpcBridgeServiceTests
{
    [Fact]
    public void RuntimeSceneSetVisibleReturnsNotImplementedForScaffoldRuntime()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var result = service.HandleSetRuntimeSceneNodeVisible(new RuntimeSceneSetVisibleRequest
        {
            NodePath = "/root/CombatScreen",
            Visible = false,
        });

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.NotImplemented, result.Error.Code);
        Assert.Equal("command", result.Error.Details[0].Field);
        Assert.Equal("dev scene set-visible", result.Error.Details[0].Value);
    }

    [Fact]
    public void RuntimeSceneHoverReturnsNotImplementedForScaffoldRuntime()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var result = service.HandleHoverRuntimeSceneControl(new RuntimeSceneControlHoverRequest
        {
            NodePath = "/root/CombatScreen",
        });

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.NotImplemented, result.Error.Code);
        Assert.Equal("command", result.Error.Details[0].Field);
        Assert.Equal("dev scene hover", result.Error.Details[0].Value);
    }

    [Fact]
    public void RuntimeSceneNodeReturnsStructuredNodeMetadata()
    {
        var runtime = BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = new FixedSnapshotExtractor(
                new GameStateSnapshot(
                    SchemaVersion: "spirectl/v0",
                    GameVersion: "sts2-live",
                    BridgeVersion: BridgeBuildInfo.BridgeVersion,
                    Source: DataSourceKind.Live,
                    Provisional: false,
                    ScreenType: "combat",
                    ScreenTitle: "Combat",
                    ScreenInstanceId: "screen:combat:live",
                    ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, null, UsesDefault: true),
                    Menu: null,
                    Lobby: null,
                    Run: null,
                    Combat: null,
                    Choices: [],
                    AvailableActions: [],
                    Notices: [],
                    Debug: null)),
            ActionHandler = new PlaceholderActionHandler(),
            LogStream = new InMemoryLogStream(),
            PerspectiveProvider = new DefaultPerspectiveProvider(),
            RuntimeSceneProvider = new FixedRuntimeSceneProvider(),
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC host running.")),
        });
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleGetRuntimeSceneNode(new RuntimeSceneNodeRequest
        {
            NodePath = "/root/CombatScreen",
        });

        Assert.NotNull(result.Success);
        Assert.Equal("combat", result.Success.Screen.Id);
        Assert.Equal("/root/CombatScreen", result.Success.Node.NodePath);
        Assert.Equal("CombatScreen", result.Success.Node.Name);
        Assert.Equal("node:screen:combat:live:/root/CombatScreen", result.Success.Node.NodeId);
        Assert.Equal("res://ui/CombatScreen.tscn", result.Success.Node.SceneFilePath);
        Assert.Equal("MegaCrit.Sts2.CombatScreenController", result.Success.Node.AttachedScriptType);
        Assert.Single(result.Success.Children);
        Assert.Equal("/root/CombatScreen/HandPanel", result.Success.Children[0].NodePath);
    }

    [Fact]
    public void RuntimeTransitionStatusMapsStructuredDiagnostics()
    {
        var runtime = BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = new FixedSnapshotExtractor(
                new GameStateSnapshot(
                    SchemaVersion: "spirectl/v0",
                    GameVersion: "sts2-live",
                    BridgeVersion: BridgeBuildInfo.BridgeVersion,
                    Source: DataSourceKind.Live,
                    Provisional: false,
                    ScreenType: "combat",
                    ScreenTitle: "Combat",
                    ScreenInstanceId: "screen:combat:live",
                    ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, null, UsesDefault: true),
                    Menu: null,
                    Lobby: null,
                    Run: null,
                    Combat: null,
                    Choices: [],
                    AvailableActions: [],
                    Notices: [],
                    Debug: null)),
            ActionHandler = new PlaceholderActionHandler(),
            LogStream = new InMemoryLogStream(),
            PerspectiveProvider = new DefaultPerspectiveProvider(),
            RuntimeSceneProvider = new FixedRuntimeSceneProvider(),
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC host running.")),
        });
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleGetRuntimeTransitionStatus(new RuntimeTransitionStatusRequest
        {
            RequestId = "transition-status-test",
        });

        Assert.NotNull(result.Success);
        Assert.Equal(DataSource.Live, result.Success.Source);
        Assert.False(result.Success.Provisional);
        Assert.Equal("combat", result.Success.Screen.Id);
        Assert.Equal("screen:combat:live", result.Success.Screen.ScreenInstanceId);
        Assert.True(result.Success.Quiescent);
        Assert.Equal(0u, result.Success.BlockingCount);
        Assert.Equal(1u, result.Success.IgnoredInfiniteCount);
        Assert.Empty(result.Success.Blockers);
        Assert.Contains("Ignored running infinite animation loops", result.Success.Notes[0]);
    }

    [Fact]
    public void RuntimeSceneNodeMapsRequestedPropertiesAndComputedTransform()
    {
        var provider = new FixedRuntimeSceneProvider();
        var runtime = BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = new FixedSnapshotExtractor(
                new GameStateSnapshot(
                    SchemaVersion: "spirectl/v0",
                    GameVersion: "sts2-live",
                    BridgeVersion: BridgeBuildInfo.BridgeVersion,
                    Source: DataSourceKind.Live,
                    Provisional: false,
                    ScreenType: "combat",
                    ScreenTitle: "Combat",
                    ScreenInstanceId: "screen:combat:live",
                    ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, null, UsesDefault: true),
                    Menu: null,
                    Lobby: null,
                    Run: null,
                    Combat: null,
                    Choices: [],
                    AvailableActions: [],
                    Notices: [],
                    Debug: null)),
            ActionHandler = new PlaceholderActionHandler(),
            LogStream = new InMemoryLogStream(),
            PerspectiveProvider = new DefaultPerspectiveProvider(),
            RuntimeSceneProvider = provider,
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC host running.")),
        });
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleGetRuntimeSceneNode(new RuntimeSceneNodeRequest
        {
            NodePath = "/root/CombatScreen",
            IncludeProperties = true,
            IncludeComputedTransform = true,
        });

        Assert.NotNull(result.Success);
        Assert.True(provider.LastQuery!.IncludeProperties);
        Assert.True(provider.LastQuery!.IncludeComputedTransform);
        Assert.NotNull(result.Success.Node.Properties);
        Assert.True(result.Success.Node.Properties.Visible);
        Assert.Equal(640, result.Success.Node.Properties.Size.X);
        Assert.Equal("Texture", result.Success.Node.Properties.Textures[0].Field);
        Assert.Equal("res://combat/backgrounds/overgrowth/layer.png", result.Success.Node.Properties.Textures[0].ResourcePath);
        Assert.Equal("#ffffffff", result.Success.Node.Properties.EffectiveModulate.Html);
        Assert.True(result.Success.Node.Properties.ClipContents);
        Assert.Equal(1, result.Success.Node.Properties.ClipChildrenMode);
        Assert.Equal(2, result.Success.Node.Properties.MouseFilter);
        Assert.Equal(1, result.Success.Node.Properties.FocusMode);
        Assert.Equal(3, result.Success.Node.Properties.MouseDefaultCursorShape);
        Assert.True(result.Success.Node.Properties.ShowBehindParent);
        Assert.False(result.Success.Node.Properties.ZAsRelative);
        Assert.Equal("keep-aspect-centered", result.Success.Node.Properties.TextureRect.StretchMode);
        Assert.Equal("ignore-size", result.Success.Node.Properties.TextureRect.ExpandMode);
        Assert.False(result.Success.Node.Properties.TextureRect.FlipH);
        Assert.True(result.Success.Node.Properties.TextureRect.FlipV);
        Assert.NotNull(result.Success.Node.Properties.Material);
        Assert.False(result.Success.Node.Properties.Material.UseParentMaterial);
        Assert.Equal("res://materials/lobby/dim.tres", result.Success.Node.Properties.Material.Material.ResourcePath);
        Assert.Equal("res://shaders/ui_dim.gdshader", result.Success.Node.Properties.Material.Shader.ResourcePath);
        Assert.Equal("darken", result.Success.Node.Properties.Material.ShaderParameters[0].Name);
        Assert.Equal("number", result.Success.Node.Properties.Material.ShaderParameters[0].ValueKind);
        Assert.Equal(0.5, result.Success.Node.Properties.Material.ShaderParameters[0].NumberValue);
        Assert.NotNull(result.Success.Node.Properties.Text);
        Assert.Equal("A spire label", result.Success.Node.Properties.Text.Text);
        Assert.Equal("A {0} label", result.Success.Node.Properties.Text.RawText);
        Assert.True(result.Success.Node.Properties.Text.RichTextEnabled);
        Assert.Equal("mega-rich-text-label", result.Success.Node.Properties.Text.Source);
        Assert.Equal("dev.runtime_scene.text", result.Success.Node.Properties.Text.DiagnosticSurface);
        Assert.Equal("mega-text._lastSetSize", result.Success.Node.Properties.Text.FontSizeSource);
        Assert.Equal(24, result.Success.Node.Properties.Text.AppliedFontSize);
        Assert.Equal(28, result.Success.Node.Properties.Text.ThemeFontSize);
        Assert.Equal(8, result.Success.Node.Properties.Text.ConfiguredMinFontSize);
        Assert.Equal(100, result.Success.Node.Properties.Text.ConfiguredMaxFontSize);
        Assert.NotNull(result.Success.Node.Properties.Text.Recipe);
        Assert.True(result.Success.Node.Properties.Text.Recipe.AutoSizeEnabled);
        Assert.Equal(24, result.Success.Node.Properties.Text.Recipe.NominalFontSizePx);
        Assert.Equal(8, result.Success.Node.Properties.Text.Recipe.MinFontSizePx);
        Assert.Equal(100, result.Success.Node.Properties.Text.Recipe.MaxFontSizePx);
        Assert.True(result.Success.Node.Properties.Text.Recipe.RichTextEnabled);
        Assert.False(result.Success.Node.Properties.Text.Recipe.HorizontallyBound);
        Assert.True(result.Success.Node.Properties.Text.Recipe.VerticallyBound);
        Assert.Equal("700", result.Success.Node.Properties.Text.FontWeight);
        Assert.Equal("italic", result.Success.Node.Properties.Text.FontStyle);
        Assert.Equal("dev_scene_text", result.Success.Node.Properties.Text.Notices[0].Code);
        Assert.NotNull(result.Success.Node.Properties.Text.Shadow);
        Assert.Equal("#00000080", result.Success.Node.Properties.Text.Shadow.Color.Html);
        Assert.Equal(2, result.Success.Node.Properties.Text.Shadow.Offset.X);
        Assert.Equal(3, result.Success.Node.Properties.Text.Shadow.Offset.Y);
        Assert.Equal(4, result.Success.Node.Properties.Text.Shadow.Size);
        Assert.Equal("theme:font_shadow_color", result.Success.Node.Properties.Text.Shadow.Source);
        Assert.Single(result.Success.Node.Properties.Text.Shadow.StackedShadows);
        Assert.Equal(0.5, result.Success.Node.Properties.Text.Shadow.StackedShadows[0].OutlineSize);
        Assert.NotNull(result.Success.Node.Properties.NinePatch);
        Assert.Equal("res://combat/backgrounds/overgrowth/layer.png", result.Success.Node.Properties.NinePatch.Texture.ResourcePath);
        Assert.True(result.Success.Node.Properties.NinePatch.DrawCenter);
        Assert.Equal(12, result.Success.Node.Properties.NinePatch.PatchMargins.Left);
        Assert.Equal("tile-fit", result.Success.Node.Properties.NinePatch.AxisStretchHorizontal);
        Assert.Equal("stretch", result.Success.Node.Properties.NinePatch.AxisStretchVertical);
        Assert.Equal("#0000005c", result.Success.Node.Properties.NinePatch.EffectiveModulate.Html);
        Assert.NotNull(result.Success.Node.ComputedTransform);
        Assert.Equal(10, result.Success.Node.ComputedTransform.GlobalTransform.Origin.X);
        Assert.Equal(20, result.Success.Node.ComputedTransform.GlobalTransform.Origin.Y);
        Assert.Equal("not_applicable", result.Success.Node.ComputedTransform.Notices[0].Code);
    }

    [Fact]
    public void RuntimeSceneSetVisibleMapsMutationResult()
    {
        var provider = new FixedRuntimeSceneProvider();
        var runtime = BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = new FixedSnapshotExtractor(
                new GameStateSnapshot(
                    SchemaVersion: "spirectl/v0",
                    GameVersion: "sts2-live",
                    BridgeVersion: BridgeBuildInfo.BridgeVersion,
                    Source: DataSourceKind.Live,
                    Provisional: false,
                    ScreenType: "combat",
                    ScreenTitle: "Combat",
                    ScreenInstanceId: "screen:combat:live",
                    ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, null, UsesDefault: true),
                    Menu: null,
                    Lobby: null,
                    Run: null,
                    Combat: null,
                    Choices: [],
                    AvailableActions: [],
                    Notices: [],
                    Debug: null)),
            ActionHandler = new PlaceholderActionHandler(),
            LogStream = new InMemoryLogStream(),
            PerspectiveProvider = new DefaultPerspectiveProvider(),
            RuntimeSceneProvider = provider,
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC host running.")),
        });
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleSetRuntimeSceneNodeVisible(new RuntimeSceneSetVisibleRequest
        {
            NodePath = "/root/CombatScreen",
            Visible = false,
            IncludeComputedTransform = true,
        });

        Assert.NotNull(result.Success);
        Assert.Equal("/root/CombatScreen", provider.LastSetVisibleRequest!.NodePath);
        Assert.False(provider.LastSetVisibleRequest.Visible);
        Assert.True(provider.LastSetVisibleRequest.IncludeComputedTransform);
        Assert.True(result.Success.PreviousVisible);
        Assert.False(result.Success.RequestedVisible);
        Assert.True(result.Success.Changed);
        Assert.NotNull(result.Success.Node.Properties);
        Assert.False(result.Success.Node.Properties.Visible);
        Assert.NotNull(result.Success.Node.ComputedTransform);
    }

    [Fact]
    public void RuntimeSceneHoverMapsControlHoverResult()
    {
        var provider = new FixedRuntimeSceneProvider();
        var runtime = BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = new FixedSnapshotExtractor(
                new GameStateSnapshot(
                    SchemaVersion: "spirectl/v0",
                    GameVersion: "sts2-live",
                    BridgeVersion: BridgeBuildInfo.BridgeVersion,
                    Source: DataSourceKind.Live,
                    Provisional: false,
                    ScreenType: "combat",
                    ScreenTitle: "Combat",
                    ScreenInstanceId: "screen:combat:live",
                    ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, null, UsesDefault: true),
                    Menu: null,
                    Lobby: null,
                    Run: null,
                    Combat: null,
                    Choices: [],
                    AvailableActions: [],
                    Notices: [],
                    Debug: null)),
            ActionHandler = new PlaceholderActionHandler(),
            LogStream = new InMemoryLogStream(),
            PerspectiveProvider = new DefaultPerspectiveProvider(),
            RuntimeSceneProvider = provider,
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC host running.")),
        });
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleHoverRuntimeSceneControl(new RuntimeSceneControlHoverRequest
        {
            NodePath = "/root/CombatScreen",
            PresentationElementId = "combat-root",
            IncludeHoverTip = true,
            SettleMs = 25,
        });

        Assert.NotNull(result.Success);
        Assert.True(result.Success.Hovered);
        Assert.Equal("/root/CombatScreen", result.Success.ResolvedNodePath);
        Assert.Equal("combat-root", result.Success.PresentationElementId);
        Assert.Equal(320, result.Success.HoverPosition.X);
        Assert.Equal(180, result.Success.HoverPosition.Y);
        Assert.NotNull(result.Success.HoverTip);
        Assert.True(result.Success.HoverTip.Visible);
        Assert.Equal("Defect", result.Success.HoverTip.Title);
        Assert.Equal("Locked.", result.Success.HoverTip.Text);
        Assert.Equal(2, result.Success.HoverTip.Labels.Count);
    }

    [Fact]
    public void RuntimeSceneSetVisibleMapsUnsupportedNodeFailure()
    {
        var runtime = BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = new FixedSnapshotExtractor(
                new GameStateSnapshot(
                    SchemaVersion: "spirectl/v0",
                    GameVersion: "sts2-live",
                    BridgeVersion: BridgeBuildInfo.BridgeVersion,
                    Source: DataSourceKind.Live,
                    Provisional: false,
                    ScreenType: "combat",
                    ScreenTitle: "Combat",
                    ScreenInstanceId: "screen:combat:live",
                    ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, null, UsesDefault: true),
                    Menu: null,
                    Lobby: null,
                    Run: null,
                    Combat: null,
                    Choices: [],
                    AvailableActions: [],
                    Notices: [],
                    Debug: null)),
            ActionHandler = new PlaceholderActionHandler(),
            LogStream = new InMemoryLogStream(),
            PerspectiveProvider = new DefaultPerspectiveProvider(),
            RuntimeSceneProvider = new UnsupportedRuntimeSceneProvider(),
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC host running.")),
        });
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleSetRuntimeSceneNodeVisible(new RuntimeSceneSetVisibleRequest
        {
            NodePath = "/root/PlainNode",
            Visible = false,
        });

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.InvalidQueryFilter, result.Error.Code);
        Assert.Equal("node_type", result.Error.Details[0].Field);
    }

    [Fact]
    public void RuntimeSceneHoverMapsUnsupportedNodeFailure()
    {
        var runtime = BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = new FixedSnapshotExtractor(
                new GameStateSnapshot(
                    SchemaVersion: "spirectl/v0",
                    GameVersion: "sts2-live",
                    BridgeVersion: BridgeBuildInfo.BridgeVersion,
                    Source: DataSourceKind.Live,
                    Provisional: false,
                    ScreenType: "combat",
                    ScreenTitle: "Combat",
                    ScreenInstanceId: "screen:combat:live",
                    ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, null, UsesDefault: true),
                    Menu: null,
                    Lobby: null,
                    Run: null,
                    Combat: null,
                    Choices: [],
                    AvailableActions: [],
                    Notices: [],
                    Debug: null)),
            ActionHandler = new PlaceholderActionHandler(),
            LogStream = new InMemoryLogStream(),
            PerspectiveProvider = new DefaultPerspectiveProvider(),
            RuntimeSceneProvider = new UnsupportedRuntimeSceneProvider(),
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC host running.")),
        });
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleHoverRuntimeSceneControl(new RuntimeSceneControlHoverRequest
        {
            NodePath = "/root/PlainNode",
            IncludeHoverTip = true,
        });

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.InvalidQueryFilter, result.Error.Code);
        Assert.Equal("node_type", result.Error.Details[0].Field);
    }

    [Fact]
    public void RuntimeSceneHoverMapsInvocationExceptionDiagnostics()
    {
        var runtime = BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = new FixedSnapshotExtractor(
                new GameStateSnapshot(
                    SchemaVersion: "spirectl/v0",
                    GameVersion: "sts2-live",
                    BridgeVersion: BridgeBuildInfo.BridgeVersion,
                    Source: DataSourceKind.Live,
                    Provisional: false,
                    ScreenType: "combat",
                    ScreenTitle: "Combat",
                    ScreenInstanceId: "screen:combat:live",
                    ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, null, UsesDefault: true),
                    Menu: null,
                    Lobby: null,
                    Run: null,
                    Combat: null,
                    Choices: [],
                    AvailableActions: [],
                    Notices: [],
                    Debug: null)),
            ActionHandler = new PlaceholderActionHandler(),
            LogStream = new InMemoryLogStream(),
            PerspectiveProvider = new DefaultPerspectiveProvider(),
            RuntimeSceneProvider = new DiagnosticHoverRuntimeSceneProvider(),
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC host running.")),
        });
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleHoverRuntimeSceneControl(new RuntimeSceneControlHoverRequest
        {
            NodePath = "/root/CharacterSelect/DEFECT_button",
            IncludeHoverTip = true,
        });

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.RuntimeFailure, result.Error.Code);
        Assert.Contains(result.Error.Details, detail =>
            detail.Field == "outer_exception_type"
            && detail.Value.Contains("TargetInvocationException", StringComparison.Ordinal));
        Assert.Contains(result.Error.Details, detail =>
            detail.Field == "root_exception_type"
            && detail.Value.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.Contains(result.Error.Details, detail =>
            detail.Field == "root_exception_message"
            && detail.Value == "hover focus handler failed");
        Assert.Contains(result.Error.Details, detail =>
            detail.Field == "requested_node_path"
            && detail.Value == "/root/CharacterSelect/DEFECT_button");
        Assert.Contains(result.Error.Details, detail =>
            detail.Field == "resolved_node_path"
            && detail.Value == "/root/CharacterSelect/DEFECT_button");
        Assert.Contains(result.Error.Details, detail =>
            detail.Field == "hover_position"
            && detail.Value == "320,180");
    }


    [Fact]
    public void RuntimeSceneNodeRequiresExactNodePath()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var result = service.HandleGetRuntimeSceneNode(new RuntimeSceneNodeRequest());

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.InvalidQueryFilter, result.Error.Code);
        Assert.Equal("node_path", result.Error.Details[0].Field);
    }

    [Fact]
    public void RuntimeSceneSetVisibleRequiresExactNodePath()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var result = service.HandleSetRuntimeSceneNodeVisible(new RuntimeSceneSetVisibleRequest());

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.InvalidQueryFilter, result.Error.Code);
        Assert.Equal("node_path", result.Error.Details[0].Field);
    }

    [Fact]
    public void ExecuteActionReturnsNotImplementedForKnownSemanticAction()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var result = service.HandleExecuteAction(new ActionRequest
        {
            RequestId = "req-1",
            PlayCard = new PlayCardAction
            {
                CardId = "c_1",
                TargetId = "e_1",
            },
        });

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.NotImplemented, result.Error.Code);
    }

    [Fact]
    public void ExecuteActionReturnsSuccessForAcceptedAction()
    {
        var runtime = BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = new FixedSnapshotExtractor(
                new GameStateSnapshot(
                    SchemaVersion: "spirectl/v0",
                    GameVersion: "sts2-live",
                    BridgeVersion: BridgeBuildInfo.BridgeVersion,
                    Source: DataSourceKind.Live,
                    Provisional: false,
                    ScreenType: "main-menu",
                    ScreenTitle: "Main Menu",
                    ScreenInstanceId: "screen:main-menu:live",
                    ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, null, UsesDefault: true),
                    Menu: new MenuStateSnapshot("main-menu", "Main Menu"),
                    Lobby: null,
                    Run: null,
                    Combat: null,
                    Choices: [],
                    AvailableActions: [],
                    Notices: [],
                    Debug: null)),
            ActionHandler = new FixedActionHandler(ActionExecutionResult.Success(
                actionInstanceId: "action:choose:req-1",
                kind: SemanticActionKind.Choose,
                message: "Accepted visible choice.",
                provisional: false)),
            LogStream = new InMemoryLogStream(),
            PerspectiveProvider = new DefaultPerspectiveProvider(),
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC host running.")),
        });
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleExecuteAction(new ActionRequest
        {
            RequestId = "req-1",
            Choose = new ChooseAction
            {
                ChoiceId = "menu:start-run",
            },
        });

        Assert.NotNull(result.Success);
        Assert.True(result.Success.Accepted);
        Assert.Equal(ActionKind.Choose, result.Success.Kind);
        Assert.False(result.Success.Provisional);
    }

    [Fact]
    public void ExecuteActionMapsSelectCharacterRequests()
    {
        var runtime = BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = new FixedSnapshotExtractor(
                new GameStateSnapshot(
                    SchemaVersion: "spirectl/v0",
                    GameVersion: "sts2-live",
                    BridgeVersion: BridgeBuildInfo.BridgeVersion,
                    Source: DataSourceKind.Live,
                    Provisional: false,
                    ScreenType: "Screens.CharacterSelect.NCharacterSelectScreen",
                    ScreenTitle: "Character Select",
                    ScreenInstanceId: "screen:lobby:start-run",
                    ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, "p:100", UsesDefault: true),
                    Menu: null,
                    Lobby: null,
                    Run: null,
                    Combat: null,
                    Choices: [],
                    AvailableActions: [],
                    Notices: [],
                    Debug: null)),
            ActionHandler = new FixedActionHandler(ActionExecutionResult.Success(
                actionInstanceId: "action:select-character:req-3",
                kind: SemanticActionKind.SelectCharacter,
                message: "Selected Silent.",
                provisional: false)),
            LogStream = new InMemoryLogStream(),
            PerspectiveProvider = new DefaultPerspectiveProvider(),
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC host running.")),
        });
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleExecuteAction(new ActionRequest
        {
            RequestId = "req-3",
            SelectCharacter = new SelectCharacterAction
            {
                CharacterId = "silent",
            },
        });

        Assert.NotNull(result.Success);
        Assert.Equal(ActionKind.SelectCharacter, result.Success.Kind);
    }

    [Fact]
    public void ExecuteActionMapsSelectMapNodeRequests()
    {
        var runtime = BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = new FixedSnapshotExtractor(
                new GameStateSnapshot(
                    SchemaVersion: "spirectl/v0",
                    GameVersion: "sts2-live",
                    BridgeVersion: BridgeBuildInfo.BridgeVersion,
                    Source: DataSourceKind.Live,
                    Provisional: false,
                    ScreenType: "map",
                    ScreenTitle: "Map",
                    ScreenInstanceId: "screen:map:live",
                    ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, "p1", UsesDefault: true),
                    Menu: null,
                    Lobby: null,
                    Run: null,
                    Combat: null,
                    Choices: [],
                    AvailableActions: [],
                    Notices: [],
                    Debug: null)),
            ActionHandler = new FixedActionHandler(ActionExecutionResult.Success(
                actionInstanceId: "action:select-map-node:req-4",
                kind: SemanticActionKind.SelectMapNode,
                message: "Selected map node.",
                provisional: false)),
            LogStream = new InMemoryLogStream(),
            PerspectiveProvider = new DefaultPerspectiveProvider(),
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC host running.")),
        });
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleExecuteAction(new ActionRequest
        {
            RequestId = "req-4",
            SelectMapNode = new SelectMapNodeAction
            {
                NodeId = "map-node:3:1",
            },
        });

        Assert.NotNull(result.Success);
        Assert.Equal(ActionKind.SelectMapNode, result.Success.Kind);
    }

    [Fact]
    public void ExecuteActionMapsTopBarRunActionRequests()
    {
        foreach (var (request, expectedSemanticKind, expectedProtoKind) in new (ActionRequest Request, SemanticActionKind SemanticKind, ActionKind ProtoKind)[]
        {
            (new ActionRequest { RequestId = "req-toggle-map", ToggleMap = new ToggleMapAction { PlayerId = "p1" } }, SemanticActionKind.ToggleMap, ActionKind.ToggleMap),
            (new ActionRequest { RequestId = "req-toggle-deck", ToggleDeck = new ToggleDeckAction { PlayerId = "p1" } }, SemanticActionKind.ToggleDeck, ActionKind.ToggleDeck),
            (new ActionRequest { RequestId = "req-toggle-settings", ToggleSettings = new ToggleSettingsAction { PlayerId = "p1" } }, SemanticActionKind.ToggleSettings, ActionKind.ToggleSettings),
            (new ActionRequest { RequestId = "req-sort-deck-view", SortDeckView = new SortDeckViewAction { By = "type", PlayerId = "p1" } }, SemanticActionKind.SortDeckView, ActionKind.SortDeckView),
            (new ActionRequest { RequestId = "req-toggle-deck-view-upgrades", ToggleDeckViewUpgrades = new ToggleDeckViewUpgradesAction { PlayerId = "p1" } }, SemanticActionKind.ToggleDeckViewUpgrades, ActionKind.ToggleDeckViewUpgrades),
        })
        {
            var actionHandler = new FixedActionHandler(ActionExecutionResult.Success(
                actionInstanceId: $"action:{expectedProtoKind}:p1",
                kind: expectedSemanticKind,
                message: "Opened top-bar target.",
                provisional: false));
            var runtime = BridgeRuntime.Create(new BridgeRuntimeOptions
            {
                StateExtractor = new FixedSnapshotExtractor(
                    new GameStateSnapshot(
                        SchemaVersion: "spirectl/v0",
                        GameVersion: "sts2-live",
                        BridgeVersion: BridgeBuildInfo.BridgeVersion,
                        Source: DataSourceKind.Live,
                        Provisional: false,
                        ScreenType: "run",
                        ScreenTitle: "Run",
                        ScreenInstanceId: "screen:run:live",
                        ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, "p1", UsesDefault: true),
                        Menu: null,
                        Lobby: null,
                        Run: null,
                        Combat: null,
                        Choices: [],
                        AvailableActions: [],
                        Notices: [],
                        Debug: null)),
                ActionHandler = actionHandler,
                LogStream = new InMemoryLogStream(),
                PerspectiveProvider = new DefaultPerspectiveProvider(),
                BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC host running.")),
            });
            var service = new GrpcBridgeService(runtime);

            var result = service.HandleExecuteAction(request);

            Assert.NotNull(result.Success);
            Assert.True(result.Success.Accepted);
            Assert.Equal(expectedProtoKind, result.Success.Kind);
            var semanticRequest = Assert.IsType<SemanticActionRequest>(actionHandler.LastRequest);
            Assert.Equal(expectedSemanticKind, semanticRequest.Kind);
            var values = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(semanticRequest.Values);
            Assert.Equal("p1", values["playerId"]);
        }
    }

    [Fact]
    public void ExecuteActionMapsCrystalSphereSelectedTool()
    {
        var actionHandler = new FixedActionHandler(ActionExecutionResult.Success(
            actionInstanceId: "action:use-crystal-sphere-control:p1",
            kind: SemanticActionKind.UseCrystalSphereControl,
            message: "Clicked Crystal Sphere cell.",
            provisional: false));
        var runtime = BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = new FixedSnapshotExtractor(
                new GameStateSnapshot(
                    SchemaVersion: "spirectl/v0",
                    GameVersion: "sts2-live",
                    BridgeVersion: BridgeBuildInfo.BridgeVersion,
                    Source: DataSourceKind.Live,
                    Provisional: false,
                    ScreenType: "crystal-sphere",
                    ScreenTitle: "Crystal Sphere",
                    ScreenInstanceId: "screen:crystal-sphere:live",
                    ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, "p1", UsesDefault: true),
                    Menu: null,
                    Lobby: null,
                    Run: null,
                    Combat: null,
                    Choices: [],
                    AvailableActions: [],
                    Notices: [],
                    Debug: null)),
            ActionHandler = actionHandler,
            LogStream = new InMemoryLogStream(),
            PerspectiveProvider = new DefaultPerspectiveProvider(),
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC host running.")),
        });
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleExecuteAction(new ActionRequest
        {
            RequestId = "req-crystal-sphere-cell",
            UseCrystalSphereControl = new UseCrystalSphereControlAction
            {
                ControlId = "crystal-sphere:cell:5:5",
                PlayerId = "p1",
                SelectedTool = "small",
            },
        });

        Assert.NotNull(result.Success);
        Assert.True(result.Success.Accepted);
        Assert.Equal(ActionKind.UseCrystalSphereControl, result.Success.Kind);
        var semanticRequest = Assert.IsType<SemanticActionRequest>(actionHandler.LastRequest);
        Assert.Equal(SemanticActionKind.UseCrystalSphereControl, semanticRequest.Kind);
        var values = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(semanticRequest.Values);
        Assert.Equal("crystal-sphere:cell:5:5", values["controlId"]);
        Assert.Equal("p1", values["playerId"]);
        Assert.Equal("small", values["selectedTool"]);
    }

    [Fact]
    public void ExecuteActionMapsStructuredInvalidActionFailures()
    {
        var remoteOrchestration = new RemoteClientOrchestrationCapabilitySnapshot(
            "local-only-degraded",
            RemoteClientOrchestrationStateSnapshot.LocalOnlyDegraded,
            "Local bridge cannot execute remote-owned semantic actions without a configured remote client.",
            Provisional: false);
        var runtime = BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = new FixedSnapshotExtractor(
                new GameStateSnapshot(
                    SchemaVersion: "spirectl/v0",
                    GameVersion: "sts2-live",
                    BridgeVersion: BridgeBuildInfo.BridgeVersion,
                    Source: DataSourceKind.Live,
                    Provisional: false,
                    ScreenType: "combat",
                    ScreenTitle: "Combat",
                    ScreenInstanceId: "screen:combat:live",
                    ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, "p1", UsesDefault: true),
                    Menu: null,
                    Lobby: null,
                    Run: null,
                    Combat: null,
                    Choices: [],
                    AvailableActions: [],
                    Notices: [],
                    Debug: null)),
            ActionHandler = new FixedActionHandler(ActionExecutionResult.Failure(
                kind: SemanticActionKind.Choose,
                code: ActionFailureCode.NotVisible,
                message: "choose requires a visible choice id from the current screen.",
                details:
                [
                    new Spirectl.Sts2.Core.Actions.ActionFailureDetail(
                        Field: "choice_id",
                        Value: "target:e_1",
                        Note: "Target markers are not executable choose actions.",
                        ReasonCode: ActionFailureCode.NotVisible,
                        Screen: "combat",
                        RequestedPlayerId: "p2",
                        ResolvedOwnerPlayerId: "p1",
                        LocalPlayerId: "p1",
                        HostPlayerId: "p1",
                        LocalRole: MultiplayerRoleSnapshot.Host,
                        Action: "choose",
                        RemoteOrchestration: remoteOrchestration),
                ],
                screen: "combat",
                checkedHookPaths: ["screen-hook:example"],
                requestedPlayerId: "p2",
                resolvedOwnerPlayerId: "p1",
                localPlayerId: "p1",
                hostPlayerId: "p1",
                localRole: MultiplayerRoleSnapshot.Host,
                action: "choose",
                remoteOrchestration: remoteOrchestration)),
            LogStream = new InMemoryLogStream(),
            PerspectiveProvider = new DefaultPerspectiveProvider(),
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC host running.")),
        });
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleExecuteAction(new ActionRequest
        {
            RequestId = "req-2",
            Choose = new ChooseAction
            {
                ChoiceId = "target:e_1",
            },
        });

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.InvalidAction, result.Error.Code);
        Assert.Equal(ErrorActionFailureReasonCode.NotVisible, result.Error.ActionFailure.ReasonCode);
        Assert.Equal("combat", result.Error.ActionFailure.Screen);
        Assert.Equal("screen-hook:example", Assert.Single(result.Error.ActionFailure.CheckedHookPaths));
        Assert.Equal("choice_id", result.Error.Details[0].Field);
        Assert.Equal("target:e_1", result.Error.Details[0].Value);
        Assert.Equal(ErrorActionFailureReasonCode.NotVisible, result.Error.Details[0].ActionReasonCode);
        Assert.Equal("combat", result.Error.Details[0].Screen);
        Assert.Equal("p2", result.Error.ActionFailure.RequestedPlayerId);
        Assert.Equal("p1", result.Error.ActionFailure.ResolvedOwnerPlayerId);
        Assert.Equal("p1", result.Error.ActionFailure.LocalPlayerId);
        Assert.Equal("p1", result.Error.ActionFailure.HostPlayerId);
        Assert.Equal(MultiplayerRole.Host, result.Error.ActionFailure.LocalRole);
        Assert.Equal("choose", result.Error.ActionFailure.Action);
        Assert.Equal(RemoteClientOrchestrationState.LocalOnlyDegraded, result.Error.ActionFailure.RemoteOrchestration.State);
        Assert.Equal("p2", result.Error.Details[0].RequestedPlayerId);
        Assert.Equal("p1", result.Error.Details[0].ResolvedOwnerPlayerId);
    }

    [Fact]
    public void ExecuteActionReturnsInvalidActionWhenPayloadIsMissing()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var result = service.HandleExecuteAction(new ActionRequest
        {
            RequestId = "req-2",
        });

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.InvalidAction, result.Error.Code);
        Assert.Equal(ErrorActionFailureReasonCode.InvalidAction, result.Error.ActionFailure.ReasonCode);
        Assert.Equal("action", result.Error.ActionFailure.FieldDiagnostics[0].Field);
    }

    // Dev-only host-side heal (`sts2 dev heal`): the proto oneof arm must carry the
    // creature:<combatId> target (empty = default to the acting seat's player creature) plus the
    // amount/full payload through Values, and map to SemanticActionKind.Heal / ActionKind.Heal.
    [Fact]
    public void ExecuteActionMapsHealRequests()
    {
        foreach (var (request, expectedTargetId, expectedAmount, expectedFull) in new (ActionRequest Request, string? TargetId, string Amount, string Full)[]
        {
            (new ActionRequest { RequestId = "req-heal-amount", Heal = new HealAction { TargetId = "creature:2", Amount = 5 } }, "creature:2", "5", "false"),
            (new ActionRequest { RequestId = "req-heal-full", Heal = new HealAction { Full = true } }, null, "0", "true"),
        })
        {
            var actionHandler = new FixedActionHandler(ActionExecutionResult.Success(
                actionInstanceId: "action:heal:req",
                kind: SemanticActionKind.Heal,
                message: "Healed.",
                provisional: false));
            var runtime = BridgeRuntime.Create(new BridgeRuntimeOptions
            {
                StateExtractor = new FixedSnapshotExtractor(
                    new GameStateSnapshot(
                        SchemaVersion: "spirectl/v0",
                        GameVersion: "sts2-live",
                        BridgeVersion: BridgeBuildInfo.BridgeVersion,
                        Source: DataSourceKind.Live,
                        Provisional: false,
                        ScreenType: "combat",
                        ScreenTitle: "Combat",
                        ScreenInstanceId: "screen:combat:live",
                        ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, "p1", UsesDefault: true),
                        Menu: null,
                        Lobby: null,
                        Run: null,
                        Combat: null,
                        Choices: [],
                        AvailableActions: [],
                        Notices: [],
                        Debug: null)),
                ActionHandler = actionHandler,
                LogStream = new InMemoryLogStream(),
                PerspectiveProvider = new DefaultPerspectiveProvider(),
                BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC host running.")),
            });
            var service = new GrpcBridgeService(runtime);

            var result = service.HandleExecuteAction(request);

            Assert.NotNull(result.Success);
            Assert.True(result.Success.Accepted);
            Assert.Equal(ActionKind.Heal, result.Success.Kind);
            var semanticRequest = Assert.IsType<SemanticActionRequest>(actionHandler.LastRequest);
            Assert.Equal(SemanticActionKind.Heal, semanticRequest.Kind);
            Assert.Equal(expectedTargetId, semanticRequest.TargetId);
            var values = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(semanticRequest.Values);
            Assert.Equal(expectedAmount, values["amount"]);
            Assert.Equal(expectedFull, values["full"]);
        }
    }

    [Fact]
    public void ExecuteActionRejectsHealWithoutAmountOrFull()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var result = service.HandleExecuteAction(new ActionRequest
        {
            RequestId = "req-heal-invalid",
            Heal = new HealAction { TargetId = "creature:2", Amount = 0, Full = false },
        });

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.InvalidAction, result.Error.Code);
        Assert.Equal("amount", result.Error.ActionFailure.FieldDiagnostics[0].Field);
    }

    [Fact]
    public void LogsRejectZeroLimit()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var result = service.HandleGetLogs(new LogsRequest
        {
            Limit = 0,
        });

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.InvalidQueryFilter, result.Error.Code);
    }

    [Fact]
    public void ScreenshotReturnsNotImplementedForScaffoldRuntime()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var result = service.HandleGetScreenshot(new ScreenshotRequest());

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.NotImplemented, result.Error.Code);
    }

    [Fact]
    public void ScreenshotReturnsStructuredCaptureMetadata()
    {
        var runtime = BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = new FixedSnapshotExtractor(
                new GameStateSnapshot(
                    SchemaVersion: "spirectl/v0",
                    GameVersion: "sts2-live",
                    BridgeVersion: BridgeBuildInfo.BridgeVersion,
                    Source: DataSourceKind.Live,
                    Provisional: false,
                    ScreenType: "combat",
                    ScreenTitle: "Combat",
                    ScreenInstanceId: "screen:combat:live",
                    ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, "p1", UsesDefault: true),
                    Menu: null,
                    Lobby: null,
                    Run: null,
                    Combat: null,
                    Choices: [],
                    AvailableActions: [],
                    Notices: [],
                    Debug: null)),
            ActionHandler = new PlaceholderActionHandler(),
            LogStream = new InMemoryLogStream(),
            PerspectiveProvider = new DefaultPerspectiveProvider(),
            ScreenshotProvider = new FixedScreenshotProvider(),
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC bridge host running.")),
        });
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleGetScreenshot(new ScreenshotRequest());

        Assert.NotNull(result.Success);
        Assert.Equal(DataSource.Live, result.Success.Source);
        Assert.Equal("png", result.Success.Format);
        Assert.Equal((uint)2, result.Success.Width);
        Assert.Equal((uint)1, result.Success.Height);
        Assert.Equal("combat", result.Success.ScreenType);
        Assert.Equal("screen:combat:live", result.Success.ScreenInstanceId);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4e, 0x47 }, result.Success.Contents.ToByteArray());
    }

    [Fact]
    public void ScreenshotPassesViewportOverrideAndReturnsViewportMetadata()
    {
        var provider = new ViewportAwareScreenshotProvider();
        var runtime = BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = new FixedSnapshotExtractor(
                new GameStateSnapshot(
                    SchemaVersion: "spirectl/v0",
                    GameVersion: "sts2-live",
                    BridgeVersion: BridgeBuildInfo.BridgeVersion,
                    Source: DataSourceKind.Live,
                    Provisional: false,
                    ScreenType: "combat",
                    ScreenTitle: "Combat",
                    ScreenInstanceId: "screen:combat:live",
                    ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, "p1", UsesDefault: true),
                    Menu: null,
                    Lobby: null,
                    Run: null,
                    Combat: null,
                    Choices: [],
                    AvailableActions: [],
                    Notices: [],
                    Debug: null)),
            ActionHandler = new PlaceholderActionHandler(),
            LogStream = new InMemoryLogStream(),
            PerspectiveProvider = new DefaultPerspectiveProvider(),
            ScreenshotProvider = provider,
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("ipc", true, "Live IPC bridge host running.")),
        });
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleGetScreenshot(new ScreenshotRequest
        {
            ViewportWidth = 1280,
            ViewportHeight = 720,
        });

        Assert.NotNull(result.Success);
        Assert.Equal(1280, provider.LastRequest?.ViewportWidth);
        Assert.Equal(720, provider.LastRequest?.ViewportHeight);
        Assert.Equal((uint)1280, result.Success.RequestedViewportWidth);
        Assert.Equal((uint)720, result.Success.RequestedViewportHeight);
        Assert.Equal((uint)1280, result.Success.AppliedViewportWidth);
        Assert.Equal((uint)720, result.Success.AppliedViewportHeight);
        Assert.Equal((uint)1920, result.Success.RestoredViewportWidth);
        Assert.Equal((uint)1080, result.Success.RestoredViewportHeight);
        Assert.True(result.Success.RestoredViewport);
    }

}
