using Spirectl.Sts2.Core.Models;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// Per-entry model-catalog projection. The rule under test is the one a whole-family try/catch got wrong:
// a single unprojectable entry must cost exactly that entry, never the family.
public sealed class Sts2ModelCatalogProjectionTests
{
    private sealed record FakeModel(string Id, bool Throws = false, bool IdThrows = false);

    private sealed record FakeSnapshot(string Id) : GameModelSnapshot("fake", Id);

    private static string ReadId(FakeModel model)
        => model.IdThrows ? throw new InvalidOperationException("id accessor blew up") : model.Id;

    private static GameModelSnapshot Project(FakeModel model)
        => model.Throws
            ? throw new InvalidOperationException($"cannot project {model.Id}")
            : new FakeSnapshot(model.Id);

    [Fact]
    public void ProjectsEveryEntryWhenNothingThrows()
    {
        List<ModelCatalogNoticeSnapshot> notices = [];

        var models = Sts2ModelCatalogProjection.ProjectSnapshots(
            [new FakeModel("a"), new FakeModel("b")],
            ReadId,
            Project,
            notices);

        Assert.Equal(["a", "b"], models.Select(model => model.Id));
        Assert.Empty(notices);
    }

    [Fact]
    public void KeepsTheEntriesAroundAFailingOne()
    {
        List<ModelCatalogNoticeSnapshot> notices = [];

        var models = Sts2ModelCatalogProjection.ProjectSnapshots(
            [new FakeModel("a"), new FakeModel("bad", Throws: true), new FakeModel("c")],
            ReadId,
            Project,
            notices);

        Assert.Equal(["a", "c"], models.Select(model => model.Id));
        var notice = Assert.Single(notices);
        Assert.Equal("model-projection-failed", notice.Code);
        Assert.Equal("warning", notice.Severity);
        Assert.Equal("models[bad]", notice.Path);
        Assert.Contains("bad", notice.Message, StringComparison.Ordinal);
        Assert.Contains("cannot project bad", notice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordsOneNoticePerFailingEntry()
    {
        List<ModelCatalogNoticeSnapshot> notices = [];

        var models = Sts2ModelCatalogProjection.ProjectSnapshots(
            [new FakeModel("x", Throws: true), new FakeModel("y", Throws: true)],
            ReadId,
            Project,
            notices);

        Assert.Empty(models);
        Assert.Equal(["models[x]", "models[y]"], notices.Select(notice => notice.Path));
    }

    [Fact]
    public void SurvivesAnEntryWhoseOwnIdAccessorThrows()
    {
        List<ModelCatalogNoticeSnapshot> notices = [];

        var models = Sts2ModelCatalogProjection.ProjectSnapshots(
            [new FakeModel("boom", Throws: true, IdThrows: true), new FakeModel("ok")],
            ReadId,
            Project,
            notices);

        Assert.Equal(["ok"], models.Select(model => model.Id));
        var notice = Assert.Single(notices);
        Assert.Equal($"models[{Sts2ModelCatalogProjection.UnknownEntryId}]", notice.Path);
    }

    [Fact]
    public void AppendsToNoticesAlreadyCollected()
    {
        List<ModelCatalogNoticeSnapshot> notices =
        [
            new ModelCatalogNoticeSnapshot("model-id-not-found", "warning", "missing", "ids"),
        ];

        Sts2ModelCatalogProjection.ProjectSnapshots(
            [new FakeModel("bad", Throws: true)],
            ReadId,
            Project,
            notices);

        Assert.Equal(["model-id-not-found", "model-projection-failed"], notices.Select(notice => notice.Code));
    }
}
