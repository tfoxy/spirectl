using System.Collections;
using MegaCrit.Sts2.Core.HoverTips;
using Spirectl.Sts2.Core.Models;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live;

/// <summary>
/// Shared projection of a game hover-tip stack (<c>IEnumerable&lt;IHoverTip&gt;</c> —
/// EventOption.HoverTips, PowerModel.HoverTips, …) into the resolved snapshot shape the
/// potion/relic model tips established: final title/description strings (dynamic vars
/// already substituted by the game), the debuff flag, and the power-icon asset key.
/// The getters format LocStrings and may touch live run state, so any failure degrades
/// to an empty stack (no tips is faithful for surfaces the game gives none).
/// </summary>
internal static class Sts2HoverTipProjection
{
    internal static IReadOnlyList<ModelHoverTipSnapshot> ExtractResolvedTips(object? hoverTips)
    {
        try
        {
            if (hoverTips is not IEnumerable enumerable)
            {
                return Array.Empty<ModelHoverTipSnapshot>();
            }

            var tips = new List<ModelHoverTipSnapshot>();
            foreach (var raw in enumerable)
            {
                if (raw is not HoverTip tip)
                {
                    continue;
                }

                var title = tip.Title;
                var description = tip.Description;
                if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(description))
                {
                    continue;
                }

                tips.Add(new ModelHoverTipSnapshot(
                    Title: title,
                    Description: description,
                    IsDebuff: tip.IsDebuff,
                    IconAssetKey: Sts2ModelCatalogProvider.TipIconAssetKey(tip)));
            }

            return tips;
        }
        catch
        {
            return Array.Empty<ModelHoverTipSnapshot>();
        }
    }

    /// <summary>
    /// The card a hover-tip stack previews, if any: the FIRST <see cref="CardHoverTip"/> in the
    /// stack (e.g. an event option that adds a card; a power like SwipePower's stolen card, or
    /// PainfulStabs-&gt;Wound / SwordSage-&gt;SovereignBlade). <see cref="ExtractResolvedTips"/>
    /// keeps only plain <c>HoverTip</c> structs and drops <c>CardHoverTip</c>, so the previewed
    /// card is surfaced here as a full card snapshot for the focus card-preview. Null when the
    /// stack contains no card tip (the common case — no preview).
    /// </summary>
    internal static StateCardSnapshot? TryResolvePreviewedCard(object? hoverTips, string id)
    {
        try
        {
            if (hoverTips is not IEnumerable enumerable)
            {
                return null;
            }

            foreach (var raw in enumerable)
            {
                if (raw is CardHoverTip cardTip && cardTip.Card is { } card)
                {
                    return Sts2CardStateSnapshotFactory.Create(card, id);
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
