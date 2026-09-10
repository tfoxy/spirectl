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
    public void RuntimeSceneTextDiagnosticsExtractMegaLabelText()
    {
        var text = Sts2RuntimeSceneTextDiagnostics.Describe(
            new MegaCrit.Sts2.UI.MegaLabel("Choose carefully"));

        Assert.NotNull(text);
        Assert.Equal("Choose carefully", text.Text);
        Assert.Null(text.RawText);
        Assert.Null(text.RichTextEnabled);
        Assert.Equal("mega-label", text.Source);
        Assert.Equal("dev.runtime_scene.text", text.DiagnosticSurface);
        Assert.Contains(text.Notices, notice => notice.Code == "dev_scene_text");
    }

    [Fact]
    public void RuntimeSceneTextDiagnosticsExtractMegaRichTextLabelText()
    {
        var text = Sts2RuntimeSceneTextDiagnostics.Describe(
            new MegaCrit.Sts2.UI.MegaRichTextLabel(
                formattedText: "[wave]Hello[/wave]",
                rawText: "{wave}Hello{/wave}",
                richTextEnabled: true));

        Assert.NotNull(text);
        Assert.Equal("[wave]Hello[/wave]", text.Text);
        Assert.Equal("{wave}Hello{/wave}", text.RawText);
        Assert.True(text.RichTextEnabled);
        Assert.Equal("mega-rich-text-label", text.Source);
    }

    [Fact]
    public void RuntimeSceneTextDiagnosticsExtractActualMegaLabelShadowFromLabelSettings()
    {
        var text = Sts2RuntimeSceneTextDiagnostics.Describe(
            new MegaCrit.Sts2.addons.mega_text.MegaLabel("El Monarca"));

        Assert.NotNull(text);
        Assert.Equal("mega-label", text.Source);
        Assert.Equal(24, text.FontSize);
        Assert.Equal(24, text.AppliedFontSize);
        Assert.Equal("mega-text._lastSetSize", text.FontSizeSource);
        Assert.Equal("#CCB299FF", text.TextColor?.Html);
        Assert.Equal("#1A0D05FF", text.OutlineColor?.Html);
        Assert.Equal(2, text.OutlineSize);
        Assert.NotNull(text.Shadow);
        Assert.Equal("#00000080", text.Shadow.Color?.Html);
        Assert.Equal(4, text.Shadow.Offset?.X);
        Assert.Equal(5, text.Shadow.Offset?.Y);
        Assert.Equal(6, text.Shadow.Size);
        Assert.Equal("label-settings", text.Shadow.Source);
        var stacked = Assert.Single(text.Shadow.StackedShadows);
        Assert.Equal(0, stacked.Index);
        Assert.Equal("#00000040", stacked.Color?.Html);
        Assert.Equal(1, stacked.Offset?.X);
        Assert.Equal(2, stacked.Offset?.Y);
        Assert.Equal(0.75, stacked.OutlineSize);
    }

    [Fact]
    public void RuntimeSceneTextDiagnosticsExtractActualMegaRichTextShadowFromTheme()
    {
        var text = Sts2RuntimeSceneTextDiagnostics.Describe(
            new MegaCrit.Sts2.addons.mega_text.MegaRichTextLabel("Description"));

        Assert.NotNull(text);
        Assert.Equal("mega-rich-text-label", text.Source);
        Assert.Equal("#E6CCB2FF", text.TextColor?.Html);
        Assert.Equal(24, text.FontSize);
        Assert.Equal(24, text.AppliedFontSize);
        Assert.Equal(24, text.ThemeFontSize);
        Assert.Equal("mega-text._lastSetSize", text.FontSizeSource);
        Assert.Equal(29, text.LineHeight);
        Assert.NotNull(text.Shadow);
        Assert.Equal("#00000073", text.Shadow.Color?.Html);
        Assert.Equal(2, text.Shadow.Offset?.X);
        Assert.Equal(3, text.Shadow.Offset?.Y);
        Assert.Equal("theme:font_shadow_color", text.Shadow.Source);
        Assert.Empty(text.Shadow.StackedShadows);
    }

    [Fact]
    public void RuntimeSceneTextDiagnosticsPrefersThemeFontSizeBeforeMegaLabelMaxFallback()
    {
        var text = Sts2RuntimeSceneTextDiagnostics.Describe(
            new MegaCrit.Sts2.addons.mega_text.MegaLabel(
                "80/80",
                lastAdjustedSize: null,
                lastSetSize: null,
                maxFontSize: 100,
                minFontSize: 8,
                themeFontSize: 28));

        Assert.NotNull(text);
        Assert.Equal("mega-label", text.Source);
        Assert.Equal(28, text.FontSize);
        Assert.Null(text.AppliedFontSize);
        Assert.Equal(28, text.ThemeFontSize);
        Assert.Equal("theme:font_size", text.FontSizeSource);
        Assert.NotNull(text.Recipe);
        Assert.Equal(28, text.Recipe.NominalFontSizePx);
        Assert.Equal(100, text.Recipe.MaxFontSizePx);
        Assert.Equal(8, text.Recipe.MinFontSizePx);
        Assert.True(text.Recipe.AutoSizeEnabled);
        Assert.True(text.Recipe.HorizontallyBound);
        Assert.True(text.Recipe.VerticallyBound);
    }

    [Fact]
    public void RuntimeSceneTextDiagnosticsUsesAppliedMegaRichTextFontSizeBeforeThemeFontSize()
    {
        var text = Sts2RuntimeSceneTextDiagnostics.Describe(
            new MegaCrit.Sts2.addons.mega_text.MegaRichTextLabel(
                "Description",
                lastAdjustedSize: null,
                lastSetSize: 22,
                maxFontSize: 100,
                minFontSize: 8,
                themeFontSize: 24));

        Assert.NotNull(text);
        Assert.Equal("mega-rich-text-label", text.Source);
        Assert.Equal(22, text.FontSize);
        Assert.Equal(22, text.AppliedFontSize);
        Assert.Equal(24, text.ThemeFontSize);
        Assert.Equal("mega-text._lastSetSize", text.FontSizeSource);
        Assert.Equal(29, text.LineHeight);
        Assert.NotNull(text.Recipe);
        Assert.Equal(24, text.Recipe.NominalFontSizePx);
        Assert.Equal(100, text.Recipe.MaxFontSizePx);
        Assert.Equal(8, text.Recipe.MinFontSizePx);
        Assert.True(text.Recipe.AutoSizeEnabled);
        Assert.False(text.Recipe.HorizontallyBound);
        Assert.True(text.Recipe.VerticallyBound);
    }

    [Fact]
    public void RuntimeSceneTextDiagnosticsDoesNotUseLastAdjustedSizeAsFontSize()
    {
        var text = Sts2RuntimeSceneTextDiagnostics.Describe(
            new MegaCrit.Sts2.addons.mega_text.MegaRichTextLabel(
                "Description",
                lastAdjustedSize: 44,
                lastSetSize: null,
                maxFontSize: 100,
                minFontSize: 8,
                themeFontSize: 24));

        Assert.NotNull(text);
        Assert.Equal(24, text.FontSize);
        Assert.Null(text.AppliedFontSize);
        Assert.Equal(24, text.ThemeFontSize);
        Assert.Equal("theme:normal_font_size", text.FontSizeSource);
    }

    [Fact]
    public void RuntimeSceneTextDiagnosticsReportsRenderedMetricsSeparatelyFromFontSize()
    {
        var text = Sts2RuntimeSceneTextDiagnostics.Describe(
            new MegaCrit.Sts2.addons.mega_text.MegaRichTextLabel(
                "Line one\nLine two",
                lastAdjustedSize: 44,
                lastSetSize: null,
                maxFontSize: 100,
                minFontSize: 8,
                themeFontSize: 24));

        Assert.NotNull(text);
        Assert.Equal(24, text.FontSize);
        Assert.Null(text.AppliedFontSize);
        Assert.Equal("theme:normal_font_size", text.FontSizeSource);
        Assert.NotNull(text.RenderedMetrics);
        Assert.Contains("godot-font", text.RenderedMetrics.MetricSource);
        Assert.Contains("node-line-metrics", text.RenderedMetrics.MetricSource);
        Assert.NotNull(text.RenderedMetrics.FontAscentPx);
        Assert.NotNull(text.RenderedMetrics.FontDescentPx);
        Assert.NotNull(text.RenderedMetrics.FontHeightPx);
        Assert.Equal(20.64, text.RenderedMetrics.FontAscentPx.Value, 2);
        Assert.Equal(5.76, text.RenderedMetrics.FontDescentPx.Value, 2);
        Assert.Equal(26.4, text.RenderedMetrics.FontHeightPx.Value, 2);
        var line = Assert.Single(text.RenderedMetrics.Lines, line => line.Index == 0);
        Assert.Equal(18, line.ParagraphAscentPx);
        Assert.Equal(6, line.ParagraphDescentPx);
        Assert.Equal(24, line.ParagraphLineSizeHeightPx);
        Assert.Equal(120, line.ParagraphLineWidthPx);
    }

    [Fact]
    public void RuntimeSceneTextDiagnosticsIgnoresFontVariationGlyphSpacingAsLetterSpacing()
    {
        var text = Sts2RuntimeSceneTextDiagnostics.Describe(
            new MegaCrit.Sts2.addons.mega_text.MegaLabel(
                "80/80",
                spacingGlyph: 1));

        Assert.NotNull(text);
        Assert.Null(text.LetterSpacing);
        Assert.Equal("res://themes/kreon_bold_glyph_space_one.tres", text.Font?.ResourcePath);
    }

    [Fact]
    public void RuntimeSceneTextDiagnosticsExtractExplicitLetterSpacing()
    {
        var text = Sts2RuntimeSceneTextDiagnostics.Describe(
            new MegaCrit.Sts2.addons.mega_text.MegaLabel(
                "80/80",
                spacingGlyph: 1,
                letterSpacing: 2));

        Assert.NotNull(text);
        Assert.Equal(2, text.LetterSpacing);
        Assert.Equal("res://themes/kreon_bold_glyph_space_one.tres", text.Font?.ResourcePath);
    }

    [Fact]
    public void RuntimeSceneTextDiagnosticsReturnNoticeForThrowingTextProperty()
    {
        var text = Sts2RuntimeSceneTextDiagnostics.Describe(
            new MegaCrit.Sts2.UI.ThrowingMegaLabel());

        Assert.NotNull(text);
        Assert.Null(text.Text);
        var notice = Assert.Single(text.Notices, notice => notice.Code == "inaccessible");
        Assert.Equal("properties.text.Text", notice.Field);
        Assert.Equal("text unavailable", notice.Message);
    }

    [Fact]
    public void HandshakeReturnsSupportedActions()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var result = service.HandleHandshake(new HandshakeRequest
        {
            CliVersion = "0.0.0",
            RequestedSchemaVersion = "spirectl/v0",
            Mode = "normal",
            TransportKind = TransportKind.Mock,
        });

        Assert.NotNull(result.Success);
        Assert.Equal("spirectl/v0", result.Success.SchemaVersion);
        Assert.Equal(TransportKind.Mock, result.Success.TransportKind);
        Assert.Equal(AttachmentState.Stubbed, result.Success.AttachmentState);
        Assert.Contains(result.Success.Capabilities, capability => capability.Id == "runtime-scene-inspection");
        Assert.Contains(result.Success.Capabilities, capability => capability.Id == "state-watch");
        var remoteOrchestration = Assert.Single(result.Success.Capabilities, capability => capability.Id == "remote-client-orchestration").RemoteOrchestration;
        Assert.NotNull(remoteOrchestration);
        Assert.Equal("local-only-degraded", remoteOrchestration.Id);
        Assert.Equal(RemoteClientOrchestrationState.LocalOnlyDegraded, remoteOrchestration.State);
        var hostLocalSeatOrchestration = Assert.Single(result.Success.Capabilities, capability => capability.Id == "host-local-seat-orchestration").RemoteOrchestration;
        Assert.NotNull(hostLocalSeatOrchestration);
        Assert.Equal("host-local-seat", hostLocalSeatOrchestration.Id);
        Assert.Equal(RemoteClientOrchestrationState.HostLocalSeat, hostLocalSeatOrchestration.State);
        var supportedActions = result.Success.SupportedActions.ToDictionary(action => action.Id);
        Assert.Equal(53, supportedActions.Count);
        Assert.Equal(ActionStatus.Implemented, supportedActions["view-draw-pile"].Status);
        Assert.Equal(ActionStatus.Implemented, supportedActions["view-discard-pile"].Status);
        Assert.Equal(ActionStatus.Implemented, supportedActions["view-exhaust-pile"].Status);
        Assert.Equal(ActionStatus.Implemented, supportedActions["inspect-relic"].Status);
        Assert.Equal("relicId", supportedActions["inspect-relic"].Parameters[0].Name);
        Assert.Equal(ActionStatus.Implemented, supportedActions["close-inspect-relic"].Status);
        Assert.Equal(ActionStatus.Scaffolded, supportedActions["play-card"].Status);
        Assert.Equal("mapNodeId", supportedActions["select-map-node"].Parameters[0].Name);
        Assert.Equal(ActionStatus.Implemented, supportedActions["select-character"].Status);
        Assert.Equal("characterId", supportedActions["select-character"].Parameters[0].Name);
        Assert.Equal(ActionStatus.Implemented, supportedActions["ready"].Status);
        Assert.Empty(supportedActions["ready"].Parameters);
        Assert.Equal(ActionStatus.Implemented, supportedActions["unready"].Status);
        Assert.Empty(supportedActions["unready"].Parameters);
        Assert.Equal(ActionStatus.Implemented, supportedActions["confirm-selection"].Status);
        Assert.Equal(ActionStatus.Implemented, supportedActions["cancel-selection"].Status);
        Assert.Empty(supportedActions["confirm-selection"].Parameters);
        Assert.Empty(supportedActions["cancel-selection"].Parameters);
        Assert.Equal(ActionStatus.Scaffolded, supportedActions["claim-reward"].Status);
        Assert.Equal("rewardId", supportedActions["claim-reward"].Parameters[0].Name);
        Assert.Equal(ActionStatus.Scaffolded, supportedActions["buy-card"].Status);
        Assert.Equal("shopItemId", supportedActions["buy-card"].Parameters[0].Name);
        Assert.Equal(ActionStatus.Scaffolded, supportedActions["use-rest-site-option"].Status);
        Assert.Equal("restOptionId", supportedActions["use-rest-site-option"].Parameters[0].Name);
        Assert.Equal(ActionStatus.Scaffolded, supportedActions["take-relic"].Status);
        Assert.Equal("relicId", supportedActions["take-relic"].Parameters[0].Name);
        Assert.Equal(ActionStatus.Scaffolded, supportedActions["select-event-option"].Status);
        Assert.Equal("eventOptionId", supportedActions["select-event-option"].Parameters[0].Name);
        Assert.Equal(ActionStatus.Implemented, supportedActions["toggle-map"].Status);
        Assert.Equal(ActionStatus.Implemented, supportedActions["toggle-deck"].Status);
        Assert.Equal(ActionStatus.Implemented, supportedActions["toggle-settings"].Status);
        Assert.Equal(ActionStatus.Implemented, supportedActions["sort-deck-view"].Status);
        Assert.Equal("by", supportedActions["sort-deck-view"].Parameters[0].Name);
        Assert.Equal(ActionStatus.Implemented, supportedActions["toggle-deck-view-upgrades"].Status);
        Assert.Equal(ActionStatus.Implemented, supportedActions["select-hand-card"].Status);
        Assert.Equal("cardId", supportedActions["select-hand-card"].Parameters[0].Name);
        Assert.Equal(ActionStatus.Implemented, supportedActions["deselect-hand-card"].Status);
        Assert.Equal("cardId", supportedActions["deselect-hand-card"].Parameters[0].Name);
        Assert.Equal(ActionStatus.Implemented, supportedActions["confirm-hand-selection"].Status);
        // confirm-hand-selection can stage its selection in one call (repeatable cardId).
        Assert.Equal("cardId", supportedActions["confirm-hand-selection"].Parameters[0].Name);
    }

    [Fact]
    public void HandshakeIncludesMouseClickInDangerousMode()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var result = service.HandleHandshake(new HandshakeRequest
        {
            CliVersion = "0.0.0",
            RequestedSchemaVersion = "spirectl/v0",
            Mode = "dangerous",
            TransportKind = TransportKind.Mock,
        });

        Assert.NotNull(result.Success);
        var supportedActions = result.Success.SupportedActions.ToDictionary(action => action.Id);
        Assert.Equal(54, supportedActions.Count);
        Assert.True(supportedActions.ContainsKey("mouse-click"));
        Assert.True(supportedActions.ContainsKey("view-draw-pile"));
        Assert.True(supportedActions.ContainsKey("ready"));
        Assert.True(supportedActions.ContainsKey("select-character"));
        Assert.True(supportedActions.ContainsKey("confirm-selection"));
        Assert.True(supportedActions.ContainsKey("cancel-selection"));
        Assert.True(supportedActions["mouse-click"].Provisional);
        Assert.Equal("x", supportedActions["mouse-click"].Parameters[0].Name);
        Assert.Equal("y", supportedActions["mouse-click"].Parameters[1].Name);
        Assert.Equal("button", supportedActions["mouse-click"].Parameters[2].Name);
    }

    [Fact]
    public async Task HotReloadReportWarningsWithoutPhaseMapToEmptyProtoPhase()
    {
        var scaffold = BridgeRuntimeBootstrap.CreateScaffold();
        var service = new GrpcBridgeService(BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = scaffold.StateExtractor,
            ActionHandler = scaffold.ActionHandler,
            LogStream = scaffold.LogStream,
            PerspectiveProvider = scaffold.PerspectiveProvider,
            FixtureLoader = scaffold.FixtureLoader,
            ScreenshotProvider = scaffold.ScreenshotProvider,
            AssetExtractor = scaffold.AssetExtractor,
            AssetExplainer = scaffold.AssetExplainer,
            AssetCatalogProvider = scaffold.AssetCatalogProvider,
            SpineCatalogProvider = scaffold.SpineCatalogProvider,
            SpineGeoClipBaker = scaffold.SpineGeoClipBaker,
            DebugControl = scaffold.DebugControl,
            RuntimeSceneProvider = scaffold.RuntimeSceneProvider,
            LifecycleControl = scaffold.LifecycleControl,
            ScenarioProvider = scaffold.ScenarioProvider,
            RecordedFixtureProvider = scaffold.RecordedFixtureProvider,
            BridgeHost = scaffold.BridgeHost,
            HotReloadControl = new WarningHotReloadControl(),
        }));

        var result = await service.HandleRequestHotReloadAsync(new HotReloadRequest
        {
            RequestId = "hr-warning",
            ProjectId = "my-hot-mod",
            ShellModId = "warning-shell",
            LogicArtifactPath = "/tmp/MyHotMod.Logic.dll",
            ExpectedContractVersion = 0,
            WaitForCompletion = true,
            TimeoutMs = 30_000,
        });

        Assert.NotNull(result.Success);
        var warning = Assert.Single(result.Success.Report.Warnings);
        Assert.Equal("reload_scheduled", warning.Code);
        Assert.Equal("", warning.Phase);
        Assert.Equal("Reload was scheduled on the main thread.", warning.Message);
    }

    [Fact]
    public void HandshakeAdvertisesDebugControlCapability()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var result = service.HandleHandshake(new HandshakeRequest
        {
            CliVersion = "0.0.0",
            RequestedSchemaVersion = "spirectl/v0",
            Mode = "dev",
            TransportKind = TransportKind.Mock,
        });

        Assert.NotNull(result.Success);
        Assert.Contains(result.Success.Capabilities, capability => capability.Id == "debug-control");
    }

    [Fact]
    public void DebugStatusReturnsStructuredUnsupportedPayload()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var result = service.HandleGetDebugStatus(new DebugStatusRequest());

        Assert.NotNull(result.Success);
        Assert.False(result.Success.Supported);
        Assert.Equal(DebugExecutionState.Unsupported, result.Success.ExecutionState);
        Assert.Equal(DebugPauseReason.Unsupported, result.Success.PauseReason);
        Assert.False(result.Success.CanPause);
        Assert.False(result.Success.CanResume);
        Assert.True(result.Success.BreakpointManagementSupported);
        Assert.False(result.Success.BreakpointEvaluationSupported);
    }

    [Fact]
    public void DebugEventStreamReturnsStructuredUnsupportedNoticeForScaffoldRuntime()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var result = service.HandleGetDebugEvents(new DebugEventStreamRequest { FromSequence = 1, Limit = 10 });

        Assert.NotNull(result.Success);
        Assert.Empty(result.Success.Events);
        Assert.Contains(result.Success.Notices, notice => notice.Code == "debug_event_stream_unsupported");
    }

    [Fact]
    public void CloseGameReturnsStructuredUnsupportedPayloadForScaffoldRuntime()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var result = service.HandleCloseGame(new GameCloseRequest { RequestId = "close-1" });

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.NotImplemented, result.Error.Code);
        Assert.Equal("graceful game close requires the live STS2 bridge host.", result.Error.Message);
        Assert.Contains(result.Error.Details, detail => detail.Field == "command" && detail.Value == "game close");
    }

    [Fact]
    public void ConsoleCommandRejectsMissingCommand()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var result = service.HandleExecuteConsoleCommand(new ConsoleCommandRequest
        {
            RequestId = "console-empty",
            Command = " ",
        });

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.InvalidAction, result.Error.Code);
        Assert.Equal("dev console requires a command name.", result.Error.Message);
        Assert.Contains(result.Error.Details, detail => detail.Field == "command");
    }

    [Fact]
    public void ConsoleCommandUsesPlaceholderExecutorInScaffoldRuntime()
    {
        var service = new GrpcBridgeService(BridgeRuntimeBootstrap.CreateScaffold());

        var result = service.HandleExecuteConsoleCommand(new ConsoleCommandRequest
        {
            RequestId = "console-placeholder",
            Command = "help",
        });

        Assert.NotNull(result.Error);
        Assert.Equal(BridgeErrorCode.NotImplemented, result.Error.Code);
        Assert.Contains("requires the live STS2 bridge host", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Error.Details, detail => detail.Field == "command" && detail.Value == "dev console");
    }

    [Fact]
    public void ConsoleCommandMapsExecutorSuccess()
    {
        var runtime = RuntimeWithConsoleExecutor(new FixedConsoleCommandExecutor(success: true));
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleExecuteConsoleCommand(new ConsoleCommandRequest
        {
            RequestId = "console-success",
            Command = "help",
            Args = { "draw" },
        });

        Assert.NotNull(result.Success);
        Assert.Equal("console-success", result.Success.RequestId);
        Assert.Equal("help", result.Success.Command);
        Assert.Equal(["draw"], result.Success.Args);
        Assert.Equal("help draw", result.Success.Line);
        Assert.True(result.Success.Accepted);
        Assert.True(result.Success.Success);
        Assert.Equal("draw <n>", result.Success.OutputLines.Single());
        Assert.Equal(DataSource.Live, result.Success.Source);
        Assert.False(result.Success.Provisional);
        var notice = Assert.Single(result.Success.Notices);
        Assert.Equal("console-networked", notice.Code);
    }

    [Fact]
    public void ConsoleCommandMapsDeliveredFailureAsSuccessEnvelope()
    {
        var runtime = RuntimeWithConsoleExecutor(new FixedConsoleCommandExecutor(success: false));
        var service = new GrpcBridgeService(runtime);

        var result = service.HandleExecuteConsoleCommand(new ConsoleCommandRequest
        {
            RequestId = "console-rejected",
            Command = "nope",
            Line = "nope",
        });

        Assert.NotNull(result.Success);
        Assert.True(result.Success.Accepted);
        Assert.False(result.Success.Success);
        Assert.Equal("nope", result.Success.Command);
        Assert.Equal("nope", result.Success.Line);
        Assert.Equal("The command 'nope' does not exist.", result.Success.Output);
        Assert.Empty(result.Success.Notices);
    }

    [Fact]
    public void ConsoleCommandDispatchUsesDirectPathWithoutActiveRunState()
    {
        var path = ConsoleCommandDispatchPolicy.Resolve(
            isRealMultiplayer: true,
            isNetworkedCommand: true,
            hasRunState: false,
            hasLocalPlayer: false);

        Assert.Equal(ConsoleCommandDispatchPath.Direct, path);
    }

    [Fact]
    public void ConsoleCommandDispatchUsesNetworkedPathOnlyWithRunStateAndLocalPlayer()
    {
        Assert.Equal(
            ConsoleCommandDispatchPath.Networked,
            ConsoleCommandDispatchPolicy.Resolve(
                isRealMultiplayer: true,
                isNetworkedCommand: true,
                hasRunState: true,
                hasLocalPlayer: true));
        Assert.Equal(
            ConsoleCommandDispatchPath.Direct,
            ConsoleCommandDispatchPolicy.Resolve(
                isRealMultiplayer: true,
                isNetworkedCommand: true,
                hasRunState: true,
                hasLocalPlayer: false));
    }

}
