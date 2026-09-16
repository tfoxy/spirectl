using Grpc.Core;
using Google.Protobuf.WellKnownTypes;
using System.Text.Json;
using Spirectl.Sts2;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.ConsoleCommands;
using Spirectl.Sts2.Core.Debugging;
using Spirectl.Sts2.Core.HotReload;
using Spirectl.Sts2.Core.Lifecycle;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.Scenarios;
using Spirectl.Sts2.Core.SceneInspection;
using Spirectl.Sts2.Core.State;
using Spirectl.Proto.V0;
using FixtureLoadFailure = Spirectl.Sts2.Core.Fixtures.FixtureLoadFailure;
using FixtureLoadFailureCode = Spirectl.Sts2.Core.Fixtures.FixtureLoadFailureCode;
using FixtureLoadRequestSnapshot = Spirectl.Sts2.Core.Fixtures.FixtureLoadRequestSnapshot;
using RecordedFixtureCompatibilityNoteSnapshot = Spirectl.Sts2.Core.Fixtures.RecordedFixtureCompatibilityNoteSnapshot;
using RecordedFixtureFailure = Spirectl.Sts2.Core.Fixtures.RecordedFixtureFailure;
using RecordedFixtureFailureCode = Spirectl.Sts2.Core.Fixtures.RecordedFixtureFailureCode;
using RecordedFixtureMetadataSnapshot = Spirectl.Sts2.Core.Fixtures.RecordedFixtureMetadataSnapshot;
using RecordedFixtureNoticeSnapshot = Spirectl.Sts2.Core.Fixtures.RecordedFixtureNoticeSnapshot;
using RecordedFixtureRequestSnapshot = Spirectl.Sts2.Core.Fixtures.RecordedFixtureRequestSnapshot;
using RecordedFixtureRestoreQuality = Spirectl.Sts2.Core.Fixtures.RecordedFixtureRestoreQuality;
using RestoreFieldFidelitySnapshot = Spirectl.Sts2.Core.Restore.RestoreFieldFidelity;
using RestoreFieldReportSnapshot = Spirectl.Sts2.Core.Restore.RestoreFieldReportSnapshot;
using RestoreMismatchSnapshot = Spirectl.Sts2.Core.Restore.RestoreMismatchSnapshot;
using RestoreVerificationSnapshot = Spirectl.Sts2.Core.Restore.RestoreVerificationSnapshot;
using RestoreVerificationStatusSnapshot = Spirectl.Sts2.Core.Restore.RestoreVerificationStatus;
using RestoreLobbyCharacterSnapshot = Spirectl.Sts2.Core.Restore.LobbyCharacterSnapshot;
using MultiplayerLobbySnapshot = Spirectl.Sts2.Core.Restore.MultiplayerLobbySnapshot;
using MultiplayerPlayerSnapshot = Spirectl.Sts2.Core.Restore.MultiplayerPlayerSnapshot;
using MultiplayerRestoreLimitationSnapshot = Spirectl.Sts2.Core.Restore.MultiplayerRestoreLimitationSnapshot;
using MultiplayerRestoreModeSnapshot = Spirectl.Sts2.Core.Restore.MultiplayerRestoreModeSnapshot;
using MultiplayerRestoreResultSnapshot = Spirectl.Sts2.Core.Restore.MultiplayerRestoreResultSnapshot;
using MultiplayerRestoreSnapshot = Spirectl.Sts2.Core.Restore.MultiplayerRestoreSnapshot;

namespace Spirectl.BridgeMod.Services;

