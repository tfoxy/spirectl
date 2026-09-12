using System.Text.Json;
using System.Text.Json.Nodes;
using Spirectl.DotnetTools;
using Spirectl.DotnetTools.Inspection;
using Spirectl.DotnetTools.Models;
using Spirectl.TestSymbols.Gameplay;
using Xunit;

namespace Spirectl.DotnetTools.Tests;

public sealed class ToolCommandDispatcherTests
{
    [Theory]
    [InlineData("locate", "type", "DeckController", "DeclarationsOnly")]
    [InlineData("describe", "type", "Spirectl.TestSymbols.Gameplay.DeckController", "DeclarationsOnly")]
    [InlineData("derived", "type", "Spirectl.TestSymbols.Gameplay.BaseController", "DeclarationsOnly")]
    [InlineData("refs", "type", "Spirectl.TestSymbols.Gameplay.DeckController", "WithReferences")]
    [InlineData("decompile", "type", "Spirectl.TestSymbols.Gameplay.ModdingNavigator", "WithReferences")]
    [InlineData("hooks", null, "OnDeckChanged", "WithReferences")]
    [InlineData(
        "hook-info",
        null,
        "Spirectl.TestSymbols.Gameplay.CombatDeckHook::OnCombatOpened(Spirectl.TestSymbols.Gameplay.DeckController)",
        "WithReferences")]
    public void InspectionCommandsRequestExpectedMetadataLoadMode(
        string command,
        string? subject,
        string query,
        string expectedModeName)
    {
        using var layout = TestAssemblyLayout.Create();
        var expectedMode = Enum.Parse<MetadataCatalogLoadMode>(expectedModeName);
        var requestedModes = new List<MetadataCatalogLoadMode>();
        var service = new InspectionCommandService((request, mode) =>
        {
            requestedModes.Add(mode);
            return MetadataCatalog.Load(request.AssembliesDir!, request.IncludeMods ? request.ModsDir : null, mode);
        });

        var response = service.Execute(CreateInspectionRequest(command, subject, query, layout.GameAssembliesDir));

        Assert.Equal(command, response.Command);
        Assert.Equal([expectedMode], requestedModes);
    }

    [Fact]
    public void LocateTypeReturnsStructuredJsonEnvelopeFromAssembliesDir()
    {
        using var layout = TestAssemblyLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            ["locate", "type", "DeckController", "--assemblies-dir", layout.GameAssembliesDir, "--json"]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;

        Assert.Equal("locate", root.GetProperty("command").GetString());
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal("type", root.GetProperty("subject").GetString());
        Assert.Equal("DeckController", root.GetProperty("query").GetString());
        Assert.True(root.GetProperty("matchCount").GetInt32() >= 1);
        Assert.False(root.GetProperty("truncated").GetBoolean());

        var match = root.GetProperty("matches")
            .EnumerateArray()
            .First(current => current.GetProperty("displayName").GetString() == "DeckController");
        Assert.Equal("type", match.GetProperty("kind").GetString());
        Assert.Equal("game", match.GetProperty("source").GetString());
        Assert.Contains("DeckController", match.GetProperty("fullName").GetString());
        Assert.StartsWith("type:", match.GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("disabled")]
    [InlineData("refresh")]
    public void LocateAcceptsInternalCacheOptionsWithoutChangingJsonPayload(string cacheMode)
    {
        using var layout = TestAssemblyLayout.Create();
        var cacheDir = Path.Combine(Path.GetTempPath(), $"spirectl-cache-{Guid.NewGuid():N}");
        var result = ToolCommandDispatcher.Dispatch(
            [
                "locate",
                "type",
                "DeckController",
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--cache-dir",
                cacheDir,
                "--cache-mode",
                cacheMode,
                "--json",
            ]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;

        Assert.Equal("locate", root.GetProperty("command").GetString());
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.False(root.TryGetProperty("cacheDir", out _));
        Assert.False(root.TryGetProperty("cacheMode", out _));
    }

    [Fact]
    public void ColdLocateWritesOnlyDeclarationsCacheEntry()
    {
        using var layout = TestAssemblyLayout.Create();
        var cacheDir = CreateTempCacheDir();

        var result = DispatchLocate(layout.GameAssembliesDir, cacheDir, "auto");

        Assert.Equal(0, result.ExitCode);
        var manifestPath = Assert.Single(EnumerateDeclarationManifestFiles(cacheDir));
        var assemblyEntryPaths = EnumerateDeclarationAssemblyEntryFiles(cacheDir);
        Assert.Single(assemblyEntryPaths);
        Assert.Empty(EnumerateReferenceEntryFiles(cacheDir));
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifestRoot = manifest.RootElement;
        Assert.True(manifestRoot.TryGetProperty("Identity", out _));
        var manifestAssembly = Assert.Single(manifestRoot.GetProperty("Assemblies").EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(manifestAssembly.GetProperty("EntryKey").GetString()));
        Assert.Equal(Path.GetFileNameWithoutExtension(Directory.EnumerateFiles(layout.GameAssembliesDir, "*.dll").Single()), manifestAssembly.GetProperty("AssemblyName").GetString());
    }

    [Fact]
    public void RepeatedRequireHitReusesDeclarationsCacheEntry()
    {
        using var layout = TestAssemblyLayout.Create();
        var cacheDir = CreateTempCacheDir();
        var cold = DispatchLocate(layout.GameAssembliesDir, cacheDir, "auto");
        Assert.Equal(0, cold.ExitCode);

        var repeat = DispatchLocate(layout.GameAssembliesDir, cacheDir, "require-hit");

        Assert.Equal(0, repeat.ExitCode);
        AssertJsonEquivalent(cold.Output, repeat.Output);
    }

    [Theory]
    [InlineData("refs", "type", "Spirectl.TestSymbols.Gameplay.DeckController")]
    [InlineData("decompile", "type", "Spirectl.TestSymbols.Gameplay.ModdingNavigator")]
    [InlineData("hooks", null, "OnDeckChanged")]
    [InlineData(
        "hook-info",
        null,
        "Spirectl.TestSymbols.Gameplay.CombatDeckHook::OnCombatOpened(Spirectl.TestSymbols.Gameplay.DeckController)")]
    public void ReferenceBackedCommandsWriteSeparateReferencesCacheEntry(string command, string? subject, string query)
    {
        using var layout = TestAssemblyLayout.Create(includeResourceFixtures: true);
        var cacheDir = CreateTempCacheDir();

        var cold = DispatchInspection(command, subject, query, layout.GameAssembliesDir, cacheDir, "auto");
        var repeat = DispatchInspection(command, subject, query, layout.GameAssembliesDir, cacheDir, "require-hit");

        Assert.Equal(0, cold.ExitCode);
        Assert.Equal(0, repeat.ExitCode);
        Assert.Empty(EnumerateDeclarationManifestFiles(cacheDir));
        Assert.Empty(EnumerateDeclarationAssemblyEntryFiles(cacheDir));
        Assert.Single(EnumerateReferenceEntryFiles(cacheDir));
        AssertJsonEquivalent(cold.Output, repeat.Output);
    }

    [Fact]
    public void DisabledCacheProducesNoFiles()
    {
        using var layout = TestAssemblyLayout.Create();
        var cacheDir = CreateTempCacheDir();

        var result = DispatchLocate(layout.GameAssembliesDir, cacheDir, "disabled");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(Directory.Exists(cacheDir) ? Directory.EnumerateFiles(cacheDir, "*", SearchOption.AllDirectories) : []);
    }

    [Fact]
    public void RefreshRebuildsCorruptEntries()
    {
        using var layout = TestAssemblyLayout.Create();
        var cacheDir = CreateTempCacheDir();
        Assert.Equal(0, DispatchLocate(layout.GameAssembliesDir, cacheDir, "auto").ExitCode);
        var entryPath = EnumerateDeclarationManifestFiles(cacheDir).Single();
        File.WriteAllText(entryPath, "{ corrupt json");

        var refresh = DispatchLocate(layout.GameAssembliesDir, cacheDir, "refresh");
        var requireHit = DispatchLocate(layout.GameAssembliesDir, cacheDir, "require-hit");

        Assert.Equal(0, refresh.ExitCode);
        Assert.Equal(0, requireHit.ExitCode);
        using var document = JsonDocument.Parse(File.ReadAllText(entryPath));
        Assert.True(document.RootElement.TryGetProperty("Identity", out _));
    }

    [Fact]
    public void AutoRebuildsWhenAssemblyFingerprintChanges()
    {
        using var layout = TestAssemblyLayout.Create(includeExtraGameAssembly: true);
        var cacheDir = CreateTempCacheDir();
        var cold = DispatchLocate(layout.GameAssembliesDir, cacheDir, "auto");
        Assert.Equal(0, cold.ExitCode);
        Assert.Equal(2, EnumerateDeclarationAssemblyEntryFiles(cacheDir).Count);

        var targetAssemblyName = typeof(DeckController).Assembly.GetName().Name!;
        var nonTargetAssemblyName = typeof(ToolCommandDispatcher).Assembly.GetName().Name!;
        var targetEntryPath = FindDeclarationAssemblyEntryPath(cacheDir, targetAssemblyName);
        var nonTargetEntryPath = FindDeclarationAssemblyEntryPath(cacheDir, nonTargetAssemblyName);
        File.WriteAllText(nonTargetEntryPath, "{ corrupt non-target entry must not be read");

        var uncached = ToolCommandDispatcher.Dispatch(
            [
                "describe",
                "type",
                $"type:{targetAssemblyName}:Spirectl.TestSymbols.Gameplay.DeckController",
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--json",
            ]);
        var cachedWithCorruptNonTarget = DispatchInspection(
            "describe",
            "type",
            $"type:{targetAssemblyName}:Spirectl.TestSymbols.Gameplay.DeckController",
            layout.GameAssembliesDir,
            cacheDir,
            "require-hit");

        Assert.Equal(0, uncached.ExitCode);
        Assert.Equal(0, cachedWithCorruptNonTarget.ExitCode);
        AssertNoCacheDiagnostics(cachedWithCorruptNonTarget.Output);
        AssertJsonEquivalent(uncached.Output, cachedWithCorruptNonTarget.Output);

        var targetAssemblyPath = Path.Combine(layout.GameAssembliesDir, $"{targetAssemblyName}.dll");
        File.SetLastWriteTimeUtc(targetAssemblyPath, File.GetLastWriteTimeUtc(targetAssemblyPath).AddMinutes(1));
        var rebuilt = DispatchInspection(
            "describe",
            "type",
            $"type:{targetAssemblyName}:Spirectl.TestSymbols.Gameplay.DeckController",
            layout.GameAssembliesDir,
            cacheDir,
            "auto");
        var requireHit = DispatchInspection(
            "describe",
            "type",
            $"type:{targetAssemblyName}:Spirectl.TestSymbols.Gameplay.DeckController",
            layout.GameAssembliesDir,
            cacheDir,
            "require-hit");

        Assert.Equal(0, rebuilt.ExitCode);
        Assert.Equal(0, requireHit.ExitCode);
        AssertNoCacheDiagnostics(rebuilt.Output);
        AssertNoCacheDiagnostics(requireHit.Output);
        AssertJsonEquivalent(rebuilt.Output, requireHit.Output);
        Assert.NotEqual(targetEntryPath, ReadManifestAssemblyEntryKeyPath(cacheDir, targetAssemblyName));
        Assert.Equal(nonTargetEntryPath, ReadManifestAssemblyEntryKeyPath(cacheDir, nonTargetAssemblyName));
        Assert.Equal("{ corrupt non-target entry must not be read", File.ReadAllText(nonTargetEntryPath));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("helper")]
    [InlineData("snapshot")]
    public void AutoRebuildsIncompatibleEntriesAtExpectedPath(string mutation)
    {
        using var layout = TestAssemblyLayout.Create();
        var cacheDir = CreateTempCacheDir();
        var cold = DispatchLocate(layout.GameAssembliesDir, cacheDir, "auto");
        Assert.Equal(0, cold.ExitCode);
        var entryPath = EnumerateDeclarationManifestFiles(cacheDir).Single();

        MutateCacheEntry(entryPath, mutation);
        var rebuilt = DispatchLocate(layout.GameAssembliesDir, cacheDir, "auto");
        var requireHit = DispatchLocate(layout.GameAssembliesDir, cacheDir, "require-hit");

        Assert.Equal(0, rebuilt.ExitCode);
        Assert.Equal(0, requireHit.ExitCode);
        AssertNoCacheDiagnostics(rebuilt.Output);
        AssertJsonEquivalent(rebuilt.Output, requireHit.Output);
    }

    [Fact]
    public void AutoRebuildsCorruptDeclarationManifestAtExpectedPath()
    {
        using var layout = TestAssemblyLayout.Create();
        var cacheDir = CreateTempCacheDir();
        var cold = DispatchLocate(layout.GameAssembliesDir, cacheDir, "auto");
        Assert.Equal(0, cold.ExitCode);
        var manifestPath = EnumerateDeclarationManifestFiles(cacheDir).Single();
        File.WriteAllText(manifestPath, "{ corrupt manifest json");

        var rebuilt = DispatchLocate(layout.GameAssembliesDir, cacheDir, "auto");
        var requireHit = DispatchLocate(layout.GameAssembliesDir, cacheDir, "require-hit");

        Assert.Equal(0, rebuilt.ExitCode);
        Assert.Equal(0, requireHit.ExitCode);
        AssertNoCacheDiagnostics(rebuilt.Output);
        AssertNoCacheDiagnostics(requireHit.Output);
        AssertJsonEquivalent(cold.Output, rebuilt.Output);
        AssertJsonEquivalent(rebuilt.Output, requireHit.Output);
        AssertValidDeclarationManifest(cacheDir);
    }

    [Fact]
    public void AutoRebuildsCorruptDeclarationAssemblyEntryAtExpectedPath()
    {
        using var layout = TestAssemblyLayout.Create();
        var cacheDir = CreateTempCacheDir();
        var cold = DispatchLocate(layout.GameAssembliesDir, cacheDir, "auto");
        Assert.Equal(0, cold.ExitCode);
        var entryPath = EnumerateDeclarationAssemblyEntryFiles(cacheDir).Single();
        File.WriteAllText(entryPath, "{ corrupt assembly entry json");

        var rebuilt = DispatchLocate(layout.GameAssembliesDir, cacheDir, "auto");
        var refresh = DispatchLocate(layout.GameAssembliesDir, cacheDir, "refresh");
        var requireHit = DispatchLocate(layout.GameAssembliesDir, cacheDir, "require-hit");

        Assert.Equal(0, rebuilt.ExitCode);
        Assert.Equal(0, refresh.ExitCode);
        Assert.Equal(0, requireHit.ExitCode);
        AssertNoCacheDiagnostics(rebuilt.Output);
        AssertNoCacheDiagnostics(refresh.Output);
        AssertNoCacheDiagnostics(requireHit.Output);
        AssertJsonEquivalent(cold.Output, rebuilt.Output);
        AssertJsonEquivalent(rebuilt.Output, refresh.Output);
        AssertJsonEquivalent(refresh.Output, requireHit.Output);
        AssertValidDeclarationManifest(cacheDir);
        AssertValidDeclarationAssemblyEntries(cacheDir);
    }

    [Fact]
    public async Task ParallelAutoDispatchesLeaveReusableCacheEntry()
    {
        using var layout = TestAssemblyLayout.Create();
        var cacheDir = CreateTempCacheDir();
        var cold = DispatchLocate(layout.GameAssembliesDir, cacheDir, "auto");
        Assert.Equal(0, cold.ExitCode);
        var entryPath = EnumerateDeclarationAssemblyEntryFiles(cacheDir).Single();
        File.WriteAllText(entryPath, "{ corrupt compact assembly entry");

        var results = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => DispatchLocate(layout.GameAssembliesDir, cacheDir, "auto")))
            .ToArray();
        await Task.WhenAll(results);
        var requireHit = DispatchLocate(layout.GameAssembliesDir, cacheDir, "require-hit");

        Assert.All(results, task => Assert.Equal(0, task.Result.ExitCode));
        Assert.Equal(0, requireHit.ExitCode);
        AssertNoCacheDiagnostics(requireHit.Output);
        AssertJsonEquivalent(cold.Output, requireHit.Output);
        AssertValidDeclarationManifest(cacheDir);
        AssertValidDeclarationAssemblyEntries(cacheDir);
        Assert.Single(EnumerateDeclarationManifestFiles(cacheDir));
        Assert.Single(EnumerateDeclarationAssemblyEntryFiles(cacheDir));
        Assert.Empty(EnumerateCacheTemporaryFiles(cacheDir));
    }

