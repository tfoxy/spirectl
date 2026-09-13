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

        // Reported as the refusal the bridge would print at startup, so a failing leg reads exactly like the
        // thing a maintainer has to act on.
        Assert.True(missing.Count == 0, Sts2GameApiProbe.BuildRefusal(missing, InstalledBuildLabel));
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
    public void GameApiProbeRefusalNamesTheLaneAndEveryMissingMember()
    {
        var refusal = Sts2GameApiProbe.BuildRefusal(["Some.Game.Type.SomeMember — because"], "v9.99.9");

        Assert.Contains("refuses to start", refusal);
        Assert.Contains(GameApiLane.Name, refusal);
        Assert.Contains("v9.99.9", refusal);
        Assert.Contains("Some.Game.Type.SomeMember", refusal);
        Assert.Contains("Sts2GameApi.props", refusal);
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