public sealed partial class GrpcBridgeService
{
    private static RuntimeSceneNodeInfo ToProtoRuntimeSceneNode(RuntimeSceneNodeSnapshot node)
    {
        var response = new RuntimeSceneNodeInfo
        {
            NodeId = node.NodeId,
            NodePath = node.NodePath,
            Name = node.Name,
            NodeType = node.NodeType,
            ParentNodePath = node.ParentNodePath ?? string.Empty,
            OwnerPath = node.OwnerPath ?? string.Empty,
            SceneFilePath = node.SceneFilePath ?? string.Empty,
            AttachedScriptPath = node.AttachedScriptPath ?? string.Empty,
            AttachedScriptType = node.AttachedScriptType ?? string.Empty,
            ChildCount = (uint)node.ChildCount,
            NativeNodeType = node.NativeNodeType ?? string.Empty,
        };
        response.Notes.Add(node.Notes);
        if (node.Properties is not null)
        {
            response.Properties = ToProtoRuntimeSceneNodeProperties(node.Properties);
        }

        if (node.ComputedTransform is not null)
        {
            response.ComputedTransform = ToProtoRuntimeSceneComputedTransform(node.ComputedTransform);
        }

        return response;
    }

    private static RuntimeSceneNodeProperties ToProtoRuntimeSceneNodeProperties(RuntimeSceneNodePropertiesSnapshot properties)
    {
        var response = new RuntimeSceneNodeProperties();
        if (properties.Visible.HasValue)
        {
            response.Visible = properties.Visible.Value;
        }

        if (properties.EffectiveVisible.HasValue)
        {
            response.EffectiveVisible = properties.EffectiveVisible.Value;
        }

        response.Position = ToProtoRuntimeSceneVector2(properties.Position);
        response.GlobalPosition = ToProtoRuntimeSceneVector2(properties.GlobalPosition);
        response.Scale = ToProtoRuntimeSceneVector2(properties.Scale);
        if (properties.RotationRadians.HasValue)
        {
            response.RotationRadians = properties.RotationRadians.Value;
        }

        response.Size = ToProtoRuntimeSceneVector2(properties.Size);
        response.PivotOffset = ToProtoRuntimeSceneVector2(properties.PivotOffset);
        response.Anchors = ToProtoRuntimeSceneAnchors(properties.Anchors);
        response.Offsets = ToProtoRuntimeSceneOffsets(properties.Offsets);
        if (properties.ZIndex.HasValue)
        {
            response.ZIndex = properties.ZIndex.Value;
        }

        if (properties.ShowBehindParent.HasValue)
        {
            response.ShowBehindParent = properties.ShowBehindParent.Value;
        }

        if (properties.ZAsRelative.HasValue)
        {
            response.ZAsRelative = properties.ZAsRelative.Value;
        }

        response.Modulate = ToProtoRuntimeSceneColor(properties.Modulate);
        response.SelfModulate = ToProtoRuntimeSceneColor(properties.SelfModulate);
        response.EffectiveModulate = ToProtoRuntimeSceneColor(properties.EffectiveModulate);
        if (properties.ClipContents.HasValue)
        {
            response.ClipContents = properties.ClipContents.Value;
        }

        if (properties.ClipChildrenMode.HasValue)
        {
            response.ClipChildrenMode = properties.ClipChildrenMode.Value;
        }

        if (properties.MouseFilter.HasValue)
        {
            response.MouseFilter = properties.MouseFilter.Value;
        }

        if (properties.FocusMode.HasValue)
        {
            response.FocusMode = properties.FocusMode.Value;
        }

        if (properties.MouseDefaultCursorShape.HasValue)
        {
            response.MouseDefaultCursorShape = properties.MouseDefaultCursorShape.Value;
        }

        if (properties.TextureRect is not null)
        {
            response.TextureRect = ToProtoRuntimeSceneTextureRect(properties.TextureRect);
        }

        if (properties.Material is not null)
        {
            response.Material = ToProtoRuntimeSceneMaterial(properties.Material);
        }

        if (properties.Layout is not null)
        {
            response.Layout = ToProtoRuntimeSceneLayout(properties.Layout);
        }

        response.Textures.Add(properties.TextureRefs.Select(ToProtoRuntimeSceneResourceRef));
        if (properties.Text is not null)
        {
            response.Text = ToProtoRuntimeSceneTextProperties(properties.Text);
        }

        if (properties.NinePatch is not null)
        {
            response.NinePatch = ToProtoRuntimeSceneNinePatch(properties.NinePatch);
        }

        response.Notices.Add(properties.Notices.Select(ToProtoRuntimeScenePropertyNotice));
        return response;
    }

