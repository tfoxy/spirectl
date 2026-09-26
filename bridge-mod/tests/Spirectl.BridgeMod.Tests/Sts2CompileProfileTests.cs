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
        // Stand-ins (the embedded composition, dispatcher with its route table, action-descriptor catalog and
        // refusal helper replace these).
        "Live/Sts2RuntimeFactory.cs",
        "Live/Sts2ReusableLiveComposition.cs",
        "GameApi/Sts2GameApiProbe.GameVersion.cs",
        "Live/Sts2ActionHandler.Dispatch.cs",
        "Common/Sts2ActionDescriptorCatalog.cs",
        // The legacy state-extractor lane: the port, its placeholder and the scaffold the placeholder builds from,
        // and the snapshot types only that lane's port carried (the rest of the file is what StateSnapshot uses).
        "Core/State/GameStateSnapshot.Full.cs",
        "Core/State/IGameStateExtractor.cs",
        "Core/State/PlaceholderStateExtractor.cs",
        "Core/State/PresentationScaffoldState.cs",
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
        "Live/Sts2ActionHandler.RewardCommit.cs",
        "Live/Sts2ActionHandler.ScreenIntents.Full.cs",
        "Live/Sts2ActionHandler.ShopMapLobby.Full.cs",
        "Live/Sts2RewardElementChoice.cs",
        "Live/Sts2RewardCaptureRegistry.cs",
        "Live/Sts2RewardScreenInspector.cs",
        "Live/Sts2RewardsCaptureHooks.cs",
        // The state-side action catalog's moved half (the retained half keeps what a live arm still reads).
        "Common/Sts2ActionCatalog.Full.cs",
        // Left with no caller once those bodies are gone.
        "Live/Sts2MainMenuStartRunHooks.cs",
        "Live/Sts2CrystalSphereScreenInspector.cs",
        "Common/Sts2LobbyCharacterButtonInvoker.cs",
        "Common/Sts2PartialChoiceNotice.cs",
        "Core/Map/Sts2MapDrawingTransform.cs",
        // Full semantic-state path and its state-only observation hooks.
        "Core/State/StateSnapshot.cs",
        "Live/Sts2StateProvider.cs",
        "Live/CharacterSelectStateBuilder.cs",
        "Live/CombatStateBuilder.cs",
        "Live/MapStateBuilder.cs",
        "Live/RoomStateBuilder.cs",
        "Live/RunShellStateBuilder.cs",
        "Live/StateProjectionValues.cs",
        "Live/Sts2CardStateSnapshotFactory.cs",
        "Live/Sts2RewardsOverlayInspector.cs",
        "Live/Sts2CardSelectionScreenInspector.cs",
        "Live/Sts2CrystalSphereOverlayInspector.cs",
        "Live/Sts2DeckCardSelectionOverlayHooks.cs",
        "Live/Sts2EventRoomScreenInspector.cs",
        "Live/Sts2RestSiteScreenInspector.cs",
        "Live/Sts2TreasureRoomScreenInspector.cs",
        "Live/Sts2CombatPreviewCore.cs",
        "Live/Sts2DamageEventHooks.cs",
        "Live/Sts2CardUpgradeEventHooks.cs",
        "Live/Sts2CombatEventCapture.cs",
        "Embedding/EmbeddableCombatEventHub.cs",
        "Embedding/EmbeddableStateSubscriptionHub.cs",
        "Embedding/EmbeddableStateFingerprint.cs",
        "Live/Sts2StateWatchRuntimeSettings.cs",
        "Live/Sts2SemanticStateRevision.cs",
        "Common/Sts2RunOverlayRegistry.cs",
        "Core/State/CardDescriptionTemplate.cs",
        "Core/State/StateDeckViewSortNormalizer.cs",
        "Live/Sts2CardDescriptionTemplateFactory.cs",
        "Live/Sts2HoverTipProjection.cs",
        "Live/Sts2CardHostValueResolver.cs",
        "Live/Sts2AuthoredTransientEffectsRegistry.cs",
        "Live/Sts2ChooseACardOverlayHooks.cs",
        "Live/Sts2HandSelectionHooks.cs",
        "Live/MarkerDynamicVar.cs",

    ];

    /// <summary>The semantic action kinds the embedded dispatcher routes: what an embedder in this repo family sends.</summary>
    private static readonly string[] EmbeddedActionKinds =
    [
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
        "Sts2ChooseACardOverlayHooks",
        "Sts2HandSelectionHooks",
        "Sts2DamageEventHooks",
        "Sts2CardUpgradeEventHooks",
        "Sts2RewardsCaptureHooks",
    ];

    [Theory]
    [InlineData("V107")]
    [InlineData("V111")]
    public void EmbeddedProfileHasNoExcludedGameApiHelpers(string lane)
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "bridge-mod/src/Spirectl.Sts2/GameApi", lane, "GameApiMembers.cs"));
        var guard = new Regex(@"#if !SPIRECTL_PROFILE_EMBEDDED\r?\n(.*?)#endif", RegexOptions.Singleline);
        var fullOnly = string.Concat(guard.Matches(source).Select(match => match.Groups[1].Value));
        var embedded = guard.Replace(source, string.Empty);

        Assert.Contains("internal static decimal ModifyDamage", fullOnly);
        Assert.Contains("class GameApiPlayer", fullOnly);
        Assert.Contains("LobbyMaxPlayers", fullOnly);
        Assert.Contains("PlayerCanRemoveOrUsePotions", fullOnly);
        Assert.DoesNotContain("internal static decimal ModifyDamage", embedded);
        Assert.DoesNotContain("class GameApiPlayer", embedded);
        Assert.DoesNotContain("internal const string LobbyMaxPlayers", embedded);
        Assert.DoesNotContain("internal const string PlayerCanRemoveOrUsePotions", embedded);
        Assert.Contains("class GameApiNetHost", fullOnly);
        Assert.Contains("class GameApiMainMenu", fullOnly);
        Assert.DoesNotContain("class GameApiNetHost", embedded);
        Assert.DoesNotContain("class GameApiMainMenu", embedded);
    }

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
            // Action DTOs remain even though the semantic state provider does not.
            "Core/State/GameStateSnapshot.cs",
            // The seat registry the name hooks and the client-name action read.
            "Common/Sts2HostLocalSeatRegistry.cs",
            // The pieces of the live composition an embedder actually runs.
            "Live/Sts2ActionHandler.cs",
            "Live/Sts2ActionHandler.Input.cs",
            "Live/Sts2ActionHandler.Scroll.cs",
            "Live/Sts2ActionHandler.Multiplayer.cs",
            "Live/Sts2ActionHandler.ShopMapLobby.cs",
            "Profiles/Embedded/Sts2ActionHandler.Dispatch.cs",
            "Profiles/Embedded/Sts2ActionDescriptorCatalog.cs",
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
        var embedded = EmbeddedRoutes(File.ReadAllText(Path.Combine(
                projectDirectory, "Profiles/Embedded/Sts2ActionHandler.Dispatch.cs")))
            .Select(route => route.Kind)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(EmbeddedActionKinds.OrderBy(name => name, StringComparer.Ordinal), embedded);
        // The embedded dispatcher is a subset of the full one, never a second source of routing.
        Assert.Empty(embedded.Except(full));
        Assert.True(full.Length > embedded.Length);
        // Dropping an arm must not drop the enum member: its ordinals are wire values.
        Assert.All(full, name => Assert.True(Enum.IsDefined(typeof(Spirectl.Sts2.Core.Actions.SemanticActionKind), name), name));
    }

    /// <summary>
    /// The embedded dispatcher's route table, read from source: id, kind, and the action body the route runs.
    /// </summary>
    private static (string Id, string Kind, string Body)[] EmbeddedRoutes(string dispatcherSource)
    {
        var route = new Regex(
            @"RouteFor\(\s*""([\w-]+)"",\s*SemanticActionKind\.(\w+),.*?handler\.Execute(\w+)\(request\)",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);
        return
        [
            .. route.Matches(dispatcherSource)
                .Select(match => (match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value)),
        ];
    }

    [Fact]
    public void EmbeddedActionCatalogListsExactlyTheKindsTheEmbeddedDispatcherRoutes()
    {
        var projectDirectory = Path.Combine(FindRepositoryRoot(), "bridge-mod/src/Spirectl.Sts2");
        string Read(string relativePath) => File.ReadAllText(Path.Combine(projectDirectory, relativePath));
        var dispatcher = Read("Profiles/Embedded/Sts2ActionHandler.Dispatch.cs");
        var catalog = Read("Profiles/Embedded/Sts2ActionDescriptorCatalog.cs");

        // One list. The dispatcher looks a kind up in its route table, the catalog returns that table's
        // descriptors, and neither restates a kind on its own, so they cannot drift apart.
        Assert.Empty(new Regex(@"SemanticActionKind\.\w+\s*=>", RegexOptions.CultureInvariant).Matches(dispatcher));
        Assert.Contains("EmbeddedRoutesByKind.TryGetValue(request.Kind", dispatcher, StringComparison.Ordinal);
        Assert.DoesNotContain("SemanticActionKind.", catalog, StringComparison.Ordinal);
        Assert.Contains("Sts2ActionHandler.RoutedActionDescriptors", catalog, StringComparison.Ordinal);

        // The catalog's list is the declared set, each route runs the body named for its kind, and each id is the
        // kind's kebab-case name (the id is a wire value the full catalog spells the same way).
        var routes = EmbeddedRoutes(dispatcher);
        Assert.Equal(
            EmbeddedActionKinds.OrderBy(name => name, StringComparer.Ordinal),
            routes.Select(route => route.Kind).OrderBy(name => name, StringComparer.Ordinal));
        Assert.All(routes, route =>
        {
            Assert.Equal(route.Kind, route.Body);
            Assert.Equal(Regex.Replace(route.Kind, "(?<!^)([A-Z])", "-$1").ToLowerInvariant(), route.Id);
            Assert.True(Enum.IsDefined(typeof(Spirectl.Sts2.Core.Actions.SemanticActionKind), route.Kind), route.Kind);
        });

        // A kind the full catalog already advertises keeps its id; and keeps its wording too, except mouse-click,
        // which the full catalog words as a dangerous raw click and only advertises in dangerous mode.
        var entry = new Regex(
            @"\(\s*""([\w-]+)"",\s*SemanticActionKind\.(\w+),\s*""([^""]*)"",\s*""([^""]*)""",
            RegexOptions.CultureInvariant);
        Dictionary<string, (string Id, string Summary, string Hint)> Entries(string source) =>
            entry.Matches(source).ToDictionary(
                match => match.Groups[2].Value,
                match => (match.Groups[1].Value, match.Groups[3].Value, match.Groups[4].Value));
        var full = Entries(Read("Common/Sts2ActionDescriptorCatalog.cs"));
        var embedded = Entries(dispatcher);
        var shared = embedded.Keys.Intersect(full.Keys).ToArray();
        Assert.Contains("SelectMapNode", shared);
        Assert.Contains("ClaimReward", full.Keys);
        Assert.DoesNotContain("ClaimReward", embedded.Keys);
        Assert.All(shared, kind =>
        {
            Assert.Equal(full[kind].Id, embedded[kind].Id);
            if (kind != "MouseClick")
            {
                Assert.Equal(full[kind], embedded[kind]);
            }
        });
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
                "LobbyMaxPlayers", "LobbyPlayerIds", "LobbyPlayerIds",
                "PlayerCanRemoveOrUsePotions", "ModifyDamage",
                "PlayerConnected", "PlayerChanged",
                "CombatPlayersReadyToBeginEnemyTurn",
                "MainMenuBlurBackstop", ".ctor",
            ]
        },
        {
            "V111",
            [
                "LobbyMaxPlayers", "LobbyPlayerIds", "LobbyPlayerIds",
                "PlayerCanRemoveOrUsePotions", "ModifyDamage",
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

        // What an embedder still runs must stay pinned: the local lobby roster, end-turn readiness,
        // and the spine and animation calls. Full retains every dropped entry above.
        var shared = Members(sharedText).ToHashSet(StringComparer.Ordinal);
        string[] mustKeep =
        [
            "LocalPlayer", "Players", "AllPlayersReadyToEndTurn", "IsPlayerReadyToEndTurn",
            "SetAnimation", "AddAnimation",
        ];
        Assert.Empty(mustKeep.Where(name => !shared.Contains(name)));
        if (lane == "V107") Assert.Contains("IsDebugEncounter", shared);
        foreach (var stateOnly in new[] { "LobbyMaxPlayers", "LobbyPlayerIds", "PlayerCanRemoveOrUsePotions", "ModifyDamage" })
        {
            Assert.DoesNotContain(stateOnly, shared);
            Assert.Contains(stateOnly, Members(manifest));
        }
        Assert.DoesNotContain("LoadRunLobby", sharedText, StringComparison.Ordinal);
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
