using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Spirectl.Sts2.Live;

/// <summary>
/// The MINT MARK: how two slots that draw the SAME attachment are told apart when the renderer cannot answer a
/// colour probe. It rides on <see cref="Sts2SpineGeoClipHeadlessRemint"/> and exists for exactly one measured
/// residual of it.
///
/// <para>WHAT IS LEFT AFTER THE RE-MINT. Re-minting the mesh pool at the acquisition pose fixes the headless
/// bake's GEOMETRY, because a freshly added surface is one thing the dummy renderer stores faithfully. It does
/// not fix ASSOCIATION. When several slots draw one atlas region the atlas correctly refuses to guess between
/// them (it refuses on a real renderer too), and the tie is normally broken by the colour-flip probe: write a
/// distinguishing colour to one slot, wait a frame, see which mesh's vertex colours moved. Headless that probe is
/// inert — a colour write changes no triangle INDICES, so spine patches the existing surface's ATTRIBUTE region
/// in place, and the dummy backend discards an attribute-region update for exactly the reason it discards a
/// vertex-region one. The ironclad's two <c>hip armor right back</c> slots are the measured case: the probe spent
/// 224 colour reads and reported both slots "moved no mesh".</para>
///
/// <para>THE LEVER, ONE LEVEL UP. The re-mint already forces the CREATE path, and a created surface carries the
/// colour the slot held AT THAT MOMENT. So the distinguishing colour does not need a probe frame at all: write it
/// BEFORE the re-mint's pose recompute and the fresh surface is minted carrying it, permanently, because every
/// later frame is an update the dummy renderer drops. The slot colour is restored immediately afterwards, which
/// on a real renderer the very next draw applies (so the mark is invisible there and the arm is inert) and
/// headless is exactly the write that cannot land (so the mark survives to be read).</para>
///
/// <para>WHICH SLOTS. Only slots that SHARE an attachment name with another drawable slot at the acquisition
/// pose — the pose-derived shape of the tie, knowable before any mesh exists. On the four knight rigs that is
/// the ironclad's one pair and nothing else at all, so three of the four rigs are untouched by construction.</para>
///
/// <para>WHY IT CANNOT MIS-PAIR. The mark is a colour this code WROTE to a named ordinal, so the mesh carrying it
/// is that ordinal's by construction; the arm still runs the same mutual-best-with-margin rule
/// (<see cref="Sts2SpineGeoClipSlotColorIdentity"/>) over the tie's own candidates, so a mark that did not
/// survive — a slot the animation's colour timeline overwrites, an attachment or skeleton tint that scales the
/// channel, a surface whose vertices disagree — lands outside the tolerance and REFUSES, leaving exactly the
/// unassociated slot the bake had before. It fails to the previous behaviour, never past it.</para>
/// </summary>
internal static class Sts2SpineGeoClipHeadlessMintMark
{
    /// <summary>
    /// The kill switch. Unset means "follow the re-mint" — the mark is worthless without it and harmful nowhere
    /// with it, so an operator arms one lever, not two. <c>0</c>/<c>off</c>/<c>false</c>/<c>no</c> disarms it and
    /// restores the re-mint arm exactly as it was measured on 2026-09-09 (ironclad 42 of 44).
    /// </summary>
    internal const string ArmEnv = "SPIRECTL_SPINE_GEOCLIP_HEADLESS_MINT_MARK";

    /// <summary>
    /// The ladder step between two members of one tie group, on the RED channel. Two constraints fix it from
    /// below: <see cref="Sts2SpineGeoClipSlotColorIdentity.DefaultMargin"/> (0.05) is what the tie-break needs
    /// between the winner and the runner-up, and 8-bit vertex colour quantisation costs ~1/255. 0.125 clears the
    /// margin 2.5x and is a clean binary fraction, so the value a surface reads back is within a quantisation
    /// step of the one that was written.
    /// </summary>
    internal const double LadderStep = 0.125;

    /// <summary>
    /// The largest tie group the ladder can mark, because <c>LadderStep * (Member + 1)</c> must stay inside the
    /// unit interval. A bigger group is left entirely unmarked rather than partly marked: a group whose members
    /// cannot ALL be separated is one the tie-break must refuse anyway, and marking some of it would only make
    /// the refusal harder to read.
    /// </summary>
    internal const int MaxGroupSize = 7;

    /// <summary>One slot to mark: which ordinal, the attachment it shares, and the red channel it gets.</summary>
    internal readonly record struct Mark(int Ordinal, string AttachmentName, int Member, double Red);

    /// <summary>A group the ladder could not cover, named so the log can say what was left alone.</summary>
    internal readonly record struct Skipped(string AttachmentName, int Count);

    internal sealed record Plan(IReadOnlyList<Mark> Marks, IReadOnlyList<Skipped> Oversized)
    {
        /// <summary>How many distinct attachment names the marks span, for the log line.</summary>
        internal int GroupCount => Marks.Select(mark => mark.AttachmentName).Distinct(StringComparer.Ordinal).Count();

        internal string Describe()
            => string.Join(
                " ",
                Marks.Select(mark => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{mark.Ordinal}='{mark.AttachmentName}'@r={mark.Red:0.###}")));
    }

    /// <summary>The switch's resolved value. Blank or unset follows <paramref name="remintArmed"/>.</summary>
    internal static bool Resolve(string? raw, bool remintArmed)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return remintArmed;
        }

        // Recognised falsehoods disarm; everything else, typos included, follows the re-mint. This is the
        // OPPOSITE default to the re-mint's own parse, and deliberately: an unrecognised value there could
        // silently rebuild a mesh pool nobody asked to rebuild, while here it can only leave a mark on a slot
        // whose bake is already being rebuilt, and the mark refuses rather than guesses.
        return value.ToLowerInvariant() is not ("0" or "off" or "false" or "no") && remintArmed;
    }

    /// <summary>The switch as a bake starting right now would read it.</summary>
    internal static bool Armed(bool remintArmed)
        => Resolve(Environment.GetEnvironmentVariable(ArmEnv), remintArmed);

    /// <summary>The red channel the <paramref name="member"/>-th slot of a tie group is marked with.</summary>
    internal static double RedFor(int member) => LadderStep * (member + 1);

    /// <summary>
    /// Which slots to mark, from the acquisition pose alone. A slot qualifies when it requires drawing, names an
    /// attachment, and at least one OTHER qualifying slot names the same attachment. Members are numbered in
    /// ordinal order so the plan is a function of the pose and nothing else — two processes baking the same rig
    /// mark the same ordinals with the same colours.
    /// </summary>
    internal static Plan For(IEnumerable<(int Ordinal, string? AttachmentName, bool RequiresDrawing)> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);

        var marks = new List<Mark>();
        var oversized = new List<Skipped>();
        var groups = slots
            .Where(slot => slot.RequiresDrawing && !string.IsNullOrEmpty(slot.AttachmentName))
            .GroupBy(slot => slot.AttachmentName!, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var ordinals = group.Select(slot => slot.Ordinal).Distinct().Order().ToArray();
            if (ordinals.Length < 2)
            {
                continue;
            }

            if (ordinals.Length > MaxGroupSize)
            {
                oversized.Add(new Skipped(group.Key, ordinals.Length));
                continue;
            }

            for (var member = 0; member < ordinals.Length; member += 1)
            {
                marks.Add(new Mark(ordinals[member], group.Key, member, RedFor(member)));
            }
        }

        return new Plan([.. marks.OrderBy(mark => mark.Ordinal)], oversized);
    }
}