    private static RuntimeSceneLayoutProperties ToProtoRuntimeSceneLayout(
        RuntimeSceneLayoutPropertiesSnapshot layout)
    {
        var response = new RuntimeSceneLayoutProperties
        {
            MinimumSize = ToProtoRuntimeSceneVector2(layout.MinimumSize),
            CombinedMinimumSize = ToProtoRuntimeSceneVector2(layout.CombinedMinimumSize),
            CustomMinimumSize = ToProtoRuntimeSceneVector2(layout.CustomMinimumSize),
            LayoutDirection = layout.LayoutDirection ?? string.Empty,
            ThemeTypeVariation = layout.ThemeTypeVariation ?? string.Empty,
        };
        if (layout.SizeFlagsHorizontal.HasValue)
        {
            response.SizeFlagsHorizontal = layout.SizeFlagsHorizontal.Value;
        }

        if (layout.SizeFlagsVertical.HasValue)
        {
            response.SizeFlagsVertical = layout.SizeFlagsVertical.Value;
        }

        if (layout.SizeFlagsStretchRatio.HasValue)
        {
            response.SizeFlagsStretchRatio = layout.SizeFlagsStretchRatio.Value;
        }

        if (layout.ContainerAlignment.HasValue)
        {
            response.ContainerAlignment = layout.ContainerAlignment.Value;
        }

        if (layout.FlowVertical.HasValue)
        {
            response.FlowVertical = layout.FlowVertical.Value;
        }

        response.ThemeConstants.Add(layout.ThemeConstants.Select(ToProtoRuntimeSceneThemeConstant));
        return response;
    }

    private static RuntimeSceneThemeConstant ToProtoRuntimeSceneThemeConstant(
        RuntimeSceneThemeConstantSnapshot constant)
    {
        var response = new RuntimeSceneThemeConstant
        {
            Name = constant.Name,
        };
        if (constant.Value.HasValue)
        {
            response.Value = constant.Value.Value;
        }

        return response;
    }

    private static RuntimeSceneTextureRectProperties ToProtoRuntimeSceneTextureRect(
        RuntimeSceneTextureRectPropertiesSnapshot textureRect)
        => new()
        {
            StretchMode = textureRect.StretchMode,
            ExpandMode = textureRect.ExpandMode,
            FlipH = textureRect.FlipH,
            FlipV = textureRect.FlipV,
        };

    private static RuntimeSceneMaterialProperties ToProtoRuntimeSceneMaterial(
        RuntimeSceneMaterialPropertiesSnapshot material)
    {
        var response = new RuntimeSceneMaterialProperties
        {
            Material = ToProtoRuntimeSceneResourceRefOrNull(material.Material),
            Shader = ToProtoRuntimeSceneResourceRefOrNull(material.Shader),
            BlendMode = material.BlendMode ?? string.Empty,
        };
        if (material.UseParentMaterial.HasValue)
        {
            response.UseParentMaterial = material.UseParentMaterial.Value;
        }

        response.ShaderParameters.Add(material.ShaderParameters.Select(ToProtoRuntimeSceneShaderParameter));
        return response;
    }

    private static RuntimeSceneShaderParameter ToProtoRuntimeSceneShaderParameter(
        RuntimeSceneShaderParameterSnapshot parameter)
    {
        var response = new RuntimeSceneShaderParameter
        {
            Name = parameter.Name,
            ValueKind = parameter.ValueKind,
            StringValue = parameter.StringValue ?? string.Empty,
            ColorValue = ToProtoRuntimeSceneColor(parameter.ColorValue),
            Vector2Value = ToProtoRuntimeSceneVector2(parameter.Vector2Value),
            ResourceValue = ToProtoRuntimeSceneResourceRefOrNull(parameter.ResourceValue),
        };
        if (parameter.NumberValue.HasValue)
        {
            response.NumberValue = parameter.NumberValue.Value;
        }

        if (parameter.BoolValue.HasValue)
        {
            response.BoolValue = parameter.BoolValue.Value;
        }

        return response;
    }

