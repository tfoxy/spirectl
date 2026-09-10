using Spirectl.Sts2.Core.Models;

namespace Spirectl.Sts2.Live;

/// <summary>
/// Per-entry projection for the model catalog families.
/// </summary>
/// <remarks>
/// <para>
/// A family builder turns N game models into N snapshots. Doing that with a plain
/// <c>selected.Select(ToXSnapshot).ToArray()</c> under the provider's single outer try/catch means one
/// unprojectable entry takes the whole family down as <c>runtime_failure</c> — the caller asks for every
/// potion and gets nothing, with no way to tell which one was the problem.
/// </para>
/// <para>
/// Projecting entry by entry instead keeps the models that did project, records a
/// <c>model-projection-failed</c> notice naming the entry that did not, and lets the family come back
/// <see cref="ModelCatalogStatus.Partial"/>. The outer catch stays: an exception raised before or after
/// the per-entry loop is still a real failure of the whole request.
/// </para>
/// <para>
/// Godot-free on purpose so the behaviour is unit-testable without a live host.
/// </para>
/// </remarks>
public static class Sts2ModelCatalogProjection
{
    public const string ProjectionFailedCode = "model-projection-failed";

    /// <summary>Id used in a notice when even reading the entry's own id threw.</summary>
    public const string UnknownEntryId = "<unknown>";

    /// <summary>
    /// Project <paramref name="selected"/> one entry at a time, appending a notice to
    /// <paramref name="notices"/> for each entry whose projection threw.
    /// </summary>
    public static IReadOnlyList<GameModelSnapshot> ProjectSnapshots<TModel>(
        IEnumerable<TModel> selected,
        Func<TModel, string> id,
        Func<TModel, GameModelSnapshot> project,
        IList<ModelCatalogNoticeSnapshot> notices)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(notices);

        var models = new List<GameModelSnapshot>();
        foreach (var model in selected)
        {
            string entryId;
            try
            {
                entryId = id(model) ?? UnknownEntryId;
            }
            catch (Exception)
            {
                // A model whose own id accessor throws is exactly the kind of entry this exists for;
                // it must still not stop the family.
                entryId = UnknownEntryId;
            }

            try
            {
                models.Add(project(model));
            }
            catch (Exception ex)
            {
                notices.Add(ProjectionFailedNotice(entryId, ex));
            }
        }

        return models;
    }

    public static ModelCatalogNoticeSnapshot ProjectionFailedNotice(string entryId, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var id = string.IsNullOrWhiteSpace(entryId) ? UnknownEntryId : entryId;
        return new ModelCatalogNoticeSnapshot(
            ProjectionFailedCode,
            "warning",
            $"Failed to project model '{id}': {exception.Message}",
            $"models[{id}]");
    }
}
