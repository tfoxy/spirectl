using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spirectl.Sts2.Embedding;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

/// <summary>
/// Guards the compile profiles of Spirectl.Sts2 (the <c>Sts2Profile</c> property): Full is what every
/// consumer gets unless it asks otherwise, and the source lists that make up a profile stay consistent.
/// Evaluation only (<c>-getItem:Compile</c>), so it needs no game assemblies and no build.
/// </summary>
public sealed class Sts2CompileProfileTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullIsTheDefaultProfile(bool enableLiveHost)
    {
        var implicitDefault = Evaluate(enableLiveHost);
        var explicitFull = Evaluate(enableLiveHost, profile: "Full");

        Assert.Equal(explicitFull, implicitDefault);
    }

    [Fact]
    public void TheSharedAssemblyTheTestsLinkAgainstIsTheFullProfile()
    {
        var profile = typeof(ISpirectlRuntime).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "SpirectlSts2Profile")?.Value;

        Assert.Equal("Full", profile);
    }

    [Theory]
    [InlineData(false, "Full")]
    [InlineData(true, "Full")]
    [InlineData(true, "Embedded")]
    public void NoSourceIsListedTwice(bool enableLiveHost, string profile)
    {
        var items = Evaluate(enableLiveHost, profile);

        var duplicates = items
            .GroupBy(path => path, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void EveryLiveSourceOnDiskIsCompiledByTheFullLiveProfile()
    {
        var root = FindRepositoryRoot();
        var projectDirectory = Path.Combine(root, "bridge-mod/src/Spirectl.Sts2");
        var compiled = Evaluate(enableLiveHost: true, profile: "Full").ToHashSet(StringComparer.Ordinal);

        // The live sources are listed by name, so a new file that nobody added to the project would silently
        // never compile. This is the check the old glob used to give for free.
        var missing = Directory
            .EnumerateFiles(Path.Combine(projectDirectory, "Live"), "*.cs", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Where(path => !compiled.Contains(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(projectDirectory, path))
            .ToArray();

        Assert.Empty(missing);
    }

    /// <summary>
    /// What the embedded profile does not compile, relative to the project directory. One entry per file, in
    /// the project file's own words: see the "Embedded profile" item group in Spirectl.Sts2.csproj.
    /// </summary>
    private static readonly string[] EmbeddedExcluded =
    [
        // Stand-ins (the embedded composition and refusal helper replace these).
        "Live/Sts2RuntimeFactory.cs",
        "Live/Sts2ReusableLiveComposition.cs",
        "GameApi/Sts2GameApiProbe.GameVersion.cs",
        "Live/Sts2ActionHandler.Dispatch.cs",
        // The legacy state-extractor lane.
        "Core/State/ObservedGameStateExtractor.cs",
        "Core/State/RuntimeStateMapper.cs",
        "Core/State/ScaffoldRuntimeObservationProvider.cs",
        "Core/State/IRuntimeObservationProvider.cs",
        "Core/State/BridgeRuntimeObservation.cs",
        "Live/Sts2RuntimeObservationProvider.cs",
        "Live/Sts2LobbyPresentationGeometryResolver.cs",
        "Live/Sts2PresentationStateResolver.cs",
        "Live/Sts2CardOverlayInspector.cs",
        "Common/Sts2UnsupportedScreenNotice.cs",
        "Core/Models/RandomCharacterFacts.cs",
        // Reference-data implementation (the DTOs and the port stay).
        "Live/Sts2ReferenceDataProvider.cs",
        // Synthetic host-local seats.
        "Live/Sts2HostLocalSeatSyncWatcher.cs",
        "Live/Sts2HostLocalSeatTurnWatcher.cs",
        // The VFX-spawn hook.
        "Live/Sts2VfxSpawnEventHooks.cs",
        // Bridge- and test-only callers.
        "Core/State/StateResourceReferenceCollector.cs",
        "Live/EncounterVisuals/Sts2EncounterVisualEventStore.cs",
        "Live/EncounterVisuals/Sts2KaiserCrabVisualHooks.cs",
        // Action bodies no embedded dispatch arm reaches: six whole partials, and the moved halves of five.
        "Live/Sts2ActionHandler.CardPile.cs",
        "Live/Sts2ActionHandler.Combat.cs",
        "Live/Sts2ActionHandler.DeckView.cs",
        "Live/Sts2ActionHandler.HandSelection.cs",
        "Live/Sts2ActionHandler.InspectRelic.cs",
        "Live/Sts2ActionHandler.TopBar.cs",
        "Live/Sts2ActionHandler.ContextOwnership.Full.cs",
        "Live/Sts2ActionHandler.FailuresInputTypes.Full.cs",
        "Live/Sts2ActionHandler.RewardCommit.Full.cs",
        "Live/Sts2ActionHandler.ScreenIntents.Full.cs",
        "Live/Sts2ActionHandler.ShopMapLobby.Full.cs",
        // Left with no caller once those bodies are gone.
        "Live/Sts2MainMenuStartRunHooks.cs",
        "Live/Sts2CrystalSphereScreenInspector.cs",
        "Common/Sts2LobbyCharacterButtonInvoker.cs",
        "Common/Sts2PartialChoiceNotice.cs",
        "Core/Map/Sts2MapDrawingTransform.cs",
    ];

    /// <summary>The semantic action kinds the embedded dispatcher routes: what an embedder in this repo family sends.</summary>
    private static readonly string[] EmbeddedActionKinds =
    [
        "ClaimReward",
        "ControllerInput",
        "DisconnectClient",
        "HoverElement",
        "KeyInput",
        "MouseClick",
        "SelectMapNode",
        "SetClientName",
        "SetScrollOffset",
    ];

    /// <summary>Hook and watcher installs the embedded composition deliberately does not perform.</summary>
    private static readonly string[] EmbeddedDroppedInstalls =
    [
        "Sts2HostLocalSeatSyncWatcher",
        "Sts2HostLocalSeatTurnWatcher",
        "Sts2VfxSpawnEventHooks",
    ];

    [Fact]
    public void EmbeddedProfileLeavesOutExactlyTheDeclaredSet()
    {
        var projectDirectory = Path.Combine(FindRepositoryRoot(), "bridge-mod/src/Spirectl.Sts2");
        string Relative(string path) => Path.GetRelativePath(projectDirectory, path).Replace('\\', '/');

        var full = Evaluate(enableLiveHost: true, profile: "Full").Select(Relative).ToHashSet(StringComparer.Ordinal);
        var embedded = Evaluate(enableLiveHost: true, profile: "Embedded").Select(Relative).ToHashSet(StringComparer.Ordinal);

        var dropped = full.Except(embedded).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var added = embedded.Except(full).OrderBy(path => path, StringComparer.Ordinal).ToArray();

        Assert.Equal(EmbeddedExcluded.OrderBy(path => path, StringComparer.Ordinal), dropped);
        // Only the profile's own stand-ins may exist in the embedded profile alone.
        Assert.All(added, path => Assert.StartsWith("Profiles/Embedded/", path, StringComparison.Ordinal));
        Assert.NotEmpty(added);
    }

    [Fact]
    public void EmbeddedProfileKeepsTheSurfaceAnEmbedderCompilesAgainst()
    {
        var projectDirectory = Path.Combine(FindRepositoryRoot(), "bridge-mod/src/Spirectl.Sts2");
        var embedded = Evaluate(enableLiveHost: true, profile: "Embedded")
            .Select(path => Path.GetRelativePath(projectDirectory, path).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);

        string[] retained =
        [
            // The runtime ports, and the reference DTOs ISpirectlRuntime inherits.
            "Embedding/ISpirectlRuntime.cs",
            "Embedding/SpirectlRuntimeFacade.cs",
            "Core/Reference/IReferenceDataProvider.cs",
            "Core/Reference/PlaceholderReferenceDataProvider.cs",
            // The state DTOs and the placeholder the facade's extractor slot holds.
            "Core/State/IGameStateExtractor.cs",
            "Core/State/PlaceholderStateExtractor.cs",
            "Core/State/GameStateSnapshot.cs",
            "Core/State/PresentationScaffoldState.cs",
            // The seat registry the name hooks and the client-name action read.
            "Common/Sts2HostLocalSeatRegistry.cs",
            // The pieces of the live composition an embedder actually runs.
            "Live/Sts2StateProvider.cs",
            "Live/Sts2ActionHandler.cs",
            "Live/Sts2ActionHandler.Input.cs",
            "Live/Sts2ActionHandler.Scroll.cs",
            "Live/Sts2ActionHandler.Multiplayer.cs",
            "Live/Sts2ActionHandler.ShopMapLobby.cs",
            "Live/Sts2ActionHandler.RewardCommit.cs",
            "Profiles/Embedded/Sts2ActionHandler.Dispatch.cs",
            "Live/Sts2RuntimeSceneWatcher.cs",
            "Live/Sts2AssetExtractProvider.cs",
            "Live/Sts2ModelCatalogProvider.cs",
            "Live/Sts2ScreenContext.cs",
            "Live/Sts2MultiplayerConnectionHooks.cs",
            "Live/Sts2SyntheticLobbyNameHooks.cs",
            "Profiles/Embedded/Sts2RuntimeFactory.cs",
        ];

        Assert.Empty(retained.Where(path => !embedded.Contains(path)));
    }

    [Fact]
    public void EmbeddedDispatcherRoutesOnlyTheDeclaredKindsAndTheEnumStaysWhole()
    {
        var projectDirectory = Path.Combine(FindRepositoryRoot(), "bridge-mod/src/Spirectl.Sts2");
        var arm = new Regex(@"SemanticActionKind\.(\w+)\s*=>", RegexOptions.CultureInvariant);
        string[] Arms(string relativePath) =>
        [
            .. arm
                .Matches(File.ReadAllText(Path.Combine(projectDirectory, relativePath)))
                .Select(match => match.Groups[1].Value)
                .OrderBy(name => name, StringComparer.Ordinal),
        ];

        var full = Arms("Live/Sts2ActionHandler.Dispatch.cs");
        var embedded = Arms("Profiles/Embedded/Sts2ActionHandler.Dispatch.cs");

        Assert.Equal(EmbeddedActionKinds.OrderBy(name => name, StringComparer.Ordinal), embedded);
        // The embedded dispatcher is a subset of the full one, never a second source of routing.
        Assert.Empty(embedded.Except(full));
        Assert.True(full.Length > embedded.Length);
        // Dropping an arm must not drop the enum member: its ordinals are wire values.
        Assert.All(full, name => Assert.True(Enum.IsDefined(typeof(Spirectl.Sts2.Core.Actions.SemanticActionKind), name), name));
    }

    [Fact]
    public void EmbeddedCompositionInstallsTheFullOnesMinusTheDroppedLanes()
    {
        var projectDirectory = Path.Combine(FindRepositoryRoot(), "bridge-mod/src/Spirectl.Sts2");
        var install = new Regex(@"\b(Sts2\w+)\.Install\(", RegexOptions.CultureInvariant);
        string[] Installs(string relativePath) =>
        [
            .. install
                .Matches(File.ReadAllText(Path.Combine(projectDirectory, relativePath)))
                .Select(match => match.Groups[1].Value),
        ];

        var full = Installs("Live/Sts2ReusableLiveComposition.cs");
        var embedded = Installs("Profiles/Embedded/Sts2RuntimeFactory.cs");

        Assert.NotEmpty(full);
        // Same hooks in the same order, minus exactly the dropped ones: a hook added to one list and not the
        // other is a behavior difference between the bridge and the embedder that nobody chose.
        Assert.Equal(full.Where(name => !EmbeddedDroppedInstalls.Contains(name)), embedded);
        // A dropped install must also be a dropped file, or the type would compile in and never run.
        var excluded = EmbeddedExcluded.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);
        Assert.All(EmbeddedDroppedInstalls, name => Assert.Contains(name, excluded));
    }

    /// <summary>
    /// Per lane: the manifest members read only by code the embedded profile leaves out. They sit inside
    /// <c>#if !SPIRECTL_PROFILE_EMBEDDED</c> in that lane's manifest, so the embedded copy neither checks them at
    /// startup nor references their game types.
    /// </summary>
    public static TheoryData<string, string[]> EmbeddedManifestDrops => new()
    {
        {
            "V107",
            [
                "LobbyPlayerIds",
                "PlayerConnected", "PlayerChanged",
                "CombatPlayersReadyToBeginEnemyTurn",
                "MainMenuBlurBackstop", ".ctor",
            ]
        },
        {
            "V111",
            [
                "LobbyPlayerIds",
                "PlayerConnected", "PlayerChanged",
                "CombatTurnState", "TurnStatePlayersReadyToBeginEnemyTurn",
                "MainMenuBlurBackstop", ".ctor",
            ]
        },
    };

    [Theory]
    [MemberData(nameof(EmbeddedManifestDrops))]
    public void EmbeddedManifestDropsOnlyWhatExcludedLanesRead(string lane, string[] expectedDropped)
    {
        var manifest = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "bridge-mod/src/Spirectl.Sts2/GameApi", lane, "GameApiManifest.cs"));

        // The members an entry names: a GameApiNames constant, a nameof member, or a short quoted name.
        var member = new Regex(@"GameApiNames\.(\w+)|nameof\(\w+\.(\w+)\)|""(\w+|\.ctor)""", RegexOptions.CultureInvariant);
        string[] Members(string text) =>
        [
            .. member.Matches(text).Select(match => match.Groups.Cast<Group>().Skip(1).First(group => group.Success).Value),
        ];

        var dropped = new Regex(@"#if !SPIRECTL_PROFILE_EMBEDDED\r?\n(.*?)#endif", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        var droppedText = string.Concat(dropped.Matches(manifest).Select(match => match.Groups[1].Value));
        var sharedText = dropped.Replace(manifest, string.Empty);

        Assert.Equal(expectedDropped.Order(StringComparer.Ordinal), Members(droppedText).Order(StringComparer.Ordinal));

        // What an embedder still runs must stay pinned: the end-turn readiness pair, the damage preview, the
        // lobby roster reads the state builders make, and the spine and animation calls.
        var shared = Members(sharedText).ToHashSet(StringComparer.Ordinal);
        string[] mustKeep =
        [
            "AllPlayersReadyToEndTurn", "IsPlayerReadyToEndTurn", "ModifyDamage",
            "LobbyMaxPlayers", "LobbyPlayerIds", "PlayerCanRemoveOrUsePotions",
            "SetAnimation", "AddAnimation",
        ];
        // LobbyPlayerIds is dropped for the in-run lobby only; the saved-run lobby entry stays.
        Assert.Empty(mustKeep.Where(name => !shared.Contains(name)));
        Assert.Contains("LoadRunLobby", sharedText, StringComparison.Ordinal);
        Assert.DoesNotContain("NMainMenu", sharedText, StringComparison.Ordinal);
        Assert.DoesNotContain("NetHostGameService", sharedText, StringComparison.Ordinal);
        Assert.DoesNotContain("NCharacterSelectScreen", sharedText, StringComparison.Ordinal);
    }

    internal static IReadOnlyList<string> Evaluate(bool enableLiveHost, string? profile = null)
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
        if (profile is not null) start.ArgumentList.Add($"-p:Sts2Profile={profile}");

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet msbuild.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(30)), "Timed out evaluating the shared project.");
        Assert.True(process.ExitCode == 0, $"dotnet msbuild failed: {error}");

        using var document = JsonDocument.Parse(output);
        return
        [
            .. document.RootElement
                .GetProperty("Items")
                .GetProperty("Compile")
                .EnumerateArray()
                .Select(item => item.GetProperty("FullPath").GetString())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .OrderBy(path => path, StringComparer.Ordinal),
        ];
    }

    internal static string FindRepositoryRoot()
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