    private static RuntimeSceneNinePatchProperties ToProtoRuntimeSceneNinePatch(RuntimeSceneNinePatchPropertiesSnapshot ninePatch)
        => new()
        {
            Texture = ToProtoRuntimeSceneResourceRefOrNull(ninePatch.Texture),
            DrawCenter = ninePatch.DrawCenter,
            PatchMargins = new RuntimeScenePatchMargins
            {
                Left = ninePatch.PatchMargins.Left,
                Top = ninePatch.PatchMargins.Top,
                Right = ninePatch.PatchMargins.Right,
                Bottom = ninePatch.PatchMargins.Bottom,
            },
            AxisStretchHorizontal = ninePatch.AxisStretchHorizontal,
            AxisStretchVertical = ninePatch.AxisStretchVertical,
            EffectiveModulate = ToProtoRuntimeSceneColor(ninePatch.EffectiveModulate),
        };

    private static RuntimeSceneTextProperties ToProtoRuntimeSceneTextProperties(RuntimeSceneTextPropertiesSnapshot text)
    {
        var response = new RuntimeSceneTextProperties
        {
            Source = text.Source,
            DiagnosticSurface = text.DiagnosticSurface,
        };
        if (text.Text is not null)
        {
            response.Text = text.Text;
        }

        if (text.RawText is not null)
        {
            response.RawText = text.RawText;
        }

        if (text.RichTextEnabled.HasValue)
        {
            response.RichTextEnabled = text.RichTextEnabled.Value;
        }

        response.Font = ToProtoRuntimeSceneResourceRefOrNull(text.Font);
        if (text.FontSize.HasValue)
        {
            response.FontSize = text.FontSize.Value;
        }

        if (text.LineHeight.HasValue)
        {
            response.LineHeight = text.LineHeight.Value;
        }

        response.TextColor = ToProtoRuntimeSceneColor(text.TextColor);
        response.OutlineColor = ToProtoRuntimeSceneColor(text.OutlineColor);
        if (text.OutlineSize.HasValue)
        {
            response.OutlineSize = text.OutlineSize.Value;
        }
        if (text.LetterSpacing.HasValue)
        {
            response.LetterSpacing = text.LetterSpacing.Value;
        }
        if (!string.IsNullOrWhiteSpace(text.FontWeight))
        {
            response.FontWeight = text.FontWeight;
        }
        if (!string.IsNullOrWhiteSpace(text.FontStyle))
        {
            response.FontStyle = text.FontStyle;
        }
        if (text.Layout is not null)
        {
            response.Layout = ToProtoRuntimeSceneTextLayout(text.Layout);
        }
        if (text.Recipe is not null)
        {
            response.Recipe = ToProtoRuntimeSceneTextRecipe(text.Recipe);
        }
        if (!string.IsNullOrWhiteSpace(text.FontSizeSource))
        {
            response.FontSizeSource = text.FontSizeSource;
        }
        if (text.AppliedFontSize.HasValue)
        {
            response.AppliedFontSize = text.AppliedFontSize.Value;
        }
        if (text.ThemeFontSize.HasValue)
        {
            response.ThemeFontSize = text.ThemeFontSize.Value;
        }
        if (text.ConfiguredMinFontSize.HasValue)
        {
            response.ConfiguredMinFontSize = text.ConfiguredMinFontSize.Value;
        }
        if (text.ConfiguredMaxFontSize.HasValue)
        {
            response.ConfiguredMaxFontSize = text.ConfiguredMaxFontSize.Value;
        }
        if (text.RenderedMetrics is not null)
        {
            response.RenderedMetrics = ToProtoRuntimeSceneTextRenderedMetrics(text.RenderedMetrics);
        }

        if (text.Shadow is not null)
        {
            response.Shadow = ToProtoRuntimeSceneTextShadow(text.Shadow);
        }

        response.RichTextSpans.Add(text.RichTextSpans.Select(span => new RuntimeSceneRichTextSpan
        {
            Tag = span.Tag,
            Text = span.Text,
            Color = ToProtoRuntimeSceneColor(span.Color),
        }));
        response.Notices.Add(text.Notices.Select(ToProtoRuntimeScenePropertyNotice));
        return response;
    }

