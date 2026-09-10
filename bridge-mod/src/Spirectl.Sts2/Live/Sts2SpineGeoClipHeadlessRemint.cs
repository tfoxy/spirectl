using System;

namespace Spirectl.Sts2.Live;

/// <summary>
/// ARM SWITCH for the geoclip baker's MESH-POOL RE-MINT, an acquisition-time lever that exists for exactly one
/// measured problem: under Godot's <c>--headless</c> dummy renderer a geoclip bake reads geometry back from the
/// pose each slot mesh was CREATED at, never the pose the bake sampled.
///
/// <para>WHY THAT HAPPENS. The spine runtime hands a slot's deformed triangles to the rendering server on the
/// DRAW path, and it has two of them. The first time a slot's mesh is drawn — or any time its triangle INDICES
/// differ from the ones the mesh instance last cached — it builds a whole new surface and ADDS it. Every other
/// frame it patches the existing surface's vertex region in place. The dummy renderer stores an added surface
/// faithfully (which is why <c>MeshSurfaceGetArrays</c> can read anything back at all headless) and silently
/// discards an in-place vertex-region update. For a steady idle animation the indices never change, so a slot's
/// mesh keeps its creation-time contents for the whole process — a valid pose of the same skeleton at the wrong
/// instant, which is exactly the recorded headless defect.</para>
///
/// <para>WHAT THE LEVER DOES. Immediately before the acquisition bracket, it re-sets the sprite's own skeleton
/// data resource. That frees every slot mesh and mints a fresh pool, so the next draw is a CREATE for every slot
/// at once — at the acquisition pose, if (and only if) the pose is re-applied before that draw can run. The
/// baker owns that ordering; this type only decides whether it is armed.</para>
///
/// <para>SCOPE, AND IT IS ENFORCED, NOT JUST DOCUMENTED. It corrects the mesh pool at the ACQUISITION pose only.
/// A single-pose bake samples geometry exactly there, so the whole artifact is corrected; a whole-clip bake still
/// reads every later frame off a mesh nothing re-created. Left unenforced that is a REGRESSION rather than a
/// no-op, because arming the re-mint is what makes such a bake pass the completeness gate: measured on this
/// build, an unarmed whole-clip headless bake refuses on all 8 clips (<c>complete=false</c>, so a 404 and nothing
/// adopted) while an ARMED one is <c>complete=true</c> on 7 of 8, is ADOPTED, gets a <c>.complete</c> marker and
/// is SERVED — carrying zero of the real renderer's 34 624 per-frame vertex scalars and reference geometry up to
/// 3 966 px out. So <see cref="WholeClipRefusal"/> refuses that combination outright; see it for where.</para>
///
/// <para>WHY THE DEFAULT IS NOW THE RENDERER, NOT OFF. Left off by default, nobody benefits: a bake worker under
/// <c>--headless</c> is exactly the caller that cannot know to set a variable, and the shipped <c>/geoclips/</c>
/// route answered 404 for all four knight rigs there. So the default is DETECTED — the lever arms itself when,
/// and only when, this process is running the very rendering backend whose two mesh-update entry points are
/// empty-bodied. That keeps the change scoped to the broken configuration by construction rather than by an
/// operator remembering. The environment variable remains, and <c>=0</c> forces it off on either backend.</para>
///
/// <para>THE DETECTION IS THREE SIGNALS, OR-ed, and the arm is safe even when they over-fire — see
/// <see cref="IsDummyRenderer"/>. The one that actually fires is the DISPLAY SERVER being <c>headless</c>, and
/// that is a MEASURED fact rather than the obvious one: under <c>--headless</c> on this build the engine still
/// reports <c>renderingDriver='vulkan' renderingMethod='forward_plus'</c> — the CONFIGURED backend, not the dummy
/// one it actually installed — so a detector keyed on the driver name alone would have matched nothing and
/// changed nothing. The two driver-name arms are kept for the configurations that do spell it out
/// (<c>--rendering-driver dummy</c> under a real display server), and they are what keeps a lavapipe/llvmpipe
/// software rasterizer under Xvfb OUT: that profile bakes correctly unarmed, reports a real display server, and
/// has no reason to pay for a skeleton rebuild. A false POSITIVE costs nothing measurable — a lever-on bake on a
/// real renderer came out byte-identical to a lever-off one through the shipped route, four rigs, twice — while
/// a false NEGATIVE is the 404 this change exists to remove.</para>
/// </summary>
internal static class Sts2SpineGeoClipHeadlessRemint
{
    /// <summary>The environment switch. Unset or blank means <see cref="DefaultFor"/>.</summary>
    internal const string ArmEnv = "SPIRECTL_SPINE_GEOCLIP_HEADLESS_REMINT";

    /// <summary>Godot's no-op rendering backend, the one configuration this lever repairs.</summary>
    internal const string DummyRenderingDriver = "dummy";

