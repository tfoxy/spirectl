using HotMod.Contracts;
using HotMod.Shell.Runtime;
using Xunit;

namespace HotMod.ReloadTests;

public sealed class ReloadManagerTests
{
    [Fact]
    public async Task ReloadManagerLoadsValidLogicFromShadowPath()
    {
        var source = await FixtureLogicBuilder.BuildAsync("ValidLogic");
        var manager = new ReloadManager(new TestHotHost());

        var outcome = await manager.ReloadAsync(Request(source), currentGeneration: null);

        Assert.Equal(ReloadStatus.Loaded, outcome.Report.Status);
        Assert.NotNull(outcome.NewGeneration);
        Assert.Contains(Path.Combine(".shadow", "generation-1"), outcome.Report.ShadowAssemblyPath);
        Assert.NotEqual(source, outcome.NewGeneration!.Logic!.GetType().Assembly.Location);
    }

    [Fact]
    public async Task ContractMismatchReturnsStructuredFailure()
    {
        var source = await FixtureLogicBuilder.BuildAsync("ContractMismatchLogic");
        var manager = new ReloadManager(new TestHotHost());

        var outcome = await manager.ReloadAsync(Request(source), currentGeneration: null);

        Assert.Equal(ReloadStatus.Failed, outcome.Report.Status);
        Assert.Equal(ReloadErrorCode.ReloadContractVersionMismatch, outcome.Report.Error?.Code);
        Assert.Null(outcome.NewGeneration);
    }

    [Fact]
    public async Task ContractMissingReturnsStructuredFailure()
    {
        var source = await FixtureLogicBuilder.BuildAsync("ContractMissingLogic");
        var manager = new ReloadManager(new TestHotHost());

        var outcome = await manager.ReloadAsync(Request(source), currentGeneration: null);

        Assert.Equal(ReloadErrorCode.ReloadContractMissing, outcome.Report.Error?.Code);
        Assert.Null(outcome.NewGeneration);
    }

    [Fact]
    public async Task ReloadManagerSwapsGenerationsAtomically()
    {
        var firstSource = await FixtureLogicBuilder.BuildAsync("ValidLogic");
        var secondSource = await FixtureLogicBuilder.BuildAsync("SecondValidLogic");
        var manager = new ReloadManager(new TestHotHost());
        var first = await manager.ReloadAsync(Request(firstSource), currentGeneration: null);

        var second = await manager.ReloadAsync(Request(secondSource), first.NewGeneration);

        Assert.Equal(ReloadStatus.Loaded, second.Report.Status);
        Assert.Equal(2, second.NewGeneration?.Generation);
        Assert.Same(first.NewGeneration, second.OldGenerationToDispose);
    }

    [Fact]
    public async Task OldGenerationRemainsActiveWhenNewLoadFails()
    {
        var validSource = await FixtureLogicBuilder.BuildAsync("ValidLogic");
        var failingSource = await FixtureLogicBuilder.BuildAsync("ActivationFailedLogic");
        var manager = new ReloadManager(new TestHotHost());
        var first = await manager.ReloadAsync(Request(validSource), currentGeneration: null);

        var failed = await manager.ReloadAsync(Request(failingSource), first.NewGeneration);
        var result = first.NewGeneration!.Logic!.OnHook(new HotHookContext("combat.turn", first.NewGeneration.Generation, new Dictionary<string, string>()));

        Assert.Equal(ReloadErrorCode.ReloadActivationFailed, failed.Report.Error?.Code);
        Assert.Null(failed.NewGeneration);
        Assert.True(result.Handled);
        Assert.Equal(["valid"], result.Messages);
    }

    [Fact]
    public async Task InitializationFailureReturnsStructuredFailure()
    {
        var source = await FixtureLogicBuilder.BuildAsync("InitializationFailedLogic");
        var manager = new ReloadManager(new TestHotHost());

        var outcome = await manager.ReloadAsync(Request(source), currentGeneration: null);

        Assert.Equal(ReloadErrorCode.ReloadInitializationFailed, outcome.Report.Error?.Code);
        Assert.Null(outcome.NewGeneration);
    }

    [Fact]
    public async Task MissingEntryTypeReturnsStructuredFailure()
    {
        var source = await FixtureLogicBuilder.BuildAsync("NoEntryLogic");
        var manager = new ReloadManager(new TestHotHost());

        var outcome = await manager.ReloadAsync(Request(source), currentGeneration: null);

        Assert.Equal(ReloadErrorCode.ReloadEntryTypeMissing, outcome.Report.Error?.Code);
    }

    [Fact]
    public async Task AmbiguousEntryTypeReturnsStructuredFailure()
    {
        var source = await FixtureLogicBuilder.BuildAsync("AmbiguousEntryLogic");
        var manager = new ReloadManager(new TestHotHost());

        var outcome = await manager.ReloadAsync(Request(source), currentGeneration: null);

        Assert.Equal(ReloadErrorCode.ReloadEntryTypeAmbiguous, outcome.Report.Error?.Code);
    }

    [Fact]
    public async Task SourceMissingReturnsStructuredFailure()
    {
        var manager = new ReloadManager(new TestHotHost());
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "HotMod.Logic.dll");

        var outcome = await manager.ReloadAsync(Request(missing), currentGeneration: null);

        Assert.Equal(ReloadErrorCode.ReloadSourceMissing, outcome.Report.Error?.Code);
    }

    [Fact]
    public async Task SourceChangedDuringStabilityCheckReturnsStructuredFailure()
    {
        var source = await FixtureLogicBuilder.BuildAsync("ValidLogic");
        var tempDirectory = Directory.CreateTempSubdirectory("hotmod-changing-source-");
        var changingSource = Path.Combine(tempDirectory.FullName, "HotMod.Logic.dll");
        File.Copy(source, changingSource);
        var manager = new ReloadManager(new TestHotHost());

        var reload = manager.ReloadAsync(Request(changingSource), currentGeneration: null);
        await Task.Delay(75);
        File.SetLastWriteTimeUtc(changingSource, DateTime.UtcNow.AddMinutes(1));
        var outcome = await reload;

        Assert.Equal(ReloadErrorCode.ReloadSourceLockedOrIncomplete, outcome.Report.Error?.Code);
        Assert.Null(outcome.NewGeneration);
    }

    [Fact]
    public async Task BusyReloadReturnsStructuredFailure()
    {
        var source = await FixtureLogicBuilder.BuildAsync("ValidLogic");
        var manager = new ReloadManager(new TestHotHost());

        var first = manager.ReloadAsync(Request(source), currentGeneration: null);
        var second = await manager.ReloadAsync(Request(source), currentGeneration: null);
        var firstOutcome = await first;

        Assert.Equal(ReloadStatus.Loaded, firstOutcome.Report.Status);
        Assert.Equal(ReloadErrorCode.ReloadBusy, second.Report.Error?.Code);
    }

    private static ReloadRequest Request(string source)
    {
        return new ReloadRequest(
            SourceAssemblyPath: source,
            ShadowRoot: Path.Combine(Path.GetDirectoryName(source)!, ".shadow"),
            EntryTypeName: null,
            Reason: "test",
            VerifyUnload: true);
    }

    private sealed class TestHotHost : IHotHost
    {
        public DateTimeOffset Now => DateTimeOffset.Parse("2026-04-24T00:00:00Z");

        public void Log(HotLogLevel level, string message, IReadOnlyDictionary<string, string>? fields = null)
        {
        }
    }
}