    private static RuntimeSceneTextRenderedMetrics ToProtoRuntimeSceneTextRenderedMetrics(RuntimeSceneTextRenderedMetricsSnapshot metrics)
    {
        var response = new RuntimeSceneTextRenderedMetrics
        {
            MetricSource = metrics.MetricSource,
        };
        if (metrics.FontAscentPx.HasValue)
        {
            response.FontAscentPx = metrics.FontAscentPx.Value;
        }
        if (metrics.FontDescentPx.HasValue)
        {
            response.FontDescentPx = metrics.FontDescentPx.Value;
        }
        if (metrics.FontHeightPx.HasValue)
        {
            response.FontHeightPx = metrics.FontHeightPx.Value;
        }
        if (metrics.ParagraphSizeWidthPx.HasValue)
        {
            response.ParagraphSizeWidthPx = metrics.ParagraphSizeWidthPx.Value;
        }
        if (metrics.ParagraphSizeHeightPx.HasValue)
        {
            response.ParagraphSizeHeightPx = metrics.ParagraphSizeHeightPx.Value;
        }
        response.Lines.Add(metrics.Lines.Select(line =>
        {
            var protoLine = new RuntimeSceneTextRenderedLineMetrics
            {
                Index = (uint)Math.Max(0, line.Index),
            };
            if (line.ParagraphAscentPx.HasValue)
            {
                protoLine.ParagraphAscentPx = line.ParagraphAscentPx.Value;
            }
            if (line.ParagraphDescentPx.HasValue)
            {
                protoLine.ParagraphDescentPx = line.ParagraphDescentPx.Value;
            }
            if (line.ParagraphLineSizeWidthPx.HasValue)
            {
                protoLine.ParagraphLineSizeWidthPx = line.ParagraphLineSizeWidthPx.Value;
            }
            if (line.ParagraphLineSizeHeightPx.HasValue)
            {
                protoLine.ParagraphLineSizeHeightPx = line.ParagraphLineSizeHeightPx.Value;
            }
            if (line.ParagraphLineWidthPx.HasValue)
            {
                protoLine.ParagraphLineWidthPx = line.ParagraphLineWidthPx.Value;
            }

            return protoLine;
        }));
        return response;
    }

    private static RuntimeSceneTextRecipe ToProtoRuntimeSceneTextRecipe(RuntimeSceneTextRecipeSnapshot recipe)
    {
        var response = new RuntimeSceneTextRecipe
        {
            Source = recipe.Source,
        };
        if (recipe.AutoSizeEnabled.HasValue)
        {
            response.AutoSizeEnabled = recipe.AutoSizeEnabled.Value;
        }
        if (recipe.MinFontSizePx.HasValue)
        {
            response.MinFontSizePx = recipe.MinFontSizePx.Value;
        }
        if (recipe.MaxFontSizePx.HasValue)
        {
            response.MaxFontSizePx = recipe.MaxFontSizePx.Value;
        }
        if (recipe.NominalFontSizePx.HasValue)
        {
            response.NominalFontSizePx = recipe.NominalFontSizePx.Value;
        }
        if (recipe.RichTextEnabled.HasValue)
        {
            response.RichTextEnabled = recipe.RichTextEnabled.Value;
        }
        if (!string.IsNullOrWhiteSpace(recipe.WrapMode))
        {
            response.WrapMode = recipe.WrapMode;
        }
        if (!string.IsNullOrWhiteSpace(recipe.BreakFlags))
        {
            response.BreakFlags = recipe.BreakFlags;
        }
        if (!string.IsNullOrWhiteSpace(recipe.JustificationFlags))
        {
            response.JustificationFlags = recipe.JustificationFlags;
        }
        if (!string.IsNullOrWhiteSpace(recipe.TextOverrunBehavior))
        {
            response.TextOverrunBehavior = recipe.TextOverrunBehavior;
        }
        if (recipe.HorizontallyBound.HasValue)
        {
            response.HorizontallyBound = recipe.HorizontallyBound.Value;
        }
        if (recipe.VerticallyBound.HasValue)
        {
            response.VerticallyBound = recipe.VerticallyBound.Value;
        }

        return response;
    }