    /// <summary>Godot's windowless display server, which on this engine implies <see cref="DummyRenderingDriver"/>.</summary>
    internal const string HeadlessDisplayServer = "headless";

    /// <summary>
    /// Whether a process reporting these three backend names is one whose slot meshes can only be written by the
    /// CREATE path — a pure function so the rule is testable without a game.
    /// </summary>
    /// <remarks>
    /// <para>ANY of the three is sufficient; see the type remarks for why OR rather than AND. All are compared
    /// case-insensitively and tolerate null/blank, because an engine that declines to answer must read as "not
    /// known to be dummy" and leave the previous behaviour in place.</para>
    /// <para>THE FIRST TWO ARE THE NARROW SIGNALS, and they are NOT the ones that fire under <c>--headless</c>.
    /// Measured on this build: a <c>--headless</c> process reports <c>renderingDriver='vulkan'</c> and
    /// <c>renderingMethod='forward_plus'</c>, i.e. the backend it was CONFIGURED for rather than the dummy one it
    /// installed, so neither name says <c>dummy</c> and a detector keyed on them alone would be inert. They are
    /// here for the configuration that does spell it out — an explicit <c>--rendering-driver dummy</c> under a
    /// real display server, which the display-server arm below cannot see — and cost one string compare each.</para>
    /// <para>THE DISPLAY-SERVER ARM is what actually decides, and it is the same signal this assembly already
    /// refuses raster stills on. It is a coarser question than "is the driver dummy", and it is safe in this
    /// direction only because a false positive is measured inert: a lever-on bake on a REAL renderer came out
    /// byte-identical to a lever-off one through the shipped route, four rigs, twice. It is also why a software
    /// rasterizer is not matched — lavapipe under Xvfb reports <c>displayServer='X11'</c>, so it keeps the
    /// unarmed path it already bakes correctly on.</para>
    /// </remarks>
    internal static bool IsDummyRenderer(
        string? renderingDriverName,
        string? renderingMethodName,
        string? displayServerName)
        => Names(renderingDriverName, DummyRenderingDriver)
            || Names(renderingMethodName, DummyRenderingDriver)
            || Names(displayServerName, HeadlessDisplayServer);

