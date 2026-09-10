using System.Diagnostics;
using System.Text.Json;
using Spirectl.Sts2.Embedding;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

/// <summary>Guards the reduced embedded assembly against bridge-only ownership regressions.</summary>
public sealed class Sts2AssemblyBoundaryTests
{
    private static readonly string[] RequiredTypes =
    [
        "Spirectl.Sts2.Sts2EmbeddableRuntimeFactory",
        "Spirectl.Sts2.Embedding.ISpirectlRuntime",
        "Spirectl.Sts2.Embedding.SpirectlRuntimeFacade",
        "Spirectl.Sts2.Embedding.IRuntimeCapabilitySource",
        "Spirectl.Sts2.Embedding.IRuntimeAssetSource",
        "Spirectl.Sts2.Embedding.IRuntimeStateSource",
        "Spirectl.Sts2.Embedding.ICombatEventSource",
        "Spirectl.Sts2.Embedding.IAnimationHintSource",
        "Spirectl.Sts2.Embedding.IRuntimeSceneDeltaSource",
        "Spirectl.Sts2.Embedding.IGameModelSource",
        "Spirectl.Sts2.Embedding.IGameReferenceSource",
        "Spirectl.Sts2.Embedding.ISpineCatalogSource",
        "Spirectl.Sts2.Embedding.ISemanticActionSource",
        "Spirectl.Sts2.Embedding.IRuntimeSceneWatchControlSource",
        "Spirectl.Sts2.Embedding.IRuntimeSceneWatchControls",
        "Spirectl.Sts2.Embedding.TweenAnimationHint",
        "Spirectl.Sts2.Embedding.CombatWatchEvent",
        "Spirectl.Sts2.Live.Sts2MainThreadDispatcher",
        "Spirectl.Sts2.Live.Sts2SceneWatchRuntimeSettings",
        "Spirectl.Sts2.Live.Sts2RenderEncodeBudget",
        "Spirectl.Sts2.Live.Sts2RenderPhaseProfile",
        "Spirectl.Sts2.Live.Sts2ContentKey",
        "Spirectl.Sts2.Core.SceneInspection.SpirectlSceneStreamMeta",
        "Spirectl.Sts2.Core.SceneInspection.RuntimeSceneDelta",
        "Spirectl.Sts2.Core.Artifacts.ISpineGeoClipBaker",
        "Spirectl.Sts2.Live.Sts2SpineReprobeGate",
        "Spirectl.Sts2.Core.State.BridgeRuntimeObservation",
        "Spirectl.Sts2.Core.State.DebugStateSnapshot",
    ];

    private static readonly string[] ForbiddenTypeFragments =
    [
        "BridgeRuntime", "Fixture", "Scenario", "DebugControl", "DevelopmentAction", "Console",
        "HotReload", "Lifecycle", "ModInspector", "ModInspection", "Screenshot", "RuntimeSceneProvider",
        "RuntimeSceneQuery", "RuntimeSceneMutation", "CombatPreview", "MapDrawing", "RestoreFidelity",
        "RestoreDiagnos", "Transport", "ProducerWalkProfile", "StateWatchProfile", "PerfReport",
        "SpineGeometryProbe", "ScriptBacktrace", "Backtrace",
    ];

    private static readonly HashSet<string> ExplicitlyRetainedTypes = new(StringComparer.Ordinal)
    {
        "Spirectl.Sts2.Live.Sts2CardFlightVfxProbe",
        "Spirectl.Sts2.Live.Sts2RenderPhaseProfile",
        "Spirectl.Sts2.Core.State.BridgeRuntimeObservation",
        "Spirectl.Sts2.Core.State.DebugStateSnapshot",
        "Spirectl.Sts2.Live.Sts2CombatPreviewCore",
        "Spirectl.Sts2.Core.Map.Sts2MapDrawingTransform",
    };

    [Fact]
    public void ReflectionVisibleSharedTypesKeepOnlyTheEmbeddedSurface()
    {
        var sharedAssembly = typeof(ISpirectlRuntime).Assembly;
        Assert.Equal("Spirectl.Sts2", sharedAssembly.GetName().Name);

        var names = sharedAssembly
            .GetTypes()
            .Select(type => type.FullName ?? type.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(RequiredTypes, type => Assert.Contains(type, names));

        var forbidden = names
            .Where(type => !ExplicitlyRetainedTypes.Contains(type))
            .Where(type => ForbiddenTypeFragments.Any(fragment => type.Contains(fragment, StringComparison.Ordinal)))
            .OrderBy(type => type, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(forbidden);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EvaluatedCompileItemsExcludeBridgeOnlyFamilies(bool enableLiveHost)
    {
        var compileItems = EvaluateCompileItems(enableLiveHost);
        Assert.NotEmpty(compileItems);

        var forbidden = compileItems
            .Where(IsForbiddenCompileItem)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(forbidden);
        Assert.Contains(compileItems, path => path.EndsWith("Sts2EmbeddableRuntimeFactory.cs", StringComparison.Ordinal));
        Assert.Contains(compileItems, path => path.EndsWith("ISpirectlRuntime.cs", StringComparison.Ordinal));
        Assert.Contains(compileItems, path => path.EndsWith("SpirectlRuntimeFacade.cs", StringComparison.Ordinal));

        if (enableLiveHost)
        {
            Assert.Contains(compileItems, path => path.EndsWith("Sts2RuntimeSceneWatcher.cs", StringComparison.Ordinal));
            Assert.Contains(compileItems, path => path.EndsWith("Sts2CardFlightVfxProbe.cs", StringComparison.Ordinal));
            Assert.Contains(compileItems, path => path.EndsWith("Sts2SpineReprobeGate.cs", StringComparison.Ordinal));
        }
    }

    private static bool IsForbiddenCompileItem(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName is "Sts2CardFlightVfxProbe.cs" or "Sts2RenderPhaseProfile.cs"
            or "Sts2CombatPreviewCore.cs" or "CombatPreviewModels.cs" or "Sts2MapDrawingTransform.cs"
            or "BridgeRuntimeObservation.cs")
        {
            return false;
        }

        return ForbiddenTypeFragments.Any(fragment => fileName.Contains(fragment, StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> EvaluateCompileItems(bool enableLiveHost)
    {
        var root = FindRepositoryRoot();
        var project = Path.Combine(root, "bridge-mod/src/Spirectl.Sts2/Spirectl.Sts2.csproj");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("msbuild");
        start.ArgumentList.Add(project);
        start.ArgumentList.Add("-nologo");
        start.ArgumentList.Add("-getItem:Compile");
        start.ArgumentList.Add($"-p:EnableSts2LiveHost={enableLiveHost.ToString().ToLowerInvariant()}");

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet msbuild.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(30)), "Timed out evaluating the shared project.");
        Assert.True(process.ExitCode == 0, $"dotnet msbuild failed: {error}");

        using var document = JsonDocument.Parse(output);
        return document.RootElement
            .GetProperty("Items")
            .GetProperty("Compile")
            .EnumerateArray()
            .Select(item => item.GetProperty("FullPath").GetString())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "spirectl.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root.");
    }
}
