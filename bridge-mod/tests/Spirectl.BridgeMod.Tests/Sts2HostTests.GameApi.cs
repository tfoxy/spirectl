using Spirectl.Sts2;
using Spirectl.Sts2.Live.GameApi;
using Xunit;
using Xunit.Abstractions;

namespace Spirectl.BridgeMod.Tests;

/// <summary>
/// The startup API gate, run against whatever game assemblies this test leg was pointed at. This is the leg
/// that says "the lane this build compiled is actually the lane this game build needs" — so run it against
/// every supported install (<c>scripts/validate.sh bridge-live-host-tests --assemblies-dir …</c>).
/// </summary>
public sealed partial class Sts2HostTests(ITestOutputHelper output)
{
    // The probe's own label for the build is the game's release record, which only a running game can read.
    private const string InstalledBuildLabel = "<the assemblies this leg was pointed at>";

    [Fact]
    public void GameApiManifestResolvesAgainstTheInstalledGameAssemblies()
    {
        var missing = Sts2GameApiProbe.FindMissing(GameApiManifest.Requirements);

        // Verification remains strict even though optional runtime failures only degrade the bridge.
        Assert.True(missing.Count == 0, Sts2GameApiProbe.DescribeMissing(missing, InstalledBuildLabel));
        Assert.NotEmpty(GameApiManifest.Requirements);

        // And the clean verdict, in the startup log's own words: the lane, and how many members it declares.
        // A lane that quietly stopped declaring what it reads shows up here as a falling count.
        output.WriteLine(
            Sts2GameApiProbe.DescribeVerdict(GameApiManifest.Requirements.Count, InstalledBuildLabel));
    }

    [Fact]
    public void GameApiProbeReportsAMemberThatIsNotThere()
    {
        var bogus = new GameApiRequirement(
            typeof(string),
            "AMemberNoGameBuildHas",
            GameApiMemberKind.Value,
            Note: "a deliberately impossible requirement");

        var missing = Sts2GameApiProbe.FindMissing([bogus]);

        Assert.Single(missing);
        Assert.Contains("AMemberNoGameBuildHas", missing[0]);
        Assert.Contains("a deliberately impossible requirement", missing[0]);
    }

    [Fact]
    public void GameApiProbeDiagnosticNamesTheLaneAndEveryMissingMember()
    {
        var refusal = Sts2GameApiProbe.DescribeMissing(["Some.Game.Type.SomeMember — because"], "v9.99.9");

        Assert.Contains("compatibility check found", refusal);
        Assert.Contains(GameApiLane.Name, refusal);
        Assert.Contains("v9.99.9", refusal);
        Assert.Contains("Some.Game.Type.SomeMember", refusal);
        Assert.Contains("Sts2GameApi.props", refusal);
    }

    /// <summary>
    /// A manifest that cannot load is reported with both build identities.
    /// </summary>
    [Fact]
    public void GameApiProbeReportsALaneItCannotLoadAndNamesBothBuilds()
    {
        var refusal = Sts2GameApiProbe.DescribeLaneLoadFailure(
            new TypeLoadException("Could not load type 'A.Game.Type' from assembly 'sts2'."),
            "v9.99.9");

        Assert.Contains("is degraded", refusal);
        Assert.DoesNotContain("refuses to start", refusal);
        Assert.Contains(GameApiLane.Name, refusal);
        Assert.Contains("v9.99.9", refusal);
        Assert.Contains("A.Game.Type", refusal);
        Assert.Contains(nameof(TypeLoadException), refusal);
        Assert.Contains("install-bridge", refusal);
        Assert.Contains("Sts2GameApi.props", refusal);

        // The build this payload IS. A source build stamps it; a reference-SDK build cannot, and then the
        // refusal has to say so rather than print an empty version and read as "compiled for nothing".
        Assert.Contains(
            string.IsNullOrWhiteSpace(BridgeBuildInfo.BuiltAgainstGameVersion)
                ? "a build it does not record"
                : BridgeBuildInfo.BuiltAgainstGameVersion,
            refusal);
    }

    [Fact]
    public void GameApiProbeMatchesAMethodByItsExactParameterList()
    {
        var present = new GameApiRequirement(
            typeof(string),
            nameof(string.StartsWith),
            GameApiMemberKind.Method,
            [typeof(string)]);
        var wrongArity = new GameApiRequirement(
            typeof(string),
            nameof(string.StartsWith),
            GameApiMemberKind.Method,
            [typeof(string), typeof(string), typeof(string)]);

        Assert.Empty(Sts2GameApiProbe.FindMissing([present]));
        Assert.Single(Sts2GameApiProbe.FindMissing([wrongArity]));
    }

    [Fact]
    public void GameApiProbeMatchesAMethodByParameterTypeNameWhenTheTypeIsInaccessible()
    {
        var byName = new GameApiRequirement(
            typeof(string),
            nameof(string.StartsWith),
            GameApiMemberKind.Method,
            ParameterTypeNames: ["String"]);
        var wrongName = new GameApiRequirement(
            typeof(string),
            nameof(string.StartsWith),
            GameApiMemberKind.Method,
            ParameterTypeNames: ["NotAType"]);

        Assert.Empty(Sts2GameApiProbe.FindMissing([byName]));
        Assert.Single(Sts2GameApiProbe.FindMissing([wrongName]));
    }

    /// <summary>
    /// A requirement whose DECLARING type is inaccessible from the bridge names it by string instead, and the
    /// probe resolves it out of the loaded game assembly. Uses a game type both supported builds declare, so
    /// the mechanism is checked on every lane.
    /// </summary>
    [Fact]
    public void GameApiProbeResolvesAnOwnerNamedByStringOutOfTheGameAssembly()
    {
        var byFullName = new GameApiRequirement(
            Owner: null,
            Member: "ReleaseInfo",
            Kind: GameApiMemberKind.Value,
            OwnerTypeName: "MegaCrit.Sts2.Core.Debug.ReleaseInfoManager");
        var bySimpleName = byFullName with { OwnerTypeName = "ReleaseInfoManager" };
        var unknownOwner = byFullName with { OwnerTypeName = "NoGameTypeIsCalledThis" };
        var unknownMember = byFullName with { Member = "NoGameMemberIsCalledThis" };

        Assert.Empty(Sts2GameApiProbe.FindMissing([byFullName]));
        Assert.Empty(Sts2GameApiProbe.FindMissing([bySimpleName]));
        Assert.Single(Sts2GameApiProbe.FindMissing([unknownOwner]));
        Assert.Single(Sts2GameApiProbe.FindMissing([unknownMember]));

        // The refusal names the owner as written, so a missing type reads as its own line.
        Assert.Equal(
            "NoGameTypeIsCalledThis.ReleaseInfo",
            Sts2GameApiProbe.FindMissing([unknownOwner])[0]);
    }

    /// <summary>
    /// Both hops of the enemy-turn readiness set the host-local seat turn watcher completes. The lane's own
    /// accessor reads them by name, so a rename would leave the watcher silently inert; these are the
    /// manifest entries that turn that into a refusal. Named explicitly rather than left to the manifest sweep
    /// so the regression that produced them cannot come back by way of a dropped declaration.
    /// </summary>
    [Fact]
    public void GameApiManifestDeclaresTheEnemyTurnReadinessSetTheTurnWatcherReads()
    {
        var readiness = GameApiManifest.Requirements
            .Where(requirement => requirement.Member.Contains("ReadyToBeginEnemyTurn", StringComparison.Ordinal)
                || requirement.Member.Contains("turnState", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(readiness);
        Assert.Empty(Sts2GameApiProbe.FindMissing(readiness));
    }
}
