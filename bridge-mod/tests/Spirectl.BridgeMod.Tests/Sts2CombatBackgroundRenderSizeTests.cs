#if ENABLE_STS2_LIVE_HOST
extern alias GodotLive;

using GodotLive::Godot;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Live;
using System.Reflection;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// Stage-A1 size-addressable combat-background render pins: the requested-viewport override
// (ResolveRequestedViewportSize) feeds TryResolveCombatBackgroundFrame, which centers the runtime
// BgContainer framing (x-offset 23, scale 0.9) on WHATEVER viewport it is given — 2520x1080 for the
// widescreen mirror-stage capture, and byte-identical 1920x1080 values when no override rides the
// request. Plus the explicit-layer-selection honoring rules over the composition plan.
public sealed class Sts2CombatBackgroundRenderSizeTests
{
    private const string CombatBackgroundScenePath = "res://scenes/backgrounds/test_bg/test_bg_background.tscn";
    private const string Layer00A = "res://scenes/backgrounds/test_bg/layers/test_bg_bg_00_a.tscn";
    private const string Layer00C = "res://scenes/backgrounds/test_bg/layers/test_bg_bg_00_c.tscn";
    private const string Layer01B = "res://scenes/backgrounds/test_bg/layers/test_bg_bg_01_b.tscn";
    private const string ForegroundA = "res://scenes/backgrounds/test_bg/layers/test_bg_fg_a.tscn";
    private const string ForegroundC = "res://scenes/backgrounds/test_bg/layers/test_bg_fg_c.tscn";

    [Fact]
    public void CombatBackgroundFrameCentersOnTheRequestedWidescreenViewport()
    {
        var frame = ResolveFrame(new Vector2I(2520, 1080));

        Assert.Equal(new Vector2I(2520, 1080), frame.GetType().GetProperty("ViewportSize")!.GetValue(frame));
        Assert.Equal(new Vector2(1283f, 540f), frame.GetType().GetProperty("NodePosition")!.GetValue(frame));
        Assert.Equal(new Vector2(0.9f, 0.9f), frame.GetType().GetProperty("NodeScale")!.GetValue(frame));
        Assert.True(Assert.IsType<bool>(frame.GetType().GetProperty("PreserveControlSize")!.GetValue(frame)));
    }

    [Fact]
    public void CombatBackgroundFrameWithoutOverrideKeepsTodaysRootViewportValues()
    {
        var frame = ResolveFrame(new Vector2I(1920, 1080));

        Assert.Equal(new Vector2I(1920, 1080), frame.GetType().GetProperty("ViewportSize")!.GetValue(frame));
        Assert.Equal(new Vector2(983f, 540f), frame.GetType().GetProperty("NodePosition")!.GetValue(frame));
        Assert.Equal(new Vector2(0.9f, 0.9f), frame.GetType().GetProperty("NodeScale")!.GetValue(frame));
        Assert.True(Assert.IsType<bool>(frame.GetType().GetProperty("PreserveControlSize")!.GetValue(frame)));
    }

    [Fact]
    public void ResolveRequestedViewportSizeUsesTheOverrideWhenBothFieldsArePositive()
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "ResolveRequestedViewportSize",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var size = method!.Invoke(null, [CombatBackgroundRequest(renderWidth: 2520, renderHeight: 1080)]);

        Assert.Equal(new Vector2I(2520, 1080), size);
    }

    [Fact]
    public void ExplicitSelectionPlanHonorsTheNonDefaultLayerVariants()
    {
        // Deliberately NOT the deterministic-first-sorted winners (those would be 00_a / fg_a):
        // the explicit selection replaces the discovery list, so the plan must select exactly it.
        string[] explicitSelection = [Layer00C, Layer01B, ForegroundC];
        var selected = SelectedPaths(BuildPlan(["Layer_00", "Layer_01", "Foreground"], explicitSelection));

        Assert.Equal([Layer00C, Layer01B, ForegroundC], selected);
        Assert.Empty(Sts2CombatBackgroundLayerSelection.FindUnhonoredLayerPaths(explicitSelection, selected));
    }

    [Fact]
    public void ExplicitSelectionOfTheDeterministicWinnersMatchesDeterministicDiscovery()
    {
        string[] placeholders = ["Layer_00", "Layer_01", "Foreground"];
        string[] discovered = [Layer00A, Layer00C, Layer01B, ForegroundA, ForegroundC];
        var deterministic = SelectedPaths(BuildPlan(placeholders, discovered));

        var explicitPlan = SelectedPaths(BuildPlan(placeholders, [.. deterministic]));

        Assert.Equal(deterministic, explicitPlan);
    }

    [Fact]
    public void ExplicitSelectionWithUnrecognizedLayerFileIsReportedUnhonored()
    {
        string[] explicitSelection = [Layer00C, "res://scenes/backgrounds/test_bg/layers/not_a_layer.tscn"];
        var selected = SelectedPaths(BuildPlan(["Layer_00", "Foreground"], explicitSelection));

        var unhonored = Sts2CombatBackgroundLayerSelection.FindUnhonoredLayerPaths(explicitSelection, selected);

        Assert.Equal(["res://scenes/backgrounds/test_bg/layers/not_a_layer.tscn"], unhonored);
    }

    [Fact]
    public void ExplicitSelectionWithCompetingVariantsForOnePlaceholderIsReportedUnhonored()
    {
        string[] explicitSelection = [Layer00A, Layer00C];
        var selected = SelectedPaths(BuildPlan(["Layer_00", "Foreground"], explicitSelection));

        var unhonored = Sts2CombatBackgroundLayerSelection.FindUnhonoredLayerPaths(explicitSelection, selected);

        Assert.Equal([Layer00C], unhonored);
    }

    private static object ResolveFrame(Vector2I viewportSize)
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "TryResolveCombatBackgroundFrame",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var args = new object?[] { CombatBackgroundRequest(), viewportSize, null };
        Assert.True(Assert.IsType<bool>(method!.Invoke(null, args)));
        Assert.NotNull(args[2]);
        return args[2]!;
    }

    private static object BuildPlan(string[] placeholders, string[] layerPaths)
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "BuildCombatBackgroundCompositionPlan",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var plan = method!.Invoke(null, ["test_bg", placeholders, layerPaths]);
        Assert.NotNull(plan);
        return plan!;
    }

    private static List<string> SelectedPaths(object plan)
    {
        var groups = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
            plan.GetType().GetProperty("LayerGroups")!.GetValue(plan));
        return groups
            .Cast<object>()
            .Select(group => group.GetType().GetProperty("SelectedPath")!.GetValue(group) as string)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToList();
    }

    private static AssetExtractRequestSnapshot CombatBackgroundRequest(int? renderWidth = null, int? renderHeight = null)
        => new(
            "req-frame",
            "resources",
            CombatBackgroundScenePath,
            CombatBackgroundScenePath,
            "png",
            RenderWidth: renderWidth,
            RenderHeight: renderHeight);
}
#endif