    private static bool Names(string? value, string expected)
        => string.Equals((value ?? string.Empty).Trim(), expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What the lever does when the environment says nothing: follow the renderer. This is the whole default
    /// change — <c>false</c> on any backend that can write a mesh in place, <c>true</c> on the one that cannot.
    /// </summary>
    internal static bool DefaultFor(bool dummyRenderer) => dummyRenderer;

    /// <summary>
    /// THE OTHER HALF OF THE DEFAULT CHANGE: why a WHOLE-CLIP bake must be refused on the backend this lever
    /// arms itself on. Null means the frame plan is safe here; a string is the reason to refuse, and is written
    /// to be readable by whoever gets the 404.
    /// </summary>
    /// <remarks>
    /// <para>WHY REFUSING IS NOT THE SAME COST AS DOING NOTHING, which is the whole reason this exists. Arming
    /// the re-mint fixes ACQUISITION — the sweep now finds every slot mesh, so <c>complete=true</c>,
    /// <c>unassociated=0</c>, <c>foreignMeshes=0</c> — and BOTH admission rules
    /// (<c>Sts2SpineGeoClipRequestLane.IncompletenessReason</c> and couch's independent copy) are frame-blind:
    /// they grade acquisition and ownership, and never look at whether a per-frame vertex track exists. So on the
    /// dummy backend arming turns a whole-clip bake from a SAFE REFUSAL into an ADMITTED artifact whose frames
    /// are all the acquisition pose. Measured, four knight rigs, `--headless`, this build: unarmed refuses 8 of
    /// 8 clips; armed publishes 7 of 8, with ZERO per-frame vertex scalars against the real renderer's 180 208
    /// over the same 221 frames, per-frame slot transforms up to 5 783 px out, and reference geometry itself up
    /// to 3 966 px out (<c>refVerts</c> is sampled at <c>refFrame</c>, which need not be the acquisition
    /// frame — so not even the corrected pose survives into the artifact intact). Two
    /// headless processes produce that same wrong artifact byte-for-byte, so nothing downstream can tell it from
    /// a good bake.</para>
    /// <para>IT IS NOT ENOUGH THAT THE PRODUCT ROUTE ASKS FOR ONE FRAME TODAY. Couch's geoclip store key does
    /// NOT carry the frame count: a <c>frames=16</c> bake and a <c>frames=1</c> bake of the same rig land in the
    /// SAME content-addressed directory. Measured: seed a store with the whole-clip headless artifact and the
    /// ordinary single-pose route request serves it verbatim in 2 ms without baking. One caller asking for a
    /// clip on a headless host is therefore enough to poison the single-pose lane too, which is why this refuses
    /// at the producer rather than relying on every caller to keep asking for one frame.</para>
    /// <para>THE SINGLE-POSE PATH IS UNTOUCHED BY CONSTRUCTION — <paramref name="poseOnly"/> true returns null
    /// before anything else is considered, so the verified byte-identical pose bake cannot regress through here.
    /// So is every bake on a real renderer, software rasterizers included: they write meshes in place and their
    /// whole-clip bakes are already byte-identical to the GPU's.</para>
    /// </remarks>
    /// <param name="frameCap">The frame plan being refused, for the message only.</param>
    internal static string? WholeClipRefusal(bool dummyRenderer, bool poseOnly, int frameCap)
        => poseOnly || !dummyRenderer
            ? null
            : $"a WHOLE-CLIP geoclip bake ({frameCap} frame cap) cannot be produced on this rendering backend: "
                + "its mesh-update entry points are no-ops, so a slot mesh only ever holds the pose it was "
                + "CREATED at. The headless mesh-pool re-mint corrects the ACQUISITION pose and nothing else, so "
                + "every other frame would read back that same pose while still reporting complete — an "
                + "artifact no admission rule can tell from a correct one. Ask for a single pose "
                + "(pose=1 / maxFrames=1), or bake on a renderer that can write a mesh in place: any real "
                + "display server, a lavapipe/llvmpipe software rasterizer included.";

    /// <summary>
    /// The switch's resolved value, so the baker's arm site and any future refusal-memo signature read one
    /// spelling of "nothing said otherwise" rather than each parsing the variable their own way.
    /// </summary>
    /// <remarks>
    /// The env vocabulary is unchanged from when this shipped default-off, including the deliberate asymmetry
    /// that an UNRECOGNISED value reads as OFF: a typo must not rebuild the mesh pool of every bake on a machine
    /// that meant to leave the lever alone. Note what that now means on a dummy renderer — <c>=maybe</c> disarms
    /// a bake that would otherwise have been armed by detection, i.e. it is still the conservative direction,
    /// still "do what this code did before", and it is the same reading as the explicit <c>=0</c>.
    /// </remarks>
    internal static bool Resolve(string? raw, bool dummyRenderer)
    {
        var value = (raw ?? string.Empty).Trim();
        return value.Length == 0
            ? DefaultFor(dummyRenderer)
            : value is "1" or "true" or "TRUE" or "True" or "yes" or "on";
    }

    /// <summary>
    /// The lever's decision for a bake starting right now, together with the one line that says WHY.
    /// </summary>
    /// <remarks>
    /// <para>ONE evaluation answering both questions, so the log can never describe a different decision than the
    /// one the baker took. A lever whose default is DETECTED has to say what it detected, or the next person
    /// debugging a bake cannot tell an armed run from an unarmed one without re-deriving the backend themselves —
    /// precisely the archaeology this project keeps paying for. The baker emits it on BOTH paths, unlike the
    /// previous default-off lever which logged only when armed.</para>
    /// <para>THE BACKEND NAMES ARE PASSED IN, not read here, because this file is compiled into the GODOT-FREE
    /// build too (it is the arm switch, and the refusal memo's identity fold may come to carry it). The baker
    /// reads <c>RenderingServer.GetCurrentRenderingDriverName()</c>, <c>RenderingServer.GetCurrentRenderingMethod()</c>
    /// and <c>DisplayServer.GetName()</c>, each guarded, and a name it could not obtain arrives here blank — which <see cref="IsDummyRenderer"/> reads as "not known to
    /// be dummy", leaving the pre-detection behaviour in place.</para>
    /// </remarks>
    internal static (bool Armed, string Description) Decide(
        string? renderingDriverName,
        string? renderingMethodName,
        string? displayServerName)
        => Decide(
            Environment.GetEnvironmentVariable(ArmEnv),
            renderingDriverName,
            renderingMethodName,
            displayServerName);

    /// <summary>
    /// The same decision as a pure function of its four inputs, so the rule and its wording stay testable
    /// without a process-global variable a parallel suite could be mutating — the same discipline
    /// <c>CouchCoopGeoclipProvider.IncompletenessReason</c> takes with its ownership arm.
    /// </summary>
    internal static (bool Armed, string Description) Decide(
        string? raw,
        string? renderingDriverName,
        string? renderingMethodName,
        string? displayServerName)
    {
        var dummy = IsDummyRenderer(renderingDriverName, renderingMethodName, displayServerName);
        var armed = Resolve(raw, dummy);
        var backend = $"renderingDriver='{renderingDriverName ?? string.Empty}' "
            + $"renderingMethod='{renderingMethodName ?? string.Empty}' "
            + $"displayServer='{displayServerName ?? string.Empty}' dummy={(dummy ? 1 : 0)}";
        var source = string.IsNullOrWhiteSpace(raw)
            ? $"by detection ({backend})"
            : $"by {ArmEnv}='{raw!.Trim()}', overriding detection ({backend})";
        return (armed, $"headless mesh-pool re-mint {(armed ? "ARMED" : "not armed")} {source}");
    }
}