    private static RuntimeSceneTextLayout ToProtoRuntimeSceneTextLayout(RuntimeSceneTextLayoutSnapshot layout)
    {
        var response = new RuntimeSceneTextLayout();
        if (!string.IsNullOrWhiteSpace(layout.HorizontalAlignment))
        {
            response.HorizontalAlignment = layout.HorizontalAlignment;
        }
        if (!string.IsNullOrWhiteSpace(layout.VerticalAlignment))
        {
            response.VerticalAlignment = layout.VerticalAlignment;
        }
        if (layout.BaselineOffsetPx.HasValue)
        {
            response.BaselineOffset = layout.BaselineOffsetPx.Value;
        }
        if (layout.AscentPx.HasValue)
        {
            response.Ascent = layout.AscentPx.Value;
        }
        if (layout.DescentPx.HasValue)
        {
            response.Descent = layout.DescentPx.Value;
        }
        if (layout.LineHeightPx.HasValue)
        {
            response.LineHeight = layout.LineHeightPx.Value;
        }
        if (layout.ContentWidthPx.HasValue)
        {
            response.ContentWidth = layout.ContentWidthPx.Value;
        }
        if (layout.ContentHeightPx.HasValue)
        {
            response.ContentHeight = layout.ContentHeightPx.Value;
        }
        if (layout.ClipContents.HasValue)
        {
            response.ClipContents = layout.ClipContents.Value;
        }

        response.Lines.Add(layout.Lines.Select(line =>
        {
            var proto = new RuntimeSceneTextLine
            {
                Index = (uint)Math.Max(line.Index, 0),
            };
            if (line.Text is not null)
            {
                proto.Text = line.Text;
            }
            if (line.X.HasValue)
            {
                proto.X = line.X.Value;
            }
            if (line.Y.HasValue)
            {
                proto.Y = line.Y.Value;
            }
            if (line.BaselineY.HasValue)
            {
                proto.BaselineY = line.BaselineY.Value;
            }
            if (line.Width.HasValue)
            {
                proto.Width = line.Width.Value;
            }
            if (line.Height.HasValue)
            {
                proto.Height = line.Height.Value;
            }
            if (line.Start.HasValue)
            {
                proto.Start = line.Start.Value;
            }
            if (line.End.HasValue)
            {
                proto.End = line.End.Value;
            }

            return proto;
        }));
        return response;
    }

    private static RuntimeSceneTextShadow ToProtoRuntimeSceneTextShadow(RuntimeSceneTextShadowSnapshot shadow)
    {
        var response = new RuntimeSceneTextShadow
        {
            Color = ToProtoRuntimeSceneColor(shadow.Color),
            Offset = ToProtoRuntimeSceneVector2(shadow.Offset),
            Source = shadow.Source,
        };
        if (shadow.Size.HasValue)
        {
            response.Size = shadow.Size.Value;
        }

        if (shadow.OutlineSize.HasValue)
        {
            response.OutlineSize = shadow.OutlineSize.Value;
        }

        response.StackedShadows.Add(shadow.StackedShadows.Select(stacked =>
        {
            var proto = new RuntimeSceneStackedTextShadow
            {
                Index = (uint)Math.Max(stacked.Index, 0),
                Color = ToProtoRuntimeSceneColor(stacked.Color),
                Offset = ToProtoRuntimeSceneVector2(stacked.Offset),
                Source = stacked.Source,
            };
            if (stacked.OutlineSize.HasValue)
            {
                proto.OutlineSize = stacked.OutlineSize.Value;
            }

            return proto;
        }));
        return response;
    }

