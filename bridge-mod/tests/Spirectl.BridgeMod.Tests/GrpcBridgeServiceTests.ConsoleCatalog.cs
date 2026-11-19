using Spirectl.BridgeMod.Services;
using Spirectl.Sts2;
using Spirectl.Sts2.Core.ConsoleCommands;
using Spirectl.Proto.V0;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed partial class GrpcBridgeServiceTests
{
    [Fact]
    public void ConsoleCatalogMapsDescriptorsAndCompletions()
    {
        var service = new GrpcBridgeService(RuntimeWithConsoleExecutor(
            new FixedConsoleCommandExecutor(true), new FixedConsoleCommandCatalog()));
        var catalog = service.HandleListConsoleCommands(new() { RequestId = "catalog" }).Success;
        Assert.Equal("catalog", catalog.RequestId);
        var command = Assert.Single(catalog.Commands);
        Assert.Equal("fight", command.Name);
        Assert.Equal("<id>", command.Usage);
        Assert.Equal("Start an encounter", command.Description);
        Assert.True(command.Networked);
        Assert.False(command.DebugOnly);
        Assert.Equal(DataSource.Live, catalog.Source);
        Assert.False(catalog.Provisional);
        Assert.Equal("catalog-notice", Assert.Single(catalog.Notices).Code);
        var completion = service.HandleCompleteConsoleArguments(new()
        {
            RequestId = "complete", Command = "fight", Args = { "", "second" }, Prefix = "KN",
        }).Success;
        Assert.Equal("complete", completion.RequestId);
        Assert.True(completion.KnownCommand);
        Assert.Equal(2u, completion.ArgumentIndex);
        Assert.Equal("KNIGHTS_ELITE", Assert.Single(completion.Candidates));
        var candidate = Assert.Single(completion.CandidateDetails);
        Assert.Equal("KNIGHTS_ELITE", candidate.Value);
        Assert.Equal("Knight Gang", candidate.Label);
        Assert.Equal("KNIGHTS_ELITE (Knight Gang)", candidate.Raw);
        Assert.Equal("fight KNIGHTS_ELITE ", completion.CommonPrefix);
        Assert.Equal("argument", completion.CompletionType);
        Assert.Equal("fight  second ", completion.CommandPrefix);
        Assert.Equal(DataSource.Live, completion.Source);
        Assert.Equal("completion-notice", Assert.Single(completion.Notices).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConsoleCatalogMapsPlaceholderAndRuntimeErrors(bool fail)
    {
        var runtime = fail
            ? RuntimeWithConsoleExecutor(new FixedConsoleCommandExecutor(true), new FixedConsoleCommandCatalog(true))
            : BridgeRuntimeBootstrap.CreateScaffold();
        var service = new GrpcBridgeService(runtime);
        var expected = fail ? BridgeErrorCode.RuntimeFailure : BridgeErrorCode.NotImplemented;
        var catalog = service.HandleListConsoleCommands(new() { RequestId = "catalog" });
        var completion = service.HandleCompleteConsoleArguments(new() { Command = "fight" });
        Assert.Null(catalog.Success);
        Assert.Equal(expected, catalog.Error.Code);
        Assert.Null(completion.Success);
        Assert.Equal(expected, completion.Error.Code);
        if (fail)
            Assert.Equal("catalog", Assert.Single(catalog.Error.Details).Field);
    }

    [Theory]
    [InlineData("KNIGHTS_ELITE (Knight Gang)", "KNIGHTS_ELITE", "Knight Gang")]
    [InlineData("KNIGHTS_ELITE", "KNIGHTS_ELITE", "")]
    [InlineData("two words", "two words", "")]
    [InlineData("two words (label)", "two words (label)", "")]
    public void ConsoleCompletionSplitsCandidateLabels(string raw, string value, string label)
        => Assert.Equal((value, label), ConsoleCommandCatalogMapping.SplitCandidate(raw));

    [Fact]
    public void ConsoleCompletionAssemblyPreservesEmptyPositionsAndPrefix()
    {
        Assert.Equal(new[] { "", "second", "" }, ConsoleCommandCatalogMapping.Arguments(["", "second"], ""));
        Assert.Equal(new[] { "KN" }, ConsoleCommandCatalogMapping.Arguments([], "KN"));
        Assert.Equal("fight ", ConsoleCommandCatalogMapping.CommandPrefix("fight", []));
        Assert.Equal("card  deck ", ConsoleCommandCatalogMapping.CommandPrefix("card", ["", "deck"]));
    }

    [Theory]
    [InlineData("Command", "command")]
    [InlineData("Subcommand", "subcommand")]
    [InlineData("Argument", "argument")]
    public void ConsoleCompletionMapsType(string input, string expected)
        => Assert.Equal(expected, ConsoleCommandCatalogMapping.CompletionType(input));
}