    [Fact]
    public void CachedExecutionPreservesUncachedJsonOutputShape()
    {
        using var layout = TestAssemblyLayout.Create();
        var cacheDir = CreateTempCacheDir();
        var uncached = ToolCommandDispatcher.Dispatch(
            ["describe", "type", "Spirectl.TestSymbols.Gameplay.DeckController", "--assemblies-dir", layout.GameAssembliesDir, "--json"]);
        var cached = ToolCommandDispatcher.Dispatch(
            [
                "describe",
                "type",
                "Spirectl.TestSymbols.Gameplay.DeckController",
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--cache-dir",
                cacheDir,
                "--json",
            ]);

        Assert.Equal(0, uncached.ExitCode);
        Assert.Equal(0, cached.ExitCode);
        AssertJsonEquivalent(uncached.Output, cached.Output);
    }

    [Fact]
    public void DescribeTargetAwareTypeStableIdUsesOnlySelectedCacheEntry()
    {
        using var layout = TestAssemblyLayout.Create();
        CopyDependencyAssembly<System.Text.Json.JsonDocument>(layout.GameAssembliesDir);
        var cacheDir = CreateTempCacheDir();
        var cold = DispatchInspectionWithExtraArgs(
            "locate",
            "type",
            "JsonDocument",
            layout.GameAssembliesDir,
            cacheDir,
            "auto",
            "--include-dependencies");
        Assert.Equal(0, cold.ExitCode);
        CorruptAssemblyCacheEntry(cacheDir, "System.Text.Json");

        var uncached = ToolCommandDispatcher.Dispatch(
            [
                "describe",
                "type",
                "type:Spirectl.DotnetTools.TestSymbols:Spirectl.TestSymbols.Gameplay.DeckController",
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--include-dependencies",
                "--json",
            ]);
        var cached = DispatchInspectionWithExtraArgs(
            "describe",
            "type",
            "type:Spirectl.DotnetTools.TestSymbols:Spirectl.TestSymbols.Gameplay.DeckController",
            layout.GameAssembliesDir,
            cacheDir,
            "require-hit",
            "--include-dependencies");

        Assert.Equal(0, uncached.ExitCode);
        Assert.Equal(0, cached.ExitCode);
        AssertJsonEquivalent(uncached.Output, cached.Output);
    }

    [Fact]
    public void DescribeTargetAwareUniqueShortMethodUsesOnlyCandidateCacheEntry()
    {
        using var layout = TestAssemblyLayout.Create();
        CopyDependencyAssembly<System.Text.Json.JsonDocument>(layout.GameAssembliesDir);
        var cacheDir = CreateTempCacheDir();
        var cold = DispatchInspectionWithExtraArgs(
            "locate",
            "type",
            "JsonDocument",
            layout.GameAssembliesDir,
            cacheDir,
            "auto",
            "--include-dependencies");
        Assert.Equal(0, cold.ExitCode);
        CorruptAssemblyCacheEntry(cacheDir, "System.Text.Json");

        var uncached = ToolCommandDispatcher.Dispatch(
            [
                "describe",
                "method",
                "DescribeCard",
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--include-dependencies",
                "--json",
            ]);
        var cached = DispatchInspectionWithExtraArgs(
            "describe",
            "method",
            "DescribeCard",
            layout.GameAssembliesDir,
            cacheDir,
            "require-hit",
            "--include-dependencies");

        Assert.Equal(0, uncached.ExitCode);
        Assert.Equal(0, cached.ExitCode);
        AssertJsonEquivalent(uncached.Output, cached.Output);
    }

    [Fact]
    public void InvalidCacheModeReturnsStructuredUsageError()
    {
        using var layout = TestAssemblyLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            [
                "locate",
                "type",
                "DeckController",
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--cache-mode",
                "always",
                "--json",
            ]);

        Assert.Equal(2, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var error = document.RootElement.GetProperty("error");

        Assert.Equal("usage_error", error.GetProperty("code").GetString());
        Assert.Contains("invalid --cache-mode 'always'", error.GetProperty("message").GetString());
    }

    [Fact]
    public void AssemblySelectionExcludesKnownGameDependenciesByDefault()
    {
        using var layout = TestAssemblyLayout.Create();
        var assemblyPath = Directory.EnumerateFiles(layout.GameAssembliesDir, "*.dll").Single();
        File.Copy(assemblyPath, Path.Combine(layout.GameAssembliesDir, "System.Private.CoreLib.dll"));
        File.Copy(assemblyPath, Path.Combine(layout.GameAssembliesDir, "Microsoft.Extensions.Logging.dll"));
        File.Copy(assemblyPath, Path.Combine(layout.GameAssembliesDir, "GodotSharp.dll"));
        File.Copy(assemblyPath, Path.Combine(layout.GameAssembliesDir, "0Harmony.dll"));
        File.Copy(assemblyPath, Path.Combine(layout.GameAssembliesDir, "MonoMod.RuntimeDetour.dll"));
        File.Copy(assemblyPath, Path.Combine(layout.GameAssembliesDir, "SmartFormat.dll"));
        File.Copy(assemblyPath, Path.Combine(layout.GameAssembliesDir, "Sentry.dll"));
        File.Copy(assemblyPath, Path.Combine(layout.GameAssembliesDir, "Steamworks.NET.dll"));

        var selected = ManagedAssemblySelector.SelectAssemblyFiles(
            new InspectionSearchRoot("game", layout.GameAssembliesDir),
            includeDependencies: false);

        Assert.Equal([Path.GetFileName(assemblyPath)], [.. selected.Select(path => Path.GetFileName(path)!)]);
    }