    private static RuntimeSceneComputedTransform ToProtoRuntimeSceneComputedTransform(RuntimeSceneComputedTransformSnapshot computed)
    {
        var response = new RuntimeSceneComputedTransform
        {
            LocalTransform = ToProtoRuntimeSceneTransform2D(computed.LocalTransform),
            GlobalTransform = ToProtoRuntimeSceneTransform2D(computed.GlobalTransform),
            GlobalRect = ToProtoRuntimeSceneRect2(computed.GlobalRect),
            ViewportClippedRect = ToProtoRuntimeSceneRect2(computed.ViewportClippedRect),
        };
        response.Notices.Add(computed.Notices.Select(ToProtoRuntimeScenePropertyNotice));
        return response;
    }

    private static RuntimeSceneHoverTip? ToProtoRuntimeSceneHoverTip(RuntimeSceneHoverTipSnapshot? hoverTip)
    {
        if (hoverTip is null)
        {
            return null;
        }

        var response = new RuntimeSceneHoverTip
        {
            Visible = hoverTip.Visible,
            NodePath = hoverTip.NodePath ?? string.Empty,
        };
        if (hoverTip.Title is not null)
        {
            response.Title = hoverTip.Title;
        }

        if (hoverTip.Text is not null)
        {
            response.Text = hoverTip.Text;
        }

        response.Labels.Add(hoverTip.Labels.Select(label => new RuntimeSceneHoverLabel
        {
            NodePath = label.NodePath,
            Name = label.Name,
            Text = label.Text,
        }));
        response.Notes.Add(hoverTip.Notes);
        return response;
    }

    private static RuntimeTransitionBlocker ToProtoRuntimeTransitionBlocker(RuntimeTransitionBlockerSnapshot blocker)
    {
        var response = new RuntimeTransitionBlocker
        {
            Kind = blocker.Kind,
            NodePath = blocker.NodePath,
            NodeType = blocker.NodeType,
            Name = blocker.Name,
            Status = blocker.Status,
            Reason = blocker.Reason,
        };
        if (!string.IsNullOrWhiteSpace(blocker.AnimationName))
        {
            response.AnimationName = blocker.AnimationName;
        }

        if (blocker.LoopsLeft.HasValue)
        {
            response.LoopsLeft = blocker.LoopsLeft.Value;
        }

        if (blocker.Running.HasValue)
        {
            response.Running = blocker.Running.Value;
        }

        if (blocker.Infinite.HasValue)
        {
            response.Infinite = blocker.Infinite.Value;
        }

        return response;
    }

    private static RuntimeSceneVector2? ToProtoRuntimeSceneVector2(RuntimeSceneVector2Snapshot? vector)
        => vector is null ? null : new RuntimeSceneVector2 { X = vector.X, Y = vector.Y };

    private static RuntimeSceneHoverVisibility? ToProtoRuntimeSceneHoverVisibility(
        RuntimeSceneHoverVisibilitySnapshot? visibility)
    {
        if (visibility is null)
        {
            return null;
        }

        var response = new RuntimeSceneHoverVisibility
        {
            FullyVisible = visibility.FullyVisible,
            ControlRect = ToProtoRuntimeSceneRect2(visibility.ControlRect),
            VisibleRect = ToProtoRuntimeSceneRect2(visibility.VisibleRect),
        };

        foreach (var clip in visibility.ClippedBy)
        {
            response.ClippedBy.Add(new RuntimeSceneHoverClip
            {
                NodePath = clip.NodePath,
                NodeType = clip.NodeType,
                Reason = clip.Reason,
                Rect = ToProtoRuntimeSceneRect2(clip.Rect),
            });
        }

        foreach (var scroll in visibility.Scrolled)
        {
            response.Scrolled.Add(new RuntimeSceneHoverScroll
            {
                NodePath = scroll.NodePath,
                PreviousHorizontal = scroll.PreviousHorizontal,
                PreviousVertical = scroll.PreviousVertical,
                Horizontal = scroll.Horizontal,
                Vertical = scroll.Vertical,
            });
        }

        return response;
    }

