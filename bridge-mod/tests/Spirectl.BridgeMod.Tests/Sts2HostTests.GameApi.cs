using Spirectl.Sts2.Live.GameApi;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

/// <summary>
/// The startup API gate, run against whatever game assemblies this test leg was pointed at. This is the leg
/// that says "the lane this build compiled is actually the lane this game build needs" — so run it against
/// every supported install (<c>scripts/validate.sh bridge-live-host-tests --assemblies-dir …</c>).
/// </summary>
public sealed partial class Sts2HostTests
{
    [Fact]
    public void GameApiManifestResolvesAgainstTheInstalledGameAssemblies()
    {
        var missing = Sts2GameApiProbe.FindMissing(GameApiManifest.Requirements);

        Assert.Empty(missing);
        Assert.NotEmpty(GameApiManifest.Requirements);
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
}