    [Fact]
    public void AssemblySelectionIncludeDependenciesRestoresDependencyVisibility()
    {
        using var layout = TestAssemblyLayout.Create();
        var assemblyPath = Directory.EnumerateFiles(layout.GameAssembliesDir, "*.dll").Single();
        File.Delete(assemblyPath);
        File.Copy(typeof(DeckController).Assembly.Location, Path.Combine(layout.GameAssembliesDir, "System.Private.Gameplay.dll"));

        var defaultResult = ToolCommandDispatcher.Dispatch(
            ["locate", "type", "DeckController", "--assemblies-dir", layout.GameAssembliesDir, "--json"]);
        var includeDependenciesResult = ToolCommandDispatcher.Dispatch(
            [
                "locate",
                "type",
                "DeckController",
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--include-dependencies",
                "--json",
            ]);

        Assert.Equal(0, defaultResult.ExitCode);
        using (var defaultDocument = JsonDocument.Parse(defaultResult.Output))
        {
            Assert.Equal("no-match", defaultDocument.RootElement.GetProperty("status").GetString());
        }

        Assert.Equal(0, includeDependenciesResult.ExitCode);
        using var includeDependenciesDocument = JsonDocument.Parse(includeDependenciesResult.Output);
        Assert.Equal("ok", includeDependenciesDocument.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void AssemblySelectionKeepsNonSts2ProductGameDllsByDefault()
    {
        using var layout = TestAssemblyLayout.Create();
        var assemblyPath = Directory.EnumerateFiles(layout.GameAssembliesDir, "*.dll").Single();
        var productPath = Path.Combine(layout.GameAssembliesDir, "AnotherGameProduct.dll");
        File.Copy(assemblyPath, productPath);
        File.Delete(assemblyPath);

        var selected = ManagedAssemblySelector.SelectAssemblyFiles(
            new InspectionSearchRoot("game", layout.GameAssembliesDir),
            includeDependencies: false);

        Assert.Equal([productPath], selected);
    }

    [Fact]
    public void AssemblySelectionModDllsAreSelectedOnlyWhenIncludeModsAddsModRoot()
    {
        using var layout = TestAssemblyLayout.Create(includeModCopy: true);
        var cache = CreateCache();

        var gameOnlyIdentity = cache.BuildIdentity(
            layout.GameAssembliesDir,
            layout.ModsDir,
            includeMods: false,
            MetadataCatalogLoadMode.DeclarationsOnly);
        var includeModsIdentity = cache.BuildIdentity(
            layout.GameAssembliesDir,
            layout.ModsDir,
            includeMods: true,
            MetadataCatalogLoadMode.DeclarationsOnly);

        Assert.DoesNotContain(gameOnlyIdentity.Assemblies, fingerprint => fingerprint.Source == "mod");
        Assert.Contains(includeModsIdentity.Assemblies, fingerprint => fingerprint.Source == "mod");
    }

    [Fact]
    public void AssemblySelectionCacheIdentitySeparatesFilteredAndDependencyInclusiveCatalogs()
    {
        using var layout = TestAssemblyLayout.Create();
        var assemblyPath = Directory.EnumerateFiles(layout.GameAssembliesDir, "*.dll").Single();
        File.Copy(assemblyPath, Path.Combine(layout.GameAssembliesDir, "System.Private.Gameplay.dll"));
        var cache = CreateCache();

        var filteredIdentity = cache.BuildIdentity(
            layout.GameAssembliesDir,
            modsDir: null,
            includeMods: false,
            MetadataCatalogLoadMode.DeclarationsOnly,
            includeDependencies: false);
        var dependencyInclusiveIdentity = cache.BuildIdentity(
            layout.GameAssembliesDir,
            modsDir: null,
            includeMods: false,
            MetadataCatalogLoadMode.DeclarationsOnly,
            includeDependencies: true);

        Assert.False(filteredIdentity.IncludeDependencies);
        Assert.True(dependencyInclusiveIdentity.IncludeDependencies);
        Assert.DoesNotContain(filteredIdentity.Assemblies, fingerprint => Path.GetFileName(fingerprint.Path) == "System.Private.Gameplay.dll");
        Assert.Contains(dependencyInclusiveIdentity.Assemblies, fingerprint => Path.GetFileName(fingerprint.Path) == "System.Private.Gameplay.dll");
        Assert.NotEqual(cache.BuildDeclarationsKey(filteredIdentity), cache.BuildDeclarationsKey(dependencyInclusiveIdentity));
    }

    [Fact]
    public void MetadataCatalogCacheKeyIsStableForCanonicalRootInputs()
    {
        using var layout = TestAssemblyLayout.Create();
        var cache = CreateCache();
        var plainIdentity = cache.BuildIdentity(
            layout.GameAssembliesDir,
            modsDir: null,
            includeMods: false,
            MetadataCatalogLoadMode.DeclarationsOnly);
        var trailingSeparatorIdentity = cache.BuildIdentity(
            layout.GameAssembliesDir + Path.DirectorySeparatorChar,
            modsDir: null,
            includeMods: false,
            MetadataCatalogLoadMode.DeclarationsOnly);

        Assert.Equal(cache.BuildDeclarationsKey(plainIdentity), cache.BuildDeclarationsKey(trailingSeparatorIdentity));
    }

    [Fact]
    public void MetadataCatalogCacheKeysTrackRequiredIdentityInputs()
    {
        using var layout = TestAssemblyLayout.Create(includeModCopy: true);
        using var alternateLayout = TestAssemblyLayout.Create();
        var cache = CreateCache();
        var baseline = cache.BuildIdentity(
            layout.GameAssembliesDir,
            layout.ModsDir,
            includeMods: false,
            MetadataCatalogLoadMode.DeclarationsOnly);
        var baselineKey = cache.BuildDeclarationsKey(baseline);

        var includeModsIdentity = cache.BuildIdentity(
            layout.GameAssembliesDir,
            layout.ModsDir,
            includeMods: true,
            MetadataCatalogLoadMode.DeclarationsOnly);
        var referenceModeIdentity = cache.BuildIdentity(
            layout.GameAssembliesDir,
            layout.ModsDir,
            includeMods: false,
            MetadataCatalogLoadMode.WithReferences);
        var alternateRootIdentity = cache.BuildIdentity(
            alternateLayout.GameAssembliesDir,
            modsDir: null,
            includeMods: false,
            MetadataCatalogLoadMode.DeclarationsOnly);
        var schemaIdentity = CreateCache(schemaVersion: MetadataCatalogCache.SchemaVersion + 1).BuildIdentity(
            layout.GameAssembliesDir,
            layout.ModsDir,
            includeMods: false,
            MetadataCatalogLoadMode.DeclarationsOnly);
        var helperIdentity = new MetadataCatalogCache(
            MetadataCatalogCache.SchemaVersion,
            new HelperBuildIdentity("Spirectl.DotnetTools", "test", Guid.NewGuid().ToString("D"), null))
            .BuildIdentity(
                layout.GameAssembliesDir,
                layout.ModsDir,
                includeMods: false,
                MetadataCatalogLoadMode.DeclarationsOnly);

        Assert.NotEqual(baselineKey, cache.BuildDeclarationsKey(includeModsIdentity));
        Assert.NotEqual(baselineKey, cache.BuildDeclarationsKey(referenceModeIdentity));
        Assert.NotEqual(baselineKey, cache.BuildDeclarationsKey(alternateRootIdentity));
        Assert.NotEqual(baselineKey, CreateCache(schemaVersion: MetadataCatalogCache.SchemaVersion + 1).BuildDeclarationsKey(schemaIdentity));
        Assert.NotEqual(baselineKey, cache.BuildDeclarationsKey(helperIdentity));
        Assert.NotEqual(cache.BuildDeclarationsKey(baseline), cache.BuildReferencesKey(baseline));
    }

    [Fact]
    public void MetadataCatalogCacheKeyTracksAssemblySizeAndTimestamp()
    {
        using var layout = TestAssemblyLayout.Create();
        var cache = CreateCache();
        var baseline = cache.BuildIdentity(
            layout.GameAssembliesDir,
            modsDir: null,
            includeMods: false,
            MetadataCatalogLoadMode.DeclarationsOnly);
        var baselineKey = cache.BuildDeclarationsKey(baseline);
        var assemblyPath = Directory.EnumerateFiles(layout.GameAssembliesDir, "*.dll").Single();

        File.SetLastWriteTimeUtc(assemblyPath, File.GetLastWriteTimeUtc(assemblyPath).AddMinutes(1));
        var timestampIdentity = cache.BuildIdentity(
            layout.GameAssembliesDir,
            modsDir: null,
            includeMods: false,
            MetadataCatalogLoadMode.DeclarationsOnly);
        Assert.NotEqual(baselineKey, cache.BuildDeclarationsKey(timestampIdentity));

        File.AppendAllText(assemblyPath, "cache-key-size-change");
        var sizeIdentity = cache.BuildIdentity(
            layout.GameAssembliesDir,
            modsDir: null,
            includeMods: false,
            MetadataCatalogLoadMode.DeclarationsOnly);
        Assert.NotEqual(cache.BuildDeclarationsKey(timestampIdentity), cache.BuildDeclarationsKey(sizeIdentity));
    }

    [Fact]
    public void MetadataCatalogCacheFingerprintUsesPathSizeAndTimestampOnly()
    {
        using var layout = TestAssemblyLayout.Create();
        var managedAssemblyPath = Directory.EnumerateFiles(layout.GameAssembliesDir, "*.dll").Single();
        var nativeLikePath = Path.Combine(layout.GameAssembliesDir, "native-like.dll");
        File.WriteAllBytes(nativeLikePath, [0x00, 0x01, 0x02, 0x03]);

        var managedFingerprint = MetadataCatalogCache.BuildAssemblyFingerprint("game", managedAssemblyPath);
        var nativeLikeFingerprint = MetadataCatalogCache.BuildAssemblyFingerprint("game", nativeLikePath);

        Assert.Null(managedFingerprint.Mvid);
        Assert.Null(nativeLikeFingerprint.Mvid);
        Assert.False(string.IsNullOrWhiteSpace(MetadataCatalogCache.TryReadMvid(managedAssemblyPath)));
    }

    [Fact]
    public void MetadataCatalogCacheIdentityDoesNotRereadMvidForNormalIdentity()
    {
        using var baselineLayout = TestAssemblyLayout.Create();
        using var replacementLayout = TestAssemblyLayout.Create();
        var cache = CreateCache();
        var baselineAssemblyPath = Directory.EnumerateFiles(baselineLayout.GameAssembliesDir, "*.dll").Single();
        var replacementAssemblyPath = Path.Combine(
            replacementLayout.GameAssembliesDir,
            Path.GetFileName(baselineAssemblyPath));
        File.Copy(typeof(ToolCommandDispatcher).Assembly.Location, replacementAssemblyPath, overwrite: true);
        File.SetLastWriteTimeUtc(replacementAssemblyPath, File.GetLastWriteTimeUtc(baselineAssemblyPath));

        var baselineIdentity = cache.BuildIdentity(
            baselineLayout.GameAssembliesDir,
            modsDir: null,
            includeMods: false,
            MetadataCatalogLoadMode.DeclarationsOnly);
        var replacementIdentity = cache.BuildIdentity(
            replacementLayout.GameAssembliesDir,
            modsDir: null,
            includeMods: false,
            MetadataCatalogLoadMode.DeclarationsOnly);

        Assert.Null(baselineIdentity.Assemblies.Single().Mvid);
        Assert.Null(replacementIdentity.Assemblies.Single().Mvid);
        Assert.NotEqual(
            MetadataCatalogCache.TryReadMvid(baselineAssemblyPath),
            MetadataCatalogCache.TryReadMvid(replacementAssemblyPath));
    }

    [Fact]
    public void LocateReturnsExplicitEmptyResultWhenNoMatchExists()
    {
        using var layout = TestAssemblyLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            ["locate", "symbol", "MissingSymbol", "--assemblies-dir", layout.GameAssembliesDir, "--json"]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;

        Assert.Equal("no-match", root.GetProperty("status").GetString());
        Assert.Equal(0, root.GetProperty("matchCount").GetInt32());
        Assert.Empty(root.GetProperty("matches").EnumerateArray());
    }

    [Fact]
    public void LocateMethodReturnsSignatureMetadata()
    {
        using var layout = TestAssemblyLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            ["locate", "method", "DescribeCard", "--assemblies-dir", layout.GameAssembliesDir, "--json"]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var match = document.RootElement.GetProperty("matches").EnumerateArray().Single();

        Assert.Equal("method", match.GetProperty("kind").GetString());
        Assert.Contains("DescribeCard", match.GetProperty("signature").GetString());
    }

    [Fact]
    public void LocateSymbolCanReturnStructuredSymbolMatches()
    {
        using var layout = TestAssemblyLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            ["locate", "symbol", "CombatScreen", "--assemblies-dir", layout.GameAssembliesDir, "--json"]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Contains(
            document.RootElement.GetProperty("matches").EnumerateArray(),
            match =>
                match.GetProperty("kind").GetString() == "type"
                && match.GetProperty("fullName").GetString()!.Contains("CombatScreenPresenter"));
    }

    [Fact]
    public void DescribeTypeReturnsStructuredMembers()
    {
        using var layout = TestAssemblyLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            [
                "describe",
                "type",
                "Spirectl.TestSymbols.Gameplay.DeckController",
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--json",
            ]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;

        Assert.Equal("describe", root.GetProperty("command").GetString());
        Assert.Equal("type", root.GetProperty("subject").GetString());
        Assert.Equal("Spirectl.TestSymbols.Gameplay", root.GetProperty("namespace").GetString());
        Assert.Equal("class", root.GetProperty("typeKind").GetString());
        Assert.Equal(
            "type:Spirectl.DotnetTools.TestSymbols:Spirectl.TestSymbols.Gameplay.BaseController",
            root.GetProperty("baseTypeId").GetString());
        Assert.Contains(
            "type:Spirectl.DotnetTools.TestSymbols:Spirectl.TestSymbols.Gameplay.IInspectable",
            root.GetProperty("interfaceIds").EnumerateArray().Select(value => value.GetString()));
        Assert.DoesNotContain(
            root.GetProperty("fields").EnumerateArray(),
            field => field.GetProperty("name").GetString()!.Contains("BackingField", StringComparison.Ordinal));
        Assert.Contains(
            root.GetProperty("methods").EnumerateArray(),
            method =>
                method.GetProperty("name").GetString() == "DrawCard"
                && method.GetProperty("id").GetString()!.StartsWith("method:", StringComparison.Ordinal));
        Assert.Contains(
            root.GetProperty("properties").EnumerateArray(),
            property => property.GetProperty("name").GetString() == "DrawCount");
        Assert.Contains(
            root.GetProperty("events").EnumerateArray(),
            eventInfo => eventInfo.GetProperty("name").GetString() == "DeckChanged");
        Assert.Contains(
            root.GetProperty("nestedTypes").EnumerateArray(),
            nestedType => nestedType.GetProperty("name").GetString() == "DeckSnapshot");
        Assert.Equal(1, root.GetProperty("memberCounts").GetProperty("events").GetInt32());
        Assert.True(root.GetProperty("memberCounts").GetProperty("methods").GetInt32() >= 1);
        Assert.True(root.GetProperty("memberCounts").GetProperty("nestedTypes").GetInt32() >= 1);
    }

    [Fact]
    public void DescribeMethodReturnsStructuredParameters()
    {
        using var layout = TestAssemblyLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            [
                "describe",
                "method",
                "Spirectl.TestSymbols.Gameplay.DeckController::DescribeCard(System.String,System.Int32)",
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--json",
            ]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;

        Assert.Equal("method", root.GetProperty("subject").GetString());
        Assert.Equal("System.String DescribeCard(System.String, System.Int32)", root.GetProperty("signature").GetString());
        Assert.Equal(
            "type:Spirectl.DotnetTools.TestSymbols:Spirectl.TestSymbols.Gameplay.DeckController",
            root.GetProperty("declaringTypeId").GetString());
        Assert.Equal("public", root.GetProperty("visibility").GetString());
        Assert.Equal("System.String", root.GetProperty("returnType").GetString());
        Assert.Equal(2, root.GetProperty("parameters").GetArrayLength());
        Assert.Equal("System.String", root.GetProperty("parameters")[0].GetProperty("type").GetString());
        Assert.True(root.GetProperty("isStatic").GetBoolean());
        Assert.False(root.GetProperty("isVirtual").GetBoolean());
        Assert.False(root.GetProperty("isAbstract").GetBoolean());
    }

    [Fact]
    public void DescribeMethodRejectsAmbiguousQueries()
    {
        using var layout = TestAssemblyLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            ["describe", "method", "DrawCard", "--assemblies-dir", layout.GameAssembliesDir, "--json"]);

        Assert.Equal(3, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var error = document.RootElement.GetProperty("error");

        Assert.Equal("ambiguous_query", error.GetProperty("code").GetString());
        Assert.Contains("DrawCard", error.GetProperty("message").GetString());
    }

    [Fact]
    public void DescribeTargetAwareTypeStableIdSelectsNamedAssembly()
    {
        using var layout = TestAssemblyLayout.Create();
        var selected = DescribeTargetResolver.ResolveSelectedAssemblies(
            "type",
            "type:Spirectl.DotnetTools.TestSymbols:Spirectl.TestSymbols.Gameplay.DeckController",
            layout.GameAssembliesDir,
            null,
            includeDependencies: false);

        var assembly = Assert.Single(selected!);
        Assert.Equal("game", assembly.Source);
        Assert.Equal("Spirectl.DotnetTools.TestSymbols.dll", Path.GetFileName(assembly.Path));
    }

    [Fact]
    public void DescribeTargetAwareMethodStableIdSelectsNamedAssembly()
    {
        using var layout = TestAssemblyLayout.Create();
        var selected = DescribeTargetResolver.ResolveSelectedAssemblies(
            "method",
            "method:Spirectl.DotnetTools.TestSymbols:Spirectl.TestSymbols.Gameplay.DeckController::DescribeCard(System.String,System.Int32)",
            layout.GameAssembliesDir,
            null,
            includeDependencies: false);

        var assembly = Assert.Single(selected!);
        Assert.Equal("game", assembly.Source);
        Assert.Equal("Spirectl.DotnetTools.TestSymbols.dll", Path.GetFileName(assembly.Path));
    }

    [Fact]
    public void DescribeTargetAwareUniqueShortMethodSelectsCandidateAssembly()
    {
        using var layout = TestAssemblyLayout.Create();
        var selected = DescribeTargetResolver.ResolveSelectedAssemblies(
            "method",
            "DescribeCard",
            layout.GameAssembliesDir,
            null,
            includeDependencies: false);

        var assembly = Assert.Single(selected!);
        Assert.Equal("game", assembly.Source);
        Assert.Equal("Spirectl.DotnetTools.TestSymbols.dll", Path.GetFileName(assembly.Path));
    }

    [Fact]
    public void DescribeTargetAwareAmbiguousShortMethodStillFailsWithAmbiguousQuery()
    {
        using var layout = TestAssemblyLayout.Create(includeModCopy: true);
        var result = ToolCommandDispatcher.Dispatch(
            [
                "describe",
                "method",
                "DescribeCard",
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--mods-dir",
                layout.ModsDir!,
                "--include-mods",
                "--json",
            ]);

        Assert.Equal(3, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var error = document.RootElement.GetProperty("error");

        Assert.Equal("ambiguous_query", error.GetProperty("code").GetString());
        Assert.Contains("DescribeCard", error.GetProperty("message").GetString());
    }

    [Fact]
    public void DescribeTargetAwareMissingShortMethodStillFailsWithNotFound()
    {
        using var layout = TestAssemblyLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            ["describe", "method", "MissingMethodName", "--assemblies-dir", layout.GameAssembliesDir, "--json"]);

        Assert.Equal(3, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var error = document.RootElement.GetProperty("error");

        Assert.Equal("not_found", error.GetProperty("code").GetString());
        Assert.Contains("MissingMethodName", error.GetProperty("message").GetString());
    }

    [Fact]
    public void HooksReturnsRankedHookMatchesWithScriptHints()
    {
        using var layout = TestAssemblyLayout.Create(includeResourceFixtures: true);
        var result = ToolCommandDispatcher.Dispatch(
            [
                "hooks",
                "OnDeckChanged",
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--resources-dir",
                layout.ResourcesDir!,
                "--json",
            ]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;

        Assert.Equal("hooks", root.GetProperty("command").GetString());
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("matchCount").GetInt32() >= 3);
        Assert.True(root.GetProperty("totalCount").GetInt32() >= 3);
        Assert.True(root.GetProperty("returnedCount").GetInt32() >= 3);
        Assert.Equal(100, root.GetProperty("limit").GetInt32());
        Assert.Equal(0, root.GetProperty("offset").GetInt32());
        Assert.True(root.GetProperty("facets").GetProperty("hookForms").GetArrayLength() >= 1);

        var scriptMatch = root.GetProperty("matches")
            .EnumerateArray()
            .First(match => match.GetProperty("fullName").GetString()!.Contains("CombatScreenController::OnDeckChanged"));
        Assert.Equal("res://scripts/CombatScreen.cs", scriptMatch.GetProperty("scriptPath").GetString());
        Assert.Contains(
            "res://ui/CombatScreen.tscn",
            scriptMatch.GetProperty("scenePaths").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains(
            scriptMatch.GetProperty("hookForms").EnumerateArray().Select(value => value.GetString()),
            value => value == "managed-prefix");
        Assert.Contains(
            scriptMatch.GetProperty("suggestedNextCommands").EnumerateArray().Select(value => value.GetString()),
            value => value!.Contains("code hook-info", StringComparison.Ordinal));
        Assert.True(scriptMatch.GetProperty("referenceCount").GetInt32() >= 0);
        Assert.Equal("public", scriptMatch.GetProperty("visibility").GetString());
    }

    [Fact]
    public void HooksSupportsCatalogPagingAndFilters()
    {
        using var layout = TestAssemblyLayout.Create(includeResourceFixtures: true);
        var result = ToolCommandDispatcher.Dispatch(
            [
                "hooks",
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--resources-dir",
                layout.ResourcesDir!,
                "--limit",
                "2",
                "--offset",
                "1",
                "--source",
                "game",
                "--form",
                "managed-prefix",
                "--has-script",
                "--sort",
                "name",
                "--json",
            ]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;

        Assert.Equal("hooks", root.GetProperty("command").GetString());
        Assert.Equal("", root.GetProperty("query").GetString());
        Assert.Equal(2, root.GetProperty("limit").GetInt32());
        Assert.Equal(1, root.GetProperty("offset").GetInt32());
        Assert.True(root.GetProperty("returnedCount").GetInt32() <= 2);
        Assert.All(
            root.GetProperty("matches").EnumerateArray(),
            match =>
            {
                Assert.Equal("game", match.GetProperty("source").GetString());
                Assert.False(string.IsNullOrWhiteSpace(match.GetProperty("scriptPath").GetString()));
                Assert.Contains(
                    match.GetProperty("hookForms").EnumerateArray().Select(value => value.GetString()),
                    value => value == "managed-prefix");
            });
    }

    [Fact]
    public void HookInfoReturnsExactOverrideAndInterfaceProjection()
    {
        using var layout = TestAssemblyLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            [
                "hook-info",
                "Spirectl.TestSymbols.Gameplay.CombatDeckHook::OnCombatOpened(Spirectl.TestSymbols.Gameplay.DeckController)",
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--json",
            ]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;

        Assert.Equal("hook-info", root.GetProperty("command").GetString());
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal("method", root.GetProperty("kind").GetString());
        Assert.True(root.GetProperty("isOverride").GetBoolean());
        Assert.Equal("public", root.GetProperty("visibility").GetString());
        Assert.Contains(
            "method:Spirectl.DotnetTools.TestSymbols:Spirectl.TestSymbols.Gameplay.CombatHookBase::OnCombatOpened",
            root.GetProperty("baseMethodId").GetString());
        Assert.Contains(
            root.GetProperty("implementedInterfaceMethodIds").EnumerateArray().Select(value => value.GetString()),
            value => value == "method:Spirectl.DotnetTools.TestSymbols:Spirectl.TestSymbols.Gameplay.ICombatHook::OnCombatOpened(Spirectl.TestSymbols.Gameplay.DeckController)");
        Assert.Equal(
            "Spirectl.TestSymbols.Gameplay.DeckController",
            root.GetProperty("parameters")[0].GetProperty("type").GetString());
        Assert.Contains(
            root.GetProperty("hookForms").EnumerateArray().Select(value => value.GetString()),
            value => value == "managed-override");
    }

    [Fact]
    public void HookInfoRejectsAmbiguousQueries()
    {
        using var layout = TestAssemblyLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            ["hook-info", "OnCombatOpened", "--assemblies-dir", layout.GameAssembliesDir, "--json"]);

        Assert.Equal(3, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var error = document.RootElement.GetProperty("error");

        Assert.Equal("ambiguous_query", error.GetProperty("code").GetString());
        Assert.Contains("OnCombatOpened", error.GetProperty("message").GetString());
    }

    [Fact]
    public void DecompileTypeReturnsCSharpLikeText()
    {
        using var layout = TestAssemblyLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            [
                "decompile",
                "type",
                "Spirectl.TestSymbols.Gameplay.ModdingNavigator",
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--json",
            ]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;

        Assert.Equal("decompile", root.GetProperty("command").GetString());
        Assert.Equal("metadata", root.GetProperty("backend").GetString());
        Assert.Equal("csharp", root.GetProperty("language").GetString());
        Assert.Contains("class ModdingNavigator", root.GetProperty("text").GetString());
        Assert.Contains("// Methods", root.GetProperty("text").GetString());
        Assert.Contains(
            root.GetProperty("memberSummaries").EnumerateArray(),
            summary =>
                summary.GetProperty("signature").GetString() == "System.String Run(Spirectl.TestSymbols.Gameplay.DeckController, Spirectl.TestSymbols.Gameplay.HandController)"
                && summary.GetProperty("calls").EnumerateArray().Any(call => call.GetProperty("displayName").GetString() == "DescribeCard"));
    }

    [Fact]
    public void DecompileTypeFullReturnsIlSpyTextAndBackend()
    {
        using var layout = TestAssemblyLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            [
                "decompile",
                "type",
                "Spirectl.TestSymbols.Gameplay.ModdingNavigator",
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--full",
                "--json",
            ]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;

        Assert.Equal("ilspy", root.GetProperty("backend").GetString());
        Assert.Equal("csharp", root.GetProperty("language").GetString());
        Assert.Contains("public sealed class ModdingNavigator", root.GetProperty("text").GetString());
        Assert.Contains("deck.DrawCard();", root.GetProperty("text").GetString());
        Assert.Contains(
            root.GetProperty("memberSummaries").EnumerateArray(),
            summary => summary.GetProperty("name").GetString() == "Run");
    }

    [Fact]
    public void DecompileMethodFullReturnsIlSpyTextAndBackend()
    {
        using var layout = TestAssemblyLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            [
                "decompile",
                "method",
                "Spirectl.TestSymbols.Gameplay.ModdingNavigator::Run(Spirectl.TestSymbols.Gameplay.DeckController,Spirectl.TestSymbols.Gameplay.HandController)",
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--full",
                "--json",
            ]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;

        Assert.Equal("ilspy", root.GetProperty("backend").GetString());
        Assert.Equal("method", root.GetProperty("subject").GetString());
        Assert.Contains(
            "public string Run(DeckController deck, HandController hand)",
            root.GetProperty("text").GetString());
        Assert.Contains(
            "return DeckController.DescribeCard(deckSnapshot.Label, deck.DrawCount);",
            root.GetProperty("text").GetString());
    }

    [Fact]
    public void DecompileExportWritesAssemblyOrientedCorpus()
    {
        using var layout = TestAssemblyLayout.Create();
        var outputDir = Path.Combine(
            Path.GetDirectoryName(layout.GameAssembliesDir)!,
            "exported-decompile");

        var result = ToolCommandDispatcher.Dispatch(
            [
                "decompile-export",
                outputDir,
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--json",
            ]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;

        Assert.Equal("decompile-export", root.GetProperty("command").GetString());
        Assert.Equal(outputDir, root.GetProperty("outputDir").GetString());
        Assert.True(root.GetProperty("assemblyCount").GetInt32() >= 1);
        Assert.True(root.GetProperty("typeCount").GetInt32() >= 1);
        Assert.Contains(
            Directory.EnumerateFiles(outputDir, "*.cs", SearchOption.AllDirectories),
            path => Path.GetFileName(path).Contains("DeckController", StringComparison.Ordinal));
    }

    [Fact]
    public void RefsTypeReturnsStructuredSemanticReferences()
    {
        using var layout = TestAssemblyLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            [
                "refs",
                "type",
                "Spirectl.TestSymbols.Gameplay.DeckController",
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--json",
            ]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;

        Assert.Equal("refs", root.GetProperty("command").GetString());
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("referenceCount").GetInt32() >= 1);
        Assert.Contains(
            root.GetProperty("references").EnumerateArray(),
            reference =>
                reference.GetProperty("containerFullName").GetString() == "Spirectl.TestSymbols.Gameplay.ModdingNavigator::Run"
                && reference.GetProperty("targetFullName").GetString() == "Spirectl.TestSymbols.Gameplay.DeckController"
                && reference.GetProperty("referenceKind").GetString() == "type-use"
                && reference.GetProperty("precision").GetString() == "semantic");
    }

    [Fact]
    public void RefsMethodReturnsStructuredCallReferences()
    {
        using var layout = TestAssemblyLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            [
                "refs",
                "method",
                "Spirectl.TestSymbols.Gameplay.DeckController::DescribeCard(System.String,System.Int32)",
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--json",
            ]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;

        Assert.Contains(
            root.GetProperty("references").EnumerateArray(),
            reference =>
                reference.GetProperty("containerFullName").GetString() == "Spirectl.TestSymbols.Gameplay.ModdingNavigator::Run"
                && reference.GetProperty("referenceKind").GetString() == "method-call"
                && reference.GetProperty("via").GetString() == "call");
    }

    [Fact]
    public void RefsSymbolUsesNamePrecisionAndReturnsExplicitEmptyResults()
    {
        using var layout = TestAssemblyLayout.Create();
        var success = ToolCommandDispatcher.Dispatch(
            [
                "refs",
                "symbol",
                "DeckSnapshot",
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--json",
            ]);

        Assert.Equal(0, success.ExitCode);
        using (var successDocument = JsonDocument.Parse(success.Output))
        {
            var root = successDocument.RootElement;
            Assert.Contains(
                root.GetProperty("references").EnumerateArray(),
                reference =>
                    reference.GetProperty("precision").GetString() == "name"
                    && reference.GetProperty("matchedText").GetString()!.Contains("DeckSnapshot", StringComparison.Ordinal));
        }

        var empty = ToolCommandDispatcher.Dispatch(
            [
                "refs",
                "symbol",
                "MissingSymbol",
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--json",
            ]);

        Assert.Equal(0, empty.ExitCode);
        using var emptyDocument = JsonDocument.Parse(empty.Output);
        Assert.Equal("no-match", emptyDocument.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, emptyDocument.RootElement.GetProperty("referenceCount").GetInt32());
    }

    [Fact]
    public void RefsMethodRejectsAmbiguousQueries()
    {
        using var layout = TestAssemblyLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            ["refs", "method", "DrawCard", "--assemblies-dir", layout.GameAssembliesDir, "--json"]);

        Assert.Equal(3, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal("ambiguous_query", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void DerivedTypeReturnsBaseClassAndInterfaceRelationships()
    {
        using var layout = TestAssemblyLayout.Create();
        var baseResult = ToolCommandDispatcher.Dispatch(
            [
                "derived",
                "type",
                "Spirectl.TestSymbols.Gameplay.BaseController",
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--json",
            ]);

        Assert.Equal(0, baseResult.ExitCode);
        using (var document = JsonDocument.Parse(baseResult.Output))
        {
            var derivedTypes = document.RootElement.GetProperty("derivedTypes").EnumerateArray().ToArray();
            Assert.Contains(
                derivedTypes,
                derivedType =>
                    derivedType.GetProperty("fullName").GetString() == "Spirectl.TestSymbols.Gameplay.DeckController"
                    && derivedType.GetProperty("relationKind").GetString() == "extends");
            Assert.Contains(
                derivedTypes,
                derivedType =>
                    derivedType.GetProperty("fullName").GetString() == "Spirectl.TestSymbols.Gameplay.HandController"
                    && derivedType.GetProperty("relationKind").GetString() == "extends");
        }

        var interfaceResult = ToolCommandDispatcher.Dispatch(
            [
                "derived",
                "type",
                "Spirectl.TestSymbols.Gameplay.ICombatHook",
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--json",
            ]);

        Assert.Equal(0, interfaceResult.ExitCode);
        using var interfaceDocument = JsonDocument.Parse(interfaceResult.Output);
        var interfaceDerivedTypes = interfaceDocument.RootElement.GetProperty("derivedTypes").EnumerateArray().ToArray();
        Assert.Contains(
            interfaceDerivedTypes,
            derivedType =>
                derivedType.GetProperty("fullName").GetString() == "Spirectl.TestSymbols.Gameplay.IDeckHook"
                && derivedType.GetProperty("relationKind").GetString() == "interface-inherits");
        Assert.Contains(
            interfaceDerivedTypes,
            derivedType =>
                derivedType.GetProperty("fullName").GetString() == "Spirectl.TestSymbols.Gameplay.HandController"
                && derivedType.GetProperty("relationKind").GetString() == "implements");
    }

    [Fact]
    public void DerivedTypeReturnsNoMatchForLeafTypes()
    {
        using var layout = TestAssemblyLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            [
                "derived",
                "type",
                "Spirectl.TestSymbols.Gameplay.DeckController.DeckSnapshot",
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--json",
            ]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal("no-match", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("derivedCount").GetInt32());
    }

    [Fact]
    public void DecompileMethodRejectsMissingQueries()
    {
        using var layout = TestAssemblyLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            [
                "decompile",
                "method",
                "Spirectl.TestSymbols.Gameplay.DeckController::MissingMethod()",
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--json",
            ]);

        Assert.Equal(3, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var error = document.RootElement.GetProperty("error");

        Assert.Equal("not_found", error.GetProperty("code").GetString());
    }

    [Fact]
    public void LocateCanIncludeModAssembliesAndLabelTheirSource()
    {
        using var layout = TestAssemblyLayout.Create(includeModCopy: true);
        var result = ToolCommandDispatcher.Dispatch(
            [
                "locate",
                "type",
                "DeckController",
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--mods-dir",
                layout.ModsDir!,
                "--include-mods",
                "--json",
            ]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var sources = document.RootElement
            .GetProperty("matches")
            .EnumerateArray()
            .Select(match => match.GetProperty("source").GetString())
            .ToArray();

        Assert.Contains("game", sources);
        Assert.Contains("mod", sources);
    }

    [Fact]
    public void SceneSearchReturnsSceneNodeAndResourceMatches()
    {
        using var layout = TestAssemblyLayout.Create(includeResourceFixtures: true);
        var result = ToolCommandDispatcher.Dispatch(
            [
                "scene-search",
                "HandPanel",
                "--resources-dir",
                layout.ResourcesDir!,
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--json",
            ]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;

        Assert.Equal("scene-search", root.GetProperty("command").GetString());
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Contains(
            root.GetProperty("matches").EnumerateArray(),
            match =>
                match.GetProperty("kind").GetString() == "scene"
                && match.GetProperty("scenePath").GetString() == "res://ui/shared/HandPanel.tscn");
        Assert.Contains(
            root.GetProperty("matches").EnumerateArray(),
            match =>
                match.GetProperty("kind").GetString() == "node"
                && match.GetProperty("nodePath").GetString() == "/HandPanel"
                && match.TryGetProperty("attachedScriptType", out var attachedScriptType)
                && attachedScriptType.GetString() == "Spirectl.TestSymbols.Scenes.HandPanelController");
        var resourceResult = ToolCommandDispatcher.Dispatch(
            [
                "scene-search",
                "StatusConfig",
                "--resources-dir",
                layout.ResourcesDir!,
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--json",
            ]);

        Assert.Equal(0, resourceResult.ExitCode);
        using var resourceDocument = JsonDocument.Parse(resourceResult.Output);
        Assert.Contains(
            resourceDocument.RootElement.GetProperty("matches").EnumerateArray(),
            match =>
                match.GetProperty("kind").GetString() == "resource"
                && match.GetProperty("resourcePath").GetString() == "res://resources/StatusConfig.tres"
                && match.GetProperty("attachedScriptType").GetString() == "Spirectl.TestSymbols.Scenes.StatusConfig");

        var binarySceneResult = ToolCommandDispatcher.Dispatch(
            [
                "scene-search",
                "BinaryOnly",
                "--resources-dir",
                layout.ResourcesDir!,
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--json",
            ]);

        Assert.Equal(0, binarySceneResult.ExitCode);
        using var binarySceneDocument = JsonDocument.Parse(binarySceneResult.Output);
        Assert.Contains(
            binarySceneDocument.RootElement.GetProperty("matches").EnumerateArray(),
            match =>
                match.GetProperty("kind").GetString() == "scene"
                && match.GetProperty("scenePath").GetString() == "res://binary/BinaryOnly.scn"
                && match.GetProperty("storageKind").GetString() == "binary-file");

        var binaryResourceResult = ToolCommandDispatcher.Dispatch(
            [
                "scene-search",
                "BinaryPanelStyle",
                "--resources-dir",
                layout.ResourcesDir!,
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--json",
            ]);

        Assert.Equal(0, binaryResourceResult.ExitCode);
        using var binaryResourceDocument = JsonDocument.Parse(binaryResourceResult.Output);
        Assert.Contains(
            binaryResourceDocument.RootElement.GetProperty("matches").EnumerateArray(),
            match =>
                match.GetProperty("kind").GetString() == "resource"
                && match.GetProperty("resourcePath").GetString() == "res://resources/BinaryPanelStyle.res"
                && match.GetProperty("storageKind").GetString() == "binary-file");

        var packedResult = ToolCommandDispatcher.Dispatch(
            [
                "scene-search",
                "PackedPanel",
                "--resources-dir",
                layout.ResourcesDir!,
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--json",
            ]);

        Assert.Equal(0, packedResult.ExitCode);
        using var packedDocument = JsonDocument.Parse(packedResult.Output);
        Assert.Contains(
            packedDocument.RootElement.GetProperty("matches").EnumerateArray(),
            match =>
                match.GetProperty("kind").GetString() == "scene"
                && match.GetProperty("scenePath").GetString() == "res://packed/PackedPanel.tscn"
                && match.GetProperty("storageKind").GetString() == "packed-entry"
                && match.GetProperty("containerPath").GetString()!.EndsWith("packed-fixtures.pck", StringComparison.Ordinal));
    }

    [Fact]
    public void SceneTreeReturnsPreorderNodesWithScriptEnrichment()
    {
        using var layout = TestAssemblyLayout.Create(includeResourceFixtures: true);
        var result = ToolCommandDispatcher.Dispatch(
            [
                "scene-tree",
                "res://ui/CombatScreen.tscn",
                "--resources-dir",
                layout.ResourcesDir!,
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--json",
            ]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;

        Assert.Equal("scene-tree", root.GetProperty("command").GetString());
        Assert.Equal("res://ui/CombatScreen.tscn", root.GetProperty("scenePath").GetString());
        var nodes = root.GetProperty("nodes").EnumerateArray().ToArray();
        Assert.Equal("/CombatScreen", nodes[0].GetProperty("nodePath").GetString());
        Assert.Equal(0, nodes[0].GetProperty("depth").GetInt32());
        Assert.Equal("/CombatScreen/HandPanel", nodes[1].GetProperty("nodePath").GetString());
        Assert.Equal("res://ui/shared/HandPanel.tscn", nodes[1].GetProperty("instanceScenePath").GetString());
        Assert.Contains(
            nodes,
            node =>
                node.GetProperty("nodePath").GetString() == "/CombatScreen/StatusBadge"
                && node.GetProperty("attachedScriptType").GetString() == "Spirectl.TestSymbols.Scenes.StatusBadge");

        var binaryTreeResult = ToolCommandDispatcher.Dispatch(
            [
                "scene-tree",
                "res://binary/BinaryOnly.scn",
                "--resources-dir",
                layout.ResourcesDir!,
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--json",
            ]);

        Assert.Equal(0, binaryTreeResult.ExitCode);
        using var binaryTreeDocument = JsonDocument.Parse(binaryTreeResult.Output);
        var binaryRoot = binaryTreeDocument.RootElement;
        Assert.Equal("binary-file", binaryRoot.GetProperty("storageKind").GetString());
        Assert.Contains(
            binaryRoot.GetProperty("nodes").EnumerateArray(),
            node =>
                node.GetProperty("nodePath").GetString() == "/HandPanel/ConfirmButton"
                && node.GetProperty("parentNodePath").GetString() == "/HandPanel");
    }

    [Fact]
    public void SceneNodeReturnsResourceAndNodeReferences()
    {
        using var layout = TestAssemblyLayout.Create(includeResourceFixtures: true);
        var result = ToolCommandDispatcher.Dispatch(
            [
                "scene-node",
                "res://ui/shared/HandPanel.tscn",
                "/HandPanel/ConfirmButton",
                "--resources-dir",
                layout.ResourcesDir!,
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--json",
            ]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;

        Assert.Equal("scene-node", root.GetProperty("command").GetString());
        Assert.Equal("/HandPanel/ConfirmButton", root.GetProperty("nodePath").GetString());
        Assert.Contains(
            root.GetProperty("nodeRefs").EnumerateArray(),
            reference =>
                reference.GetProperty("property").GetString() == "focus_neighbor_left"
                && reference.GetProperty("targetNodePath").GetString() == "/HandPanel/EnergyLabel");

        var rootNodeResult = ToolCommandDispatcher.Dispatch(
            [
                "scene-node",
                "res://ui/shared/HandPanel.tscn",
                "/HandPanel",
                "--resources-dir",
                layout.ResourcesDir!,
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--json",
            ]);

        Assert.Equal(0, rootNodeResult.ExitCode);
        using var rootNodeDocument = JsonDocument.Parse(rootNodeResult.Output);
        Assert.Contains(
            rootNodeDocument.RootElement.GetProperty("resourceRefs").EnumerateArray(),
            reference =>
                reference.GetProperty("property").GetString() == "status_config"
                && reference.GetProperty("resourcePath").GetString() == "res://resources/StatusConfig.tres");

        var energyLabelResult = ToolCommandDispatcher.Dispatch(
            [
                "scene-node",
                "res://ui/shared/HandPanel.tscn",
                "/HandPanel/EnergyLabel",
                "--resources-dir",
                layout.ResourcesDir!,
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--json",
            ]);

        Assert.Equal(0, energyLabelResult.ExitCode);
        using var energyLabelDocument = JsonDocument.Parse(energyLabelResult.Output);
        Assert.Equal(
            "\"3\"",
            energyLabelDocument.RootElement
                .GetProperty("renderProperties")
                .GetProperty("text")
                .GetString());

        var binaryRootNodeResult = ToolCommandDispatcher.Dispatch(
            [
                "scene-node",
                "res://binary/BinaryOnly.scn",
                "/HandPanel",
                "--resources-dir",
                layout.ResourcesDir!,
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--json",
            ]);

        Assert.Equal(0, binaryRootNodeResult.ExitCode);
        using var binaryRootNodeDocument = JsonDocument.Parse(binaryRootNodeResult.Output);
        Assert.Equal("binary-file", binaryRootNodeDocument.RootElement.GetProperty("storageKind").GetString());
        Assert.Contains(
            binaryRootNodeDocument.RootElement.GetProperty("resourceRefs").EnumerateArray(),
            reference =>
                reference.GetProperty("property").GetString() == "panel_style"
                && reference.GetProperty("targetKind").GetString() == "subresource"
                && reference.GetProperty("resourceType").GetString() == "StyleBoxFlat");
    }

    [Fact]
    public void SceneNodeRejectsMissingExactNodeQueries()
    {
        using var layout = TestAssemblyLayout.Create(includeResourceFixtures: true);
        var result = ToolCommandDispatcher.Dispatch(
            [
                "scene-node",
                "res://ui/shared/HandPanel.tscn",
                "/HandPanel/MissingButton",
                "--resources-dir",
                layout.ResourcesDir!,
                "--json",
            ]);

        Assert.Equal(3, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal("not_found", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void VerifyReferencesReportsEveryBucketAgainstAReshapedGameBuild()
    {
        using var layout = ReferenceVerificationLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            [
                "verify-references",
                layout.ConsumerPath,
                "--assemblies-dir",
                layout.CandidateAssembliesDir,
                "--control-assemblies-dir",
                layout.ControlAssembliesDir,
                "--json",
            ]);

        Assert.Equal(3, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;

        Assert.Equal("verify-references", root.GetProperty("command").GetString());
        Assert.Equal("broken", root.GetProperty("status").GetString());
        Assert.Equal("clean", root.GetProperty("control").GetProperty("status").GetString());
        Assert.Equal(0, root.GetProperty("control").GetProperty("unresolvedCount").GetInt32());
        Assert.Equal(10, root.GetProperty("breakCount").GetInt32());
        Assert.Equal(5, root.GetProperty("typeReferenceCount").GetInt32());
        Assert.Equal(14, root.GetProperty("memberReferenceCount").GetInt32());
        Assert.Equal(["sts2", "GodotSharp", "0Harmony"], StringRows(root, "gameAssemblies"));
        Assert.Equal([Path.GetFileName(layout.ConsumerPath)], ConsumerAssemblies(root));

        Assert.Equal(
            ["Spirectl.TestGame.Lobby.SeatRecord"],
            TypeRows(root, "missingTypes"));
        Assert.Equal(
            [
                "Spirectl.TestGame.Lobby.SeatRecord::.ctor",
                "Spirectl.TestGame.Lobby.SeatRecord::Id",
                "Spirectl.TestGame.Lobby.SeatRecord::IsReady",
                "Spirectl.TestGame.Lobby.SeatRecord::Render",
            ],
            MemberRows(root, "missingMembersWithMissingOwner"));
        Assert.Equal(
            [
                "Spirectl.TestGame.Lobby.SeatRegistry::Close",
                "Spirectl.TestGame.Lobby.SeatRegistry::IsOpen",
                "Spirectl.TestGame.Lobby.SeatRegistry::get_Label",
            ],
            MemberRows(root, "missingMembers"));
        Assert.Equal(
            [
                "Spirectl.TestGame.Lobby.AnimationTrack::Play",
                "Spirectl.TestGame.Lobby.DamageHooks::ModifyDamage",
            ],
            MemberRows(root, "changedSignatures"));

        var missingType = root.GetProperty("missingTypes").EnumerateArray().Single();
        Assert.Equal("sts2", missingType.GetProperty("assembly").GetString());
        Assert.Equal([Path.GetFileName(layout.ConsumerPath)], StringRows(missingType, "consumers"));

        var missingConstructor = root.GetProperty("missingMembersWithMissingOwner")
            .EnumerateArray()
            .First(entry => entry.GetProperty("member").GetString() == ".ctor");
        Assert.Equal("method", missingConstructor.GetProperty("memberKind").GetString());
        Assert.Equal(2, missingConstructor.GetProperty("parameterCount").GetInt32());

        var missingField = root.GetProperty("missingMembers")
            .EnumerateArray()
            .First(entry => entry.GetProperty("member").GetString() == "IsOpen");
        Assert.Equal("field", missingField.GetProperty("memberKind").GetString());
        Assert.False(missingField.TryGetProperty("parameterCount", out _));
    }

    [Fact]
    public void VerifyReferencesReportsTheShapeOnBothSidesOfAChangedSignature()
    {
        using var layout = ReferenceVerificationLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            [
                "verify-references",
                layout.ConsumerPath,
                "--assemblies-dir",
                layout.CandidateAssembliesDir,
                "--control-assemblies-dir",
                layout.ControlAssembliesDir,
                "--json",
            ]);

        using var document = JsonDocument.Parse(result.Output);
        var changes = document.RootElement.GetProperty("changedSignatures");

        var droppedReturn = changes
            .EnumerateArray()
            .First(entry => entry.GetProperty("member").GetString() == "Play");
        Assert.Equal("TrackHandle Play(String,Boolean)", droppedReturn.GetProperty("controlSignature").GetString());
        Assert.Equal("Void Play(String,Boolean)", droppedReturn.GetProperty("candidateSignature").GetString());

        var addedParameter = changes
            .EnumerateArray()
            .First(entry => entry.GetProperty("member").GetString() == "ModifyDamage");
        Assert.Equal("Int32 ModifyDamage(Int32,Int32) static", addedParameter.GetProperty("controlSignature").GetString());
        Assert.Equal("Int32 ModifyDamage(Int32,Int32,Int32) static", addedParameter.GetProperty("candidateSignature").GetString());
    }

    [Fact]
    public void VerifyReferencesPassesWhenTheCandidateIsTheControlBuild()
    {
        using var layout = ReferenceVerificationLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            [
                "verify-references",
                layout.ConsumerPath,
                "--assemblies-dir",
                layout.ControlAssembliesDir,
                "--control-assemblies-dir",
                layout.ControlAssembliesDir,
                "--json",
            ]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;

        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal(0, root.GetProperty("breakCount").GetInt32());
        Assert.Empty(root.GetProperty("missingTypes").EnumerateArray());
        Assert.Empty(root.GetProperty("missingMembers").EnumerateArray());
        Assert.Empty(root.GetProperty("changedSignatures").EnumerateArray());
    }

    [Fact]
    public void VerifyReferencesRefusesTheRunWhenTheControlBuildIsDirty()
    {
        using var layout = ReferenceVerificationLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            [
                "verify-references",
                layout.ConsumerPath,
                "--assemblies-dir",
                layout.ControlAssembliesDir,
                "--control-assemblies-dir",
                layout.CandidateAssembliesDir,
                "--json",
            ]);

        Assert.Equal(2, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;

        Assert.Equal("control-dirty", root.GetProperty("status").GetString());
        var control = root.GetProperty("control");
        Assert.Equal("dirty", control.GetProperty("status").GetString());
        Assert.Equal(8, control.GetProperty("unresolvedCount").GetInt32());
        Assert.Equal(["Spirectl.TestGame.Lobby.SeatRecord"], TypeRows(control, "missingTypes"));
        Assert.Equal(4, control.GetProperty("missingMembersWithMissingOwner").GetArrayLength());
        Assert.Equal(3, control.GetProperty("missingMembers").GetArrayLength());

        // The candidate buckets are still filled in — and are exactly why the refusal exists. Read
        // on their own they say the good build reshaped two members, which is backwards; the status,
        // the exit code and the note all say the run is void.
        Assert.Equal(2, root.GetProperty("breakCount").GetInt32());
        Assert.Equal(
            [
                "Spirectl.TestGame.Lobby.AnimationTrack::Play",
                "Spirectl.TestGame.Lobby.DamageHooks::ModifyDamage",
            ],
            MemberRows(root, "changedSignatures"));
        Assert.Contains(
            root.GetProperty("notes").EnumerateArray().Select(note => note.GetString()!),
            note => note.Contains("void", StringComparison.Ordinal));
    }

    [Fact]
    public void VerifyReferencesWithoutAControlReportsOnlyUnresolvedBindings()
    {
        using var layout = ReferenceVerificationLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            [
                "verify-references",
                layout.ConsumerPath,
                "--assemblies-dir",
                layout.CandidateAssembliesDir,
                "--json",
            ]);

        Assert.Equal(3, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;

        Assert.Equal("broken", root.GetProperty("status").GetString());
        Assert.Equal(8, root.GetProperty("breakCount").GetInt32());
        Assert.False(root.TryGetProperty("control", out _));
        Assert.False(root.TryGetProperty("controlAssembliesDir", out _));
        Assert.Empty(root.GetProperty("changedSignatures").EnumerateArray());
        Assert.Contains(
            root.GetProperty("notes").EnumerateArray().Select(note => note.GetString()!),
            note => note.Contains("--control-assemblies-dir", StringComparison.Ordinal));
    }

    [Fact]
    public void VerifyReferencesAcceptsSeveralCommaSeparatedConsumers()
    {
        using var layout = ReferenceVerificationLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            [
                "verify-references",
                $"{layout.ConsumerPath},{layout.ConsumerCopyPath}",
                "--assemblies-dir",
                layout.CandidateAssembliesDir,
                "--control-assemblies-dir",
                layout.ControlAssembliesDir,
                "--json",
            ]);

        Assert.Equal(3, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;

        // The same bindings arriving from two consumers stay one row each, with both consumers
        // named, rather than doubling the reported break count.
        Assert.Equal(10, root.GetProperty("breakCount").GetInt32());
        Assert.Equal(
            [Path.GetFileName(layout.ConsumerCopyPath), Path.GetFileName(layout.ConsumerPath)],
            StringRows(root.GetProperty("missingTypes").EnumerateArray().Single(), "consumers"));
    }

    [Fact]
    public void VerifyReferencesRendersHumanOutputWithoutJson()
    {
        using var layout = ReferenceVerificationLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            [
                "verify-references",
                layout.ConsumerPath,
                "--assemblies-dir",
                layout.CandidateAssembliesDir,
                "--control-assemblies-dir",
                layout.ControlAssembliesDir,
            ]);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("status: broken", result.Output);
        Assert.Contains("breakCount: 10", result.Output);
        Assert.Contains("control: clean (0 unresolved)", result.Output);
        Assert.Contains("Spirectl.TestGame.Lobby.SeatRegistry::get_Label (method/0)", result.Output);
        Assert.Contains("candidate: Int32 ModifyDamage(Int32,Int32,Int32) static", result.Output);
    }

    [Fact]
    public void VerifyReferencesRequiresAnAssembliesDir()
    {
        using var layout = ReferenceVerificationLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(["verify-references", layout.ConsumerPath, "--json"]);

        Assert.Equal(2, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal("usage_error", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void VerifyReferencesRejectsAMissingConsumerAssembly()
    {
        using var layout = ReferenceVerificationLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            [
                "verify-references",
                Path.Combine(layout.ControlAssembliesDir, "NotThere.dll"),
                "--assemblies-dir",
                layout.CandidateAssembliesDir,
                "--json",
            ]);

        Assert.Equal(3, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal("not_found", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void ControlAssembliesDirIsRejectedForOtherCommands()
    {
        using var layout = TestAssemblyLayout.Create();
        var result = ToolCommandDispatcher.Dispatch(
            [
                "locate",
                "type",
                "DeckController",
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--control-assemblies-dir",
                layout.GameAssembliesDir,
                "--json",
            ]);

        Assert.Equal(2, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal("usage_error", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static string[] StringRows(JsonElement root, string property)
    {
        return [.. root.GetProperty(property).EnumerateArray().Select(entry => entry.GetString()!)];
    }

    private static string[] ConsumerAssemblies(JsonElement root)
    {
        return
        [
            .. root.GetProperty("consumers")
                .EnumerateArray()
                .Select(entry => entry.GetProperty("assembly").GetString()!),
        ];
    }

    private static string[] TypeRows(JsonElement root, string property)
    {
        return
        [
            .. root.GetProperty(property)
                .EnumerateArray()
                .Select(entry => entry.GetProperty("type").GetString()!),
        ];
    }

    private static string[] MemberRows(JsonElement root, string property)
    {
        return
        [
            .. root.GetProperty(property)
                .EnumerateArray()
                .Select(entry => $"{entry.GetProperty("type").GetString()}::{entry.GetProperty("member").GetString()}"),
        ];
    }

    private static InspectionCommandRequest CreateInspectionRequest(
        string command,
        string? subject,
        string query,
        string assembliesDir)
    {
        return new InspectionCommandRequest(
            command,
            subject,
            query,
            SecondaryQuery: null,
            ContainerPath: null,
            AssembliesDir: assembliesDir,
            ControlAssembliesDir: null,
            ResourcesDir: null,
            ModsDir: null,
            ExcludeDir: null,
            IncludeMods: false,
            IncludeDependencies: false,
            Full: false,
            Limit: 20,
            Offset: 0,
            SourceFilter: null,
            AssemblyFilter: null,
            FormFilters: [],
            HasScript: false,
            Sort: "relevance",
            CacheDir: null,
            CacheMode: MetadataCatalogCacheMode.Auto,
            Json: true);
    }

    private static string CreateTempCacheDir()
    {
        return Path.Combine(Path.GetTempPath(), $"spirectl-cache-{Guid.NewGuid():N}");
    }

    private static ToolCommandResult DispatchLocate(string assembliesDir, string cacheDir, string cacheMode)
    {
        return DispatchInspection("locate", "type", "DeckController", assembliesDir, cacheDir, cacheMode);
    }

    private static ToolCommandResult DispatchInspection(
        string command,
        string? subject,
        string query,
        string assembliesDir,
        string cacheDir,
        string cacheMode)
    {
        return DispatchInspectionWithExtraArgs(command, subject, query, assembliesDir, cacheDir, cacheMode);
    }

    private static ToolCommandResult DispatchInspectionWithExtraArgs(
        string command,
        string? subject,
        string query,
        string assembliesDir,
        string cacheDir,
        string cacheMode,
        params string[] extraArgs)
    {
        var args = new List<string> { command };
        if (subject is not null)
        {
            args.Add(subject);
        }

        args.AddRange(
            [
                query,
                "--assemblies-dir",
                assembliesDir,
                "--cache-dir",
                cacheDir,
                "--cache-mode",
                cacheMode,
                "--json",
            ]);
        args.AddRange(extraArgs);
        return ToolCommandDispatcher.Dispatch([.. args]);
    }

    private static IReadOnlyList<string> EnumerateDeclarationManifestFiles(string cacheDir)
    {
        var entryDir = Path.Combine(cacheDir, "metadata-catalog", "declarations");
        return Directory.Exists(entryDir)
            ? Directory.EnumerateFiles(entryDir, "manifest-*.json").OrderBy(path => path, StringComparer.Ordinal).ToArray()
            : [];
    }

    private static IReadOnlyList<string> EnumerateDeclarationAssemblyEntryFiles(string cacheDir)
    {
        var entryDir = Path.Combine(cacheDir, "metadata-catalog", "declarations", "assemblies");
        return Directory.Exists(entryDir)
            ? Directory.EnumerateFiles(entryDir, "*.json").OrderBy(path => path, StringComparer.Ordinal).ToArray()
            : [];
    }

    private static IReadOnlyList<string> EnumerateReferenceEntryFiles(string cacheDir)
    {
        var entryDir = Path.Combine(cacheDir, "metadata-catalog", "references");
        return Directory.Exists(entryDir)
            ? Directory.EnumerateFiles(entryDir, "*.json").OrderBy(path => path, StringComparer.Ordinal).ToArray()
            : [];
    }

    private static IReadOnlyList<string> EnumerateCacheTemporaryFiles(string cacheDir)
    {
        var entryDir = Path.Combine(cacheDir, "metadata-catalog");
        return Directory.Exists(entryDir)
            ? Directory.EnumerateFiles(entryDir, "*.tmp", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray()
            : [];
    }

    private static string FindDeclarationAssemblyEntryPath(string cacheDir, string assemblyName)
    {
        return EnumerateDeclarationAssemblyEntryFiles(cacheDir).Single(path =>
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return string.Equals(
                document.RootElement.GetProperty("Assembly").GetProperty("AssemblyName").GetString(),
                assemblyName,
                StringComparison.Ordinal);
        });
    }

    private static string ReadManifestAssemblyEntryKeyPath(string cacheDir, string assemblyName)
    {
        var manifestPath = EnumerateDeclarationManifestFiles(cacheDir)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifestAssembly = document.RootElement
            .GetProperty("Assemblies")
            .EnumerateArray()
            .Single(assembly => string.Equals(
                assembly.GetProperty("AssemblyName").GetString(),
                assemblyName,
                StringComparison.Ordinal));
        var entryKey = manifestAssembly.GetProperty("EntryKey").GetString();
        Assert.False(string.IsNullOrWhiteSpace(entryKey));
        return Path.Combine(cacheDir, "metadata-catalog", "declarations", "assemblies", $"{entryKey}.json");
    }

    private static void AssertValidDeclarationManifest(string cacheDir)
    {
        var manifestPath = Assert.Single(EnumerateDeclarationManifestFiles(cacheDir));
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        Assert.True(root.TryGetProperty("Identity", out _));
        Assert.NotEmpty(root.GetProperty("Assemblies").EnumerateArray());
    }

    private static void AssertValidDeclarationAssemblyEntries(string cacheDir)
    {
        Assert.All(EnumerateDeclarationAssemblyEntryFiles(cacheDir), entryPath =>
        {
            using var document = JsonDocument.Parse(File.ReadAllText(entryPath));
            var root = document.RootElement;
            Assert.True(root.TryGetProperty("Assembly", out _));
            Assert.True(root.TryGetProperty("Snapshot", out _));
        });
    }

    private static void AssertJsonEquivalent(string expected, string actual)
    {
        using var expectedDocument = JsonDocument.Parse(expected);
        using var actualDocument = JsonDocument.Parse(actual);
        Assert.Equal(expectedDocument.RootElement.GetRawText(), actualDocument.RootElement.GetRawText());
    }

    private static void AssertNoCacheDiagnostics(string output)
    {
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;

        Assert.False(root.TryGetProperty("cacheDir", out _));
        Assert.False(root.TryGetProperty("cacheMode", out _));
        Assert.False(root.TryGetProperty("cacheStatus", out _));
        Assert.False(root.TryGetProperty("cacheError", out _));
    }

    private static void MutateCacheEntry(string entryPath, string mutation)
    {
        var root = JsonNode.Parse(File.ReadAllText(entryPath))!.AsObject();
        switch (mutation)
        {
            case "schema":
                root["Identity"]!["SchemaVersion"] = MetadataCatalogCache.SchemaVersion + 1;
                break;
            case "helper":
                root["Identity"]!["HelperBuildIdentity"]!["ModuleVersionId"] = Guid.NewGuid().ToString("D");
                break;
            case "snapshot":
                if (root.ContainsKey("Assemblies"))
                {
                    root["Assemblies"] = null;
                }
                else
                {
                    root["Snapshot"] = null;
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown cache entry mutation.");
        }

        File.WriteAllText(entryPath, root.ToJsonString());
    }

    private static void CorruptAssemblyCacheEntry(string cacheDir, string assemblyName)
    {
        var entryPath = Directory
            .EnumerateFiles(Path.Combine(cacheDir, "metadata-catalog", "declarations", "assemblies"), "*.json")
            .Single(path =>
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                return string.Equals(
                    document.RootElement.GetProperty("Assembly").GetProperty("AssemblyName").GetString(),
                    assemblyName,
                    StringComparison.Ordinal);
            });

        File.WriteAllText(entryPath, "{ corrupt assembly entry");
    }

    private static void CopyDependencyAssembly<T>(string destinationDir)
    {
        var dependencyPath = typeof(T).Assembly.Location;
        File.Copy(dependencyPath, Path.Combine(destinationDir, Path.GetFileName(dependencyPath)));
    }

    private static MetadataCatalogCache CreateCache(int schemaVersion = MetadataCatalogCache.SchemaVersion)
    {
        return new MetadataCatalogCache(
            schemaVersion,
            new HelperBuildIdentity("Spirectl.DotnetTools", "test", "00000000-0000-0000-0000-000000000001", null));
    }

    [Fact]
    public void SceneSearchReportsMalformedBinaryCoverageWhenBinaryFilesAreAllThatExist()
    {
        using var layout = TestAssemblyLayout.Create();
        var resourcesRoot = Path.Combine(Path.GetTempPath(), $"spirectl-scene-binary-only-{Guid.NewGuid():N}");
        Directory.CreateDirectory(resourcesRoot);
        File.WriteAllBytes(Path.Combine(resourcesRoot, "CombatScreen.scn"), []);
        File.WriteAllBytes(Path.Combine(resourcesRoot, "StatusConfig.res"), []);

        try
        {
            var result = ToolCommandDispatcher.Dispatch(
                [
                    "scene-search",
                    "CombatScreen",
                    "--resources-dir",
                    resourcesRoot,
                    "--json",
                ]);

            Assert.Equal(0, result.ExitCode);
            using var document = JsonDocument.Parse(result.Output);
            var root = document.RootElement;
            Assert.Equal("no-match", root.GetProperty("status").GetString());
            Assert.Contains(
                root.GetProperty("notes").EnumerateArray().Select(note => note.GetString()),
                note => note!.Contains("malformed or incomplete", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(resourcesRoot, recursive: true);
        }
    }

    [Fact]
    public void SceneSearchPrefersTextOverPackedDuplicatesAndReportsSkippedPackedEntry()
    {
        using var layout = TestAssemblyLayout.Create(includeResourceFixtures: true);
        var packedDir = Path.Combine(layout.ResourcesDir!, "packed");
        Directory.CreateDirectory(packedDir);
        File.WriteAllText(
            Path.Combine(packedDir, "PackedPanel.tscn"),
            """
            [gd_scene format=3]

            [node name="PackedPanel" type="VBoxContainer"]
            """);

        var result = ToolCommandDispatcher.Dispatch(
            [
                "scene-search",
                "PackedPanel",
                "--resources-dir",
                layout.ResourcesDir!,
                "--assemblies-dir",
                layout.GameAssembliesDir,
                "--json",
            ]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;
        Assert.Contains(
            root.GetProperty("matches").EnumerateArray(),
            match =>
                match.GetProperty("kind").GetString() == "scene"
                && match.GetProperty("scenePath").GetString() == "res://packed/PackedPanel.tscn"
                && match.GetProperty("storageKind").GetString() == "text-file");
        Assert.Contains(
            root.GetProperty("notes").EnumerateArray().Select(note => note.GetString()),
            note => note!.Contains("Skipped duplicate scene 'res://packed/PackedPanel.tscn'", StringComparison.Ordinal));
    }

    [Fact]
    public void AssetSearchReturnsPackedRasterMatchesWithOfflineReadSupport()
    {
        using var root = new TempDir();
        WritePackedResource(
            Path.Combine(root.Path, "packed-assets.pck"),
            ("res://packed/packed_icon.png", TinyPngBytes()));

        var result = ToolCommandDispatcher.Dispatch(
            [
                "asset-search",
                "packed_icon",
                "--resources-dir",
                root.Path,
                "--json",
            ]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var rootElement = document.RootElement;

        Assert.Equal("asset-search", rootElement.GetProperty("command").GetString());
        Assert.Equal("ok", rootElement.GetProperty("status").GetString());
        Assert.Contains(
            rootElement.GetProperty("matches").EnumerateArray(),
            match =>
                match.GetProperty("logicalPath").GetString() == "res://packed/packed_icon.png"
                && match.GetProperty("assetKind").GetString() == "image"
                && match.GetProperty("storageKind").GetString() == "packed-entry"
                && match.GetProperty("containerPath").GetString()!.EndsWith("packed-assets.pck", StringComparison.Ordinal)
                && match.GetProperty("readableOffline").GetBoolean());
    }

    [Fact]
    public void AssetSearchReturnsFontMatchesWithOfflineReadSupport()
    {
        using var root = new TempDir();
        Directory.CreateDirectory(Path.Combine(root.Path, "fonts"));
        File.WriteAllBytes(Path.Combine(root.Path, "fonts", "sts2-title.ttf"), [0, 1, 2, 3]);
        WritePackedResource(
            Path.Combine(root.Path, "packed-assets.pck"),
            ("res://packed/fonts/sts2-body.woff2", [4, 5, 6, 7]));

        var result = ToolCommandDispatcher.Dispatch(
            [
                "asset-search",
                "sts2",
                "--resources-dir",
                root.Path,
                "--json",
            ]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var matches = document.RootElement.GetProperty("matches").EnumerateArray().ToArray();

        Assert.Contains(
            matches,
            match =>
                match.GetProperty("logicalPath").GetString() == "res://fonts/sts2-title.ttf"
                && match.GetProperty("assetKind").GetString() == "font"
                && match.GetProperty("storageKind").GetString() == "filesystem-file"
                && match.GetProperty("resourceType").GetString() == "FontFile"
                && match.GetProperty("readableOffline").GetBoolean());
        Assert.Contains(
            matches,
            match =>
                match.GetProperty("logicalPath").GetString() == "res://packed/fonts/sts2-body.woff2"
                && match.GetProperty("assetKind").GetString() == "font"
                && match.GetProperty("storageKind").GetString() == "packed-entry"
                && match.GetProperty("resourceType").GetString() == "FontFile"
                && match.GetProperty("readableOffline").GetBoolean());
    }

    [Fact]
    public void AssetIndexReturnsAllReportableAssetsWithPackedMetadata()
    {
        using var root = new TempDir();
        Directory.CreateDirectory(Path.Combine(root.Path, "ui", "shared"));
        File.WriteAllText(
            Path.Combine(root.Path, "ui", "shared", "panel.tscn"),
            """
            [gd_scene format=3]

            [node name="Panel" type="Panel"]
            """);
        WritePackedResource(
            Path.Combine(root.Path, "packed-assets.pck"),
            ("res://packed/icon.png", TinyPngBytes()));
        File.WriteAllText(Path.Combine(root.Path, "ignored.txt"), "not an asset");

        var result = ToolCommandDispatcher.Dispatch(
            [
                "asset-index",
                "--resources-dir",
                root.Path,
                "--json",
            ]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var rootElement = document.RootElement;
        var matches = rootElement.GetProperty("matches").EnumerateArray().ToArray();

        Assert.Equal("asset-index", rootElement.GetProperty("command").GetString());
        Assert.Equal("ok", rootElement.GetProperty("status").GetString());
        Assert.Equal("", rootElement.GetProperty("query").GetString());
        Assert.Equal(2, rootElement.GetProperty("matchCount").GetInt32());
        Assert.Contains(
            matches,
            match =>
                match.GetProperty("logicalPath").GetString() == "res://ui/shared/panel.tscn"
                && match.GetProperty("assetKind").GetString() == "scene"
                && match.GetProperty("storageKind").GetString() == "filesystem-file");
        Assert.Contains(
            matches,
            match =>
                match.GetProperty("logicalPath").GetString() == "res://packed/icon.png"
                && match.GetProperty("assetKind").GetString() == "image"
                && match.GetProperty("storageKind").GetString() == "packed-entry"
                && match.GetProperty("readableOffline").GetBoolean()
                && match.GetProperty("containerPath").GetString()!.EndsWith("packed-assets.pck", StringComparison.Ordinal));
    }

    [Fact]
    public void AssetSearchReturnsMixedFilesystemAndPackedSceneResourceMatchesWithMetadata()
    {
        using var root = new TempDir();
        Directory.CreateDirectory(Path.Combine(root.Path, "ui", "shared"));
        File.WriteAllText(
            Path.Combine(root.Path, "ui", "shared", "panel.tscn"),
            """
            [gd_scene format=3]

            [node name="Panel" type="Panel"]
            """);
        WritePackedResource(
            Path.Combine(root.Path, "packed-assets.pck"),
            ("res://packed/theme.tres", System.Text.Encoding.UTF8.GetBytes("""
            [gd_resource type="Theme" format=3]

            [resource]
            """)));

        var result = ToolCommandDispatcher.Dispatch(
            [
                "asset-search",
                "p",
                "--resources-dir",
                root.Path,
                "--json",
            ]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var matches = document.RootElement.GetProperty("matches").EnumerateArray().ToArray();

        Assert.Contains(
            matches,
            match =>
                match.GetProperty("logicalPath").GetString() == "res://ui/shared/panel.tscn"
                && match.GetProperty("storageKind").GetString() == "filesystem-file"
                && match.GetProperty("resourceType").GetString() == "PackedScene"
                && match.GetProperty("readableOffline").GetBoolean());
        Assert.Contains(
            matches,
            match =>
                match.GetProperty("logicalPath").GetString() == "res://packed/theme.tres"
                && match.GetProperty("storageKind").GetString() == "packed-entry"
                && match.GetProperty("resourceType").GetString() == "Theme"
                && match.GetProperty("containerPath").GetString()!.EndsWith("packed-assets.pck", StringComparison.Ordinal)
                && match.GetProperty("readableOffline").GetBoolean());
    }

    /// <summary>
    /// A control game build, a reshaped candidate build of the same surface, and a consumer
    /// assembly compiled against the control — the three inputs a reference verification run takes.
    /// </summary>
    /// <remarks>
    /// The reshaped build ships under its own file name so both can live in one output directory,
    /// and is copied into the candidate directory as <c>sts2.dll</c>: resolution is by file name in
    /// a directory, which is exactly how a game update arrives.
    /// </remarks>
    private sealed class ReferenceVerificationLayout : IDisposable
    {
        private const string GameAssemblyFileName = "sts2.dll";
        private const string ReshapedGameAssemblyFileName = "sts2-reshaped.dll";
        private const string ConsumerAssemblyFileName = "Spirectl.DotnetTools.TestGameConsumer.dll";

        private readonly string _root;

        private ReferenceVerificationLayout(
            string root,
            string controlAssembliesDir,
            string candidateAssembliesDir,
            string consumerPath,
            string consumerCopyPath)
        {
            _root = root;
            ControlAssembliesDir = controlAssembliesDir;
            CandidateAssembliesDir = candidateAssembliesDir;
            ConsumerPath = consumerPath;
            ConsumerCopyPath = consumerCopyPath;
        }

        public string ControlAssembliesDir { get; }

        public string CandidateAssembliesDir { get; }

        public string ConsumerPath { get; }

        /// <summary>A second copy of the same consumer, for the multi-consumer attribution case.</summary>
        public string ConsumerCopyPath { get; }

        public static ReferenceVerificationLayout Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"spirectl-verify-references-{Guid.NewGuid():N}");
            var controlAssembliesDir = Path.Combine(root, "control");
            var candidateAssembliesDir = Path.Combine(root, "candidate");
            var consumerDir = Path.Combine(root, "consumer");
            Directory.CreateDirectory(controlAssembliesDir);
            Directory.CreateDirectory(candidateAssembliesDir);
            Directory.CreateDirectory(consumerDir);

            File.Copy(
                Path.Combine(AppContext.BaseDirectory, GameAssemblyFileName),
                Path.Combine(controlAssembliesDir, GameAssemblyFileName));
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, ReshapedGameAssemblyFileName),
                Path.Combine(candidateAssembliesDir, GameAssemblyFileName));

            var consumerPath = Path.Combine(consumerDir, ConsumerAssemblyFileName);
            var consumerCopyPath = Path.Combine(consumerDir, "Spirectl.DotnetTools.TestGameConsumer.Copy.dll");
            File.Copy(Path.Combine(AppContext.BaseDirectory, ConsumerAssemblyFileName), consumerPath);
            File.Copy(consumerPath, consumerCopyPath);

            return new ReferenceVerificationLayout(
                root,
                controlAssembliesDir,
                candidateAssembliesDir,
                consumerPath,
                consumerCopyPath);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class TestAssemblyLayout : IDisposable
    {
        private readonly string _root;

        private TestAssemblyLayout(
            string root,
            string gameAssembliesDir,
            string? modsDir,
            string? resourcesDir)
        {
            _root = root;
            GameAssembliesDir = gameAssembliesDir;
            ModsDir = modsDir;
            ResourcesDir = resourcesDir;
        }

        public string GameAssembliesDir { get; }

        public string? ModsDir { get; }

        public string? ResourcesDir { get; }

        public static TestAssemblyLayout Create(
            bool includeModCopy = false,
            bool includeResourceFixtures = false,
            bool includeExtraGameAssembly = false)
        {
            var root = Path.Combine(Path.GetTempPath(), $"spirectl-dotnet-tools-{Guid.NewGuid():N}");
            var gameAssembliesDir = Path.Combine(root, "game-assemblies");
            Directory.CreateDirectory(gameAssembliesDir);

            var assemblyPath = typeof(DeckController).Assembly.Location;
            var fileName = Path.GetFileName(assemblyPath);
            File.Copy(assemblyPath, Path.Combine(gameAssembliesDir, fileName));

            if (includeExtraGameAssembly)
            {
                var extraAssemblyPath = typeof(ToolCommandDispatcher).Assembly.Location;
                var extraFileName = Path.GetFileName(extraAssemblyPath);
                if (!string.Equals(extraFileName, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(extraAssemblyPath, Path.Combine(gameAssembliesDir, extraFileName));
                }
            }

            string? modsDir = null;
            if (includeModCopy)
            {
                modsDir = Path.Combine(root, "mods", "SampleMod");
                Directory.CreateDirectory(modsDir);
                File.Copy(assemblyPath, Path.Combine(modsDir, fileName));
            }

            string? resourcesDir = null;
            if (includeResourceFixtures)
            {
                resourcesDir = Path.Combine(root, "resources");
                CopyDirectory(FindFixtureRoot(), resourcesDir);
            }

            return new TestAssemblyLayout(root, gameAssembliesDir, modsDir, resourcesDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static string FindFixtureRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                var candidate = Path.Combine(
                    current.FullName,
                    "dotnet-tools",
                    "tests",
                    "Spirectl.DotnetTools.TestSymbols",
                    "Fixtures");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException("Could not locate scene fixture resources.");
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (var file in Directory.EnumerateFiles(source))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            }

            foreach (var directory in Directory.EnumerateDirectories(source))
            {
                CopyDirectory(
                    directory,
                    Path.Combine(destination, Path.GetFileName(directory)));
            }
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"spirectl-dotnet-assets-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private static void WritePackedResource(string packPath, params (string LogicalPath, byte[] Contents)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(packPath)!);

        const uint packHeaderMagic = 0x43504447;
        const uint version = 3;
        const int headerLength = 40;
        ulong dataOffset = headerLength;
        var directoryOffset = dataOffset + (ulong)entries.Sum(entry => entry.Contents.Length);

        using var stream = File.Create(packPath);
        using var writer = new BinaryWriter(stream);
        writer.Write(packHeaderMagic);
        writer.Write(version);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0ul);
        writer.Write(directoryOffset);

        var entryOffsets = new List<ulong>(entries.Length);
        foreach (var (_, contents) in entries)
        {
            entryOffsets.Add(dataOffset);
            writer.Write(contents);
            dataOffset += (ulong)contents.Length;
        }

        writer.Write(entries.Length);
        for (var index = 0; index < entries.Length; index += 1)
        {
            var pathBytes = System.Text.Encoding.UTF8.GetBytes(entries[index].LogicalPath + "\0");
            writer.Write((uint)pathBytes.Length);
            writer.Write(pathBytes);
            writer.Write(entryOffsets[index]);
            writer.Write((ulong)entries[index].Contents.Length);
            writer.Write(new byte[16]);
            writer.Write(0u);
        }
    }

    private static byte[] TinyPngBytes()
    {
        return
        [
            0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
            0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x06, 0x00, 0x00, 0x00, 0x1f, 0x15, 0xc4,
            0x89, 0x00, 0x00, 0x00, 0x0d, 0x49, 0x44, 0x41,
            0x54, 0x78, 0x9c, 0x63, 0x60, 0x60, 0x60, 0xf8,
            0x0f, 0x00, 0x01, 0x04, 0x01, 0x00, 0x70, 0x20,
            0x65, 0x0b, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45,
            0x4e, 0x44, 0xae, 0x42, 0x60, 0x82,
        ];
    }
}