    private static RuntimeSceneColor? ToProtoRuntimeSceneColor(RuntimeSceneColorSnapshot? color)
        => color is null ? null : new RuntimeSceneColor
        {
            R = color.R,
            G = color.G,
            B = color.B,
            A = color.A,
            // Defensive: the proto field is a non-null string. Live-watcher colors are populated by BuildNodeDelta,
            // but a transient deferred color (Html null) maps to "" rather than throwing.
            Html = color.Html ?? string.Empty,
        };

    private static RuntimeSceneTransform2D? ToProtoRuntimeSceneTransform2D(RuntimeSceneTransform2DSnapshot? transform)
        => transform is null ? null : new RuntimeSceneTransform2D
        {
            XAxis = ToProtoRuntimeSceneVector2(transform.XAxis),
            YAxis = ToProtoRuntimeSceneVector2(transform.YAxis),
            Origin = ToProtoRuntimeSceneVector2(transform.Origin),
        };

    private static RuntimeSceneRect2? ToProtoRuntimeSceneRect2(RuntimeSceneRect2Snapshot? rect)
        => rect is null ? null : new RuntimeSceneRect2
        {
            Position = ToProtoRuntimeSceneVector2(rect.Position),
            Size = ToProtoRuntimeSceneVector2(rect.Size),
        };

    private static RuntimeSceneAnchors? ToProtoRuntimeSceneAnchors(RuntimeSceneAnchorsSnapshot? anchors)
    {
        if (anchors is null)
        {
            return null;
        }

        var response = new RuntimeSceneAnchors();
        if (anchors.Left.HasValue)
        {
            response.Left = anchors.Left.Value;
        }

        if (anchors.Top.HasValue)
        {
            response.Top = anchors.Top.Value;
        }

        if (anchors.Right.HasValue)
        {
            response.Right = anchors.Right.Value;
        }

        if (anchors.Bottom.HasValue)
        {
            response.Bottom = anchors.Bottom.Value;
        }

        return response;
    }

    private static RuntimeSceneOffsets? ToProtoRuntimeSceneOffsets(RuntimeSceneOffsetsSnapshot? offsets)
    {
        if (offsets is null)
        {
            return null;
        }

        var response = new RuntimeSceneOffsets();
        if (offsets.Left.HasValue)
        {
            response.Left = offsets.Left.Value;
        }

        if (offsets.Top.HasValue)
        {
            response.Top = offsets.Top.Value;
        }

        if (offsets.Right.HasValue)
        {
            response.Right = offsets.Right.Value;
        }

        if (offsets.Bottom.HasValue)
        {
            response.Bottom = offsets.Bottom.Value;
        }

        return response;
    }

    private static RuntimeSceneResourceRef? ToProtoRuntimeSceneResourceRefOrNull(RuntimeSceneResourceRefSnapshot? resource)
        => resource is null ? null : ToProtoRuntimeSceneResourceRef(resource);

    private static RuntimeSceneResourceRef ToProtoRuntimeSceneResourceRef(RuntimeSceneResourceRefSnapshot resource)
        => new()
        {
            Field = resource.Field,
            ResourcePath = resource.ResourcePath,
            ResourceType = resource.ResourceType,
            ResourceName = resource.ResourceName,
        };

    private static RuntimeScenePropertyNotice ToProtoRuntimeScenePropertyNotice(RuntimeScenePropertyNoticeSnapshot notice)
        => new()
        {
            Code = notice.Code,
            Field = notice.Field,
            Message = notice.Message,
        };
}
