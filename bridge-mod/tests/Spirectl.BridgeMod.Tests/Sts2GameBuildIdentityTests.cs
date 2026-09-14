using System;
using System.IO;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// The Godot-free identity ladder, against SYNTHETIC install trees: the appmanifest rung and the release_info
// read, plus the KeyValues reader underneath them. The Steamworks rung needs an initialized Steam client in the
// process, so it is proved by the couch-coop live leg (which logs which rung answered) rather than here — what
// IS covered here is that its absence falls through cleanly instead of throwing, which is the half that would
// otherwise only fail in front of a player.
//
// The manifest rung carries a SECOND job beyond its own answer: it is the test of whether the install we
// resolved is the one Steam mounted, and so whether the Steamworks rung is describing US at all. A second copy
// of the game launched from outside the Steam library, with Steam running, otherwise gets a confident branch for
// the OTHER install. That gate is not directly reachable without a Steam client, but its consequence is: the
// cases below pin that an install the manifest walk cannot identify reports NO branch — which is what the gate
// leaves behind once Steamworks is refused, and is a strictly better answer than a confident wrong one.
public sealed class Sts2GameBuildIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "spirectl-build-identity-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void ReadsTheMountedBranchAndBuildIdFromTheManifest()
    {
        var install = SeedInstall("Slay the Spire 2", Manifest("Slay the Spire 2", "23811903", mounted: "public"));

        var identity = Sts2GameBuildIdentity.TryReadAppManifest(install);

        Assert.NotNull(identity);
        Assert.Equal("public", identity!.Value.Branch);
        Assert.Equal(23811903, identity.Value.BuildId);
    }

    // MountedConfig is what is on disk; UserConfig is what the user last picked. They disagree exactly during a
    // branch switch, which is the one moment the answer matters, so the mounted value has to win.
    [Fact]
    public void MountedConfigWinsOverUserConfig()
    {
        var install = SeedInstall(
            "Slay the Spire 2",
            Manifest("Slay the Spire 2", "23811903", mounted: "public", user: "public-beta"));

        Assert.Equal("public", Sts2GameBuildIdentity.TryReadAppManifest(install)!.Value.Branch);
    }

    [Fact]
    public void FallsBackToUserConfigWhenNothingIsMounted()
    {
        var install = SeedInstall(
            "Slay the Spire 2",
            Manifest("Slay the Spire 2", "23811903", mounted: null, user: "public-beta"));

        Assert.Equal("public-beta", Sts2GameBuildIdentity.TryReadAppManifest(install)!.Value.Branch);
    }

    // An app on the default branch may carry no BetaKey at all. That absence IS "public" — reporting it as
    // unknown would push an ordinary player onto the weakest rung of the ladder.
    [Fact]
    public void AnAbsentBetaKeyIsTheDefaultBranch()
    {
        var install = SeedInstall(
            "Slay the Spire 2", Manifest("Slay the Spire 2", "23811903", mounted: null, user: null));

        Assert.Equal(Sts2GameBuildIdentity.DefaultBranch, Sts2GameBuildIdentity.TryReadAppManifest(install)!.Value.Branch);
    }

    // A manifest left behind by a moved or renamed install is worse than no manifest: believing it hands back a
    // branch for an install these files do not belong to.
    [Fact]
    public void AManifestNamingAnotherInstallIsRefused()
    {
        var install = SeedInstall("Slay the Spire 2", Manifest("Some Other Game", "23811903", mounted: "public"));

        Assert.Null(Sts2GameBuildIdentity.TryReadAppManifest(install));
    }

    [Fact]
    public void NoManifestIsNoAnswerRatherThanAGuess()
    {
        var install = SeedInstall("Slay the Spire 2", manifest: null);

        Assert.Null(Sts2GameBuildIdentity.TryReadAppManifest(install));
    }

    // steam_appid.txt is written by `sts2 game deploy`/`game launch`, not shipped by the game. Reading it would
    // work on every dev machine and degrade on a real player's install, so the app id is a constant and the
    // ladder must be indifferent to the file being absent.
    [Fact]
    public void ResolutionDoesNotDependOnSteamAppIdTxt()
    {
        var install = SeedInstall("Slay the Spire 2", Manifest("Slay the Spire 2", "23811903", mounted: "public-beta"));
        Assert.False(File.Exists(Path.Combine(install, "steam_appid.txt")));

        Assert.Equal("public-beta", Sts2GameBuildIdentity.TryReadAppManifest(install)!.Value.Branch);
    }

    [Fact]
    public void AMalformedManifestDegradesToNoAnswer()
    {
        var install = SeedInstall("Slay the Spire 2", "\"AppState\" { \"installdir\"  ");

        Assert.Null(Sts2GameBuildIdentity.TryReadAppManifest(install));
    }

    [Fact]
    public void ResolveReadsTheBuildFromReleaseInfoAndTheBranchFromTheManifest()
    {
        var install = SeedInstall("Slay the Spire 2", Manifest("Slay the Spire 2", "23811903", mounted: "public"));

        var identity = Sts2GameBuildIdentity.Resolve(install);

        Assert.Equal("v0.107.1", identity.Version);
        Assert.Equal(1692500715, identity.MainAssemblyHash);
        Assert.True(identity.HasBranch);
        // Steam is not initialized in a test process, so the Steamworks rung must fall through — not throw —
        // and the manifest rung must be the one that answers.
        Assert.Equal(Sts2GameBuildIdentity.AppManifestSource, identity.BranchSource);
        Assert.Equal("public", identity.Branch);
        Assert.Equal(23811903, identity.BuildId);
    }

    // The build identity and the branch are independent answers: an install Steam knows nothing about still
    // reports which game build it is, and an embedder can key a cache on that alone.
    [Fact]
    public void AnInstallOutsideSteamStillReportsItsBuild()
    {
        var install = SeedInstall("public-beta", manifest: null, version: "v0.111.0", hash: 1579942752);

        var identity = Sts2GameBuildIdentity.Resolve(install);

        Assert.Equal("v0.111.0", identity.Version);
        Assert.Equal(1579942752, identity.MainAssemblyHash);
        Assert.False(identity.HasBranch);
        Assert.Equal(Sts2GameBuildIdentity.UnknownSource, identity.BranchSource);
        Assert.Equal(0, identity.BuildId);
    }

    // The shape that started all this: a second copy of the game kept outside the Steam library and launched
    // directly, while Steam is running with the OTHER copy mounted. The manifest walk refuses to identify this
    // tree — there is a manifest two levels up, but it names a different install directory — and so neither rung
    // may speak for it. An empty branch is the honest answer; "public" (which is what the mounted install is on)
    // would key this build's caches to the other build's.
    [Fact]
    public void AnInstallTheManifestDoesNotNameReportsNoBranch()
    {
        var install = SeedInstall(
            "Slay the Spire 2 (beta copy)",
            Manifest("Slay the Spire 2", "23811903", mounted: "public"),
            version: "v0.111.0",
            hash: 1579942752);

        var identity = Sts2GameBuildIdentity.Resolve(install);

        Assert.False(identity.HasBranch);
        Assert.Equal(Sts2GameBuildIdentity.UnknownSource, identity.BranchSource);
        Assert.Equal(0, identity.BuildId);
        // …and the BUILD is still read, from this copy's own release_info.json. That is what keeps a cache
        // keyed on the identity correct even when the branch is unknown — and why the browser asset token
        // moved between these two installs all along.
        Assert.Equal("v0.111.0", identity.Version);
        Assert.Equal(1579942752, identity.MainAssemblyHash);
    }

    [Fact]
    public void AnUnreadableInstallRootIsUnknownRatherThanAThrow()
    {
        var identity = Sts2GameBuildIdentity.Resolve(Path.Combine(_root, "does-not-exist"));

        Assert.Equal(string.Empty, identity.Version);
        Assert.Equal(0, identity.MainAssemblyHash);
        Assert.False(identity.HasBranch);
    }

    [Fact]
    public void AMalformedReleaseInfoDegradesToNoBuildIdentity()
    {
        var install = Path.Combine(_root, "steamapps", "common", "Slay the Spire 2");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "release_info.json"), "{ not json");

        var identity = Sts2GameBuildIdentity.Resolve(install);

        Assert.Equal(string.Empty, identity.Version);
        Assert.Equal(0, identity.MainAssemblyHash);
    }

    // --- helpers -----------------------------------------------------------------------------------------

    // `<root>/steamapps/common/<installDir>` plus, optionally, `<root>/steamapps/appmanifest_<appid>.acf` —
    // exactly the two-levels-up relationship the reader relies on, and the same on Windows and Linux.
    private string SeedInstall(
        string installDir,
        string? manifest,
        string version = "v0.107.1",
        int hash = 1692500715)
    {
        var steamApps = Path.Combine(_root, "steamapps");
        var install = Path.Combine(steamApps, "common", installDir);
        Directory.CreateDirectory(install);
        File.WriteAllText(
            Path.Combine(install, "release_info.json"),
            $$"""
            {
              "commit": "59260271",
              "version": "{{version}}",
              "date": "2026-06-18T15:43:56-07:00",
              "branch": "{{version}}",
              "main_assembly_hash": {{hash}}
            }
            """);

        if (manifest is not null)
        {
            File.WriteAllText(
                Path.Combine(steamApps, $"appmanifest_{Sts2GameBuildIdentity.SteamAppId}.acf"), manifest);
        }

        return install;
    }

    private static string Manifest(string installDir, string buildId, string? mounted, string? user = null)
        => $$"""
        "AppState"
        {
        	"appid"		"{{Sts2GameBuildIdentity.SteamAppId}}"
        	"name"		"Slay the Spire 2"
        	"installdir"		"{{installDir}}"
        	"buildid"		"{{buildId}}"
        	"InstalledDepots"
        	{
        		"2868843"
        		{
        			"manifest"		"7881097836989620034"
        		}
        	}
        	"UserConfig"
        	{
        		"language"		"english"
        {{(user is null ? string.Empty : $"\t\t\"BetaKey\"\t\t\"{user}\"")}}
        	}
        	"MountedConfig"
        	{
        		"language"		"english"
        {{(mounted is null ? string.Empty : $"\t\t\"BetaKey\"\t\t\"{mounted}\"")}}
        	}
        }
        """;
}
