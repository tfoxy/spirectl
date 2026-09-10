using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Spirectl.Sts2.Live.EncounterVisuals;
using Spirectl.Proto.V0;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class EncounterVisualProtoContractTests
{
    [Fact]
    public void BridgeErrorDetailCanRoundTripStructuredDiagnostic()
    {
        var error = new BridgeError
        {
            Code = BridgeErrorCode.RuntimeFailure,
            Message = "Asset extraction failed.",
            Details =
            {
                new ErrorDetail
                {
                    Field = "scene",
                    Value = "composed://encounters/kaiser_crab_boss/background/image",
                    Note = "The live bridge rendered only fully transparent pixels while flattening the scene.",
                    Diagnostic = new Struct
                    {
                        Fields =
                        {
                            ["requestId"] = Value.ForString("request-1"),
                            ["renderTargetId"] = Value.ForString("background"),
                            ["readyRan"] = Value.ForBool(true),
                            ["alpha"] = Value.ForStruct(new Struct
                            {
                                Fields =
                                {
                                    ["nonZeroPixels"] = Value.ForNumber(0),
                                    ["rgbNonZeroBeforeAlphaNormalization"] = Value.ForBool(true),
                                },
                            }),
                            ["hiddenPartIds"] = Value.ForList(Value.ForString("crusher"), Value.ForString("rocket")),
                        },
                    },
                },
            },
        };

        var clone = BridgeError.Parser.ParseFrom(error.ToByteArray());
        Assert.Equal("request-1", clone.Details[0].Diagnostic.Fields["requestId"].StringValue);
        Assert.True(clone.Details[0].Diagnostic.Fields["readyRan"].BoolValue);
        Assert.True(clone.Details[0].Diagnostic.Fields["alpha"].StructValue.Fields["rgbNonZeroBeforeAlphaNormalization"].BoolValue);
        Assert.Equal("rocket", clone.Details[0].Diagnostic.Fields["hiddenPartIds"].ListValue.Values[1].StringValue);

        var json = JsonFormatter.Default.Format(clone);
        Assert.Contains("\"diagnostic\"", json, StringComparison.Ordinal);
        Assert.Contains("\"requestId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"rgbNonZeroBeforeAlphaNormalization\"", json, StringComparison.Ordinal);
        Assert.Contains("\"hiddenPartIds\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AssetExplainResponseCanRoundTripEncounterScenePackage()
    {
        var response = new AssetExplainResponse
        {
            RequestId = "composed://encounters/kaiser_crab_boss/scene-package",
            Source = DataSource.Live,
            Provisional = true,
            SchemaVersion = "0",
            ExplanationKind = "encounter-scene-package",
            EncounterScenePackage = new AssetEncounterScenePackage
            {
                SchemaVersion = "0",
                EncounterId = "kaiser_crab_boss",
                Viewport = new AssetEncounterViewport
                {
                    Width = 1920,
                    Height = 1080,
                    CoordinateSpace = "viewport",
                },
                Camera = new AssetEncounterCamera
                {
                    Scale = 0.75,
                    Offset = new AssetVector2 { X = 0, Y = 35 },
                    Source = "EncounterModel.GetCameraScaling/GetCameraOffset",
                    Provenance = "NKaiserCrabBossEncounter",
                },
                Background = new AssetEncounterBackground
                {
                    SourceScene = "res://scenes/backgrounds/kaiser_crab_boss/kaiser_crab_boss_background.tscn",
                    SourceQuery = "composed://encounters/kaiser_crab_boss/background/image",
                    RenderQuery = "composed://encounters/kaiser_crab_boss/background/image",
                },
                LogicalActors =
                {
                    new AssetEncounterLogicalActor
                    {
                        ActorId = "crusher",
                        SlotId = "crusher",
                        TargetRect = new AssetCompositionRect { X = 10, Y = 20, Width = 30, Height = 40 },
                        StatePartIds = { "crusher" },
                    },
                },
                VisualParts =
                {
                    new AssetEncounterVisualPart
                    {
                        PartId = "rocket",
                        ActorId = "rocket",
                        ScreenSide = "right",
                        AnatomicalSide = "right",
                        Layer = 20,
                        ViewportRect = new AssetCompositionRect { X = 100, Y = 200, Width = 300, Height = 400 },
                        SelectorDiagnostic = new AssetEncounterSelectorDiagnostic
                        {
                            PartId = "rocket",
                            Selector = "_rightRocket",
                            NormalizedSelectors = { "_rightRocket", "rightRocket" },
                            Candidates =
                            {
                                new AssetEncounterSelectorCandidate
                                {
                                    Path = "/root/KaiserCrab",
                                    Selector = "_rightRocket",
                                    Source = "field",
                                    Status = "resolved",
                                },
                            },
                            ResolvedNode = new AssetEncounterResolvedNode
                            {
                                Path = "/root/KaiserCrab/RightRocket",
                                Name = "RightRocket",
                                Type = "Node2D",
                            },
                            LocalBounds = new AssetCompositionRect { X = 1, Y = 2, Width = 3, Height = 4 },
                            VisibleBounds = new AssetCompositionRect { X = 5, Y = 6, Width = 7, Height = 8 },
                            Status = "resolved",
                            TargetStateId = "rocket-charge-up",
                            RenderTargetId = "rocket-charge-up-overlay",
                        },
                    },
                },
                States =
                {
                    new AssetEncounterVisualState
                    {
                        StateId = "rocket-charge-up",
                        AffectedPartIds = { "rocket" },
                    },
                },
                Transitions =
                {
                    new AssetEncounterVisualTransition
                    {
                        TransitionId = "rocket-charge-up",
                        Hook = "PlayRightSideChargeUpAnim",
                        AffectedPartIds = { "rocket" },
                        ActiveStateId = "rocket-charge-up",
                    },
                },
                RenderTargets =
                {
                    new AssetEncounterRenderTarget
                    {
                        TargetId = "rocket-charge-up-overlay",
                        Kind = "visual-state-overlay",
                        Query = "composed://encounters/kaiser_crab_boss/visual-state/rocket-charge-up/overlay/image",
                        Decision = new AssetEncounterRenderTargetDecision
                        {
                            TargetId = "rocket-charge-up-overlay",
                            Kind = "overlay",
                            StateId = "rocket-charge-up",
                            Decision = "hidden",
                            Reason = "Overlay hides non-state parts.",
                            AffectedPartIds = { "rocket" },
                        },
                    },
                },
                Notices =
                {
                    new AssetExplainNotice
                    {
                        Code = "base-game-catalog-v0",
                        Severity = "info",
                        Path = "encounterVisuals",
                        Message = "Base-game encounter catalog metadata is provisional.",
                        Provisional = true,
                    },
                },
                SelectorDiagnostics =
                {
                    new AssetEncounterSelectorDiagnostic
                    {
                        PartId = "missing",
                        Selector = "_missingPart",
                        NormalizedSelectors = { "_missingPart", "missingPart" },
                        Candidates =
                        {
                            new AssetEncounterSelectorCandidate
                            {
                                Path = "/root/KaiserCrab",
                                Selector = "_missingPart",
                                Source = "field",
                                Status = "searched",
                            },
                        },
                        Status = "unresolved",
                        TargetStateId = "rocket-charge-up",
                        RenderTargetId = "rocket-charge-up-overlay",
                    },
                },
            },
        };

        var clone = AssetExplainResponse.Parser.ParseFrom(response.ToByteArray());
        Assert.Equal("encounter-scene-package", clone.ExplanationKind);
        Assert.Equal("kaiser_crab_boss", clone.EncounterScenePackage.EncounterId);
        Assert.Equal(0.75, clone.EncounterScenePackage.Camera.Scale);
        Assert.Equal(35, clone.EncounterScenePackage.Camera.Offset.Y);
        Assert.Equal("right", clone.EncounterScenePackage.VisualParts[0].ScreenSide);
        Assert.Equal("_rightRocket", clone.EncounterScenePackage.VisualParts[0].SelectorDiagnostic.Selector);
        Assert.Equal("/root/KaiserCrab/RightRocket", clone.EncounterScenePackage.VisualParts[0].SelectorDiagnostic.ResolvedNode.Path);
        Assert.Equal(3, clone.EncounterScenePackage.VisualParts[0].SelectorDiagnostic.LocalBounds.Width);
        Assert.Equal("hidden", clone.EncounterScenePackage.RenderTargets[0].Decision.Decision);
        Assert.Equal("unresolved", clone.EncounterScenePackage.SelectorDiagnostics[0].Status);

        var json = JsonFormatter.Default.Format(clone);
        Assert.Contains("\"visualParts\"", json, StringComparison.Ordinal);
        Assert.Contains("\"renderTargets\"", json, StringComparison.Ordinal);
        Assert.Contains("\"screenSide\"", json, StringComparison.Ordinal);
        Assert.Contains("\"anatomicalSide\"", json, StringComparison.Ordinal);
        Assert.Contains("\"selectorDiagnostic\"", json, StringComparison.Ordinal);
        Assert.Contains("\"selectorDiagnostics\"", json, StringComparison.Ordinal);
        Assert.Contains("\"decision\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AssetExplainResponseCanRoundTripKnowledgeDemonScenePackageMetadata()
    {
        var catalog = Sts2EncounterVisualCatalog.LoadBaseGame();
        Assert.True(catalog.TryGetPackage("knowledge_demon_boss", out var package));
        var burnTarget = Assert.Single(package.RenderTargets, target => target.TargetId == "burn-fire-part");
        var burnNotice = Assert.Single(package.Notices, notice => notice.Code == "encounter-visual-vfx-live-only");

        var response = new AssetExplainResponse
        {
            RequestId = "composed://encounters/knowledge_demon_boss/scene-package",
            Source = DataSource.Live,
            Provisional = true,
            SchemaVersion = "0",
            ExplanationKind = "encounter-scene-package",
            EncounterScenePackage = new AssetEncounterScenePackage
            {
                SchemaVersion = "0",
                EncounterId = package.EncounterId,
                Viewport = new AssetEncounterViewport
                {
                    Width = 1920,
                    Height = 1080,
                    CoordinateSpace = "game-viewport-pixels",
                },
                Camera = new AssetEncounterCamera
                {
                    Scale = 0.85,
                    Offset = new AssetVector2 { X = 0, Y = 70 },
                    Source = "EncounterModel.GetCameraScaling/GetCameraOffset",
                    Provenance = "KnowledgeDemonBoss",
                },
                Background = new AssetEncounterBackground
                {
                    SourceScene = package.Background.SourceScene,
                    SourceQuery = package.Background.SourceQuery,
                    RenderQuery = package.Background.RenderQuery,
                },
            },
        };
        response.EncounterScenePackage.LogicalActors.Add(package.LogicalActors.Select(actor =>
        {
            var protoActor = new AssetEncounterLogicalActor
            {
                ActorId = actor.ActorId,
                SlotId = actor.SlotId,
                TargetRect = new AssetCompositionRect
                {
                    X = actor.TargetRect!.X,
                    Y = actor.TargetRect.Y,
                    Width = actor.TargetRect.Width,
                    Height = actor.TargetRect.Height,
                },
            };
            protoActor.StatePartIds.Add(actor.StatePartIds);
            return protoActor;
        }));
        response.EncounterScenePackage.VisualParts.Add(package.VisualParts.Select(part => new AssetEncounterVisualPart
        {
            PartId = part.PartId,
            ActorId = part.ActorId,
            ScreenSide = part.ScreenSide,
            AnatomicalSide = part.AnatomicalSide,
            Layer = part.Layer,
            ViewportRect = new AssetCompositionRect
            {
                X = part.ViewportRect!.X,
                Y = part.ViewportRect.Y,
                Width = part.ViewportRect.Width,
                Height = part.ViewportRect.Height,
            },
        }));
        response.EncounterScenePackage.States.Add(package.States.Select(state =>
        {
            var protoState = new AssetEncounterVisualState { StateId = state.StateId };
            protoState.AffectedPartIds.Add(state.AffectedPartIds);
            return protoState;
        }));
        response.EncounterScenePackage.RenderTargets.Add(new AssetEncounterRenderTarget
        {
            TargetId = burnTarget.TargetId,
            Kind = burnTarget.Kind,
            Query = burnTarget.Query,
            Decision = new AssetEncounterRenderTargetDecision
            {
                TargetId = burnTarget.TargetId,
                Kind = burnTarget.Kind,
                StateId = "heavy-attack-burnt",
                PartId = "burn-fire",
                Decision = "selector-bounds",
                Reason = "Part target isolates the provisional burn-fire selector and must be verified live.",
                AffectedPartIds = { "burn-fire" },
            },
        });
        response.EncounterScenePackage.Notices.Add(new AssetExplainNotice
        {
            Code = burnNotice.Code,
            Severity = burnNotice.Severity,
            Path = burnNotice.Path,
            Message = burnNotice.Message,
            Provisional = burnNotice.Provisional,
        });

        var clone = AssetExplainResponse.Parser.ParseFrom(response.ToByteArray());
        Assert.Equal("knowledge_demon_boss", clone.EncounterScenePackage.EncounterId);
        Assert.Equal("res://scenes/backgrounds/knowledge_demon_boss/knowledge_demon_boss_background.tscn", clone.EncounterScenePackage.Background.SourceScene);
        Assert.Contains(clone.EncounterScenePackage.VisualParts, part => part.PartId == "burn-fire" && part.AnatomicalSide == "overlay");
        Assert.Equal("selector-bounds", clone.EncounterScenePackage.RenderTargets[0].Decision.Decision);
        Assert.Equal("encounter-visual-vfx-live-only", clone.EncounterScenePackage.Notices[0].Code);

        var json = JsonFormatter.Default.Format(clone);
        Assert.Contains("\"knowledge_demon_boss\"", json, StringComparison.Ordinal);
        Assert.Contains("\"burn-fire\"", json, StringComparison.Ordinal);
        Assert.Contains("\"renderTargets\"", json, StringComparison.Ordinal);
        Assert.Contains("\"notices\"", json, StringComparison.Ordinal);
    }
}
