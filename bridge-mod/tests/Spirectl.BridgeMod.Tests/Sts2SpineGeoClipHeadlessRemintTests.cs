using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// THE ARM SWITCH for the headless mesh-pool re-mint. The lever itself is Godot-typed and lives in the baker; what
// is provable without a game is the RULE that decides whether it runs — which, since the product-path round below,
// is a function of the rendering backend rather than a constant.
//
// WHAT THE LEVER BUYS, measured, so the default is a choice and not an omission. Four rigs at `idle_loop`
// (ironclad, flail_knight, spectral_knight, magi_knight), one build, virgin process per arm, compared against a
// real-renderer bake of the SAME build on part identity (slotIndex, attachmentName):
//
//   arm                                parts (of 44/37/22/16)   byte-identical parts
//   headless, lever OFF                40 / 28 / 19 / 15        1 / 1 / 1 / 1
//   headless, lever ON                 42 / 37 / 22 / 16       42 / 37 / 22 / 16
//   headless, lever ON + mint mark     44 / 37 / 22 / 16       44 / 37 / 22 / 16
//
// WHY THE DEFAULT IS NOW DETECTED. Measured again through the SHIPPED `/geoclips/` route (not the env-armed baker
// that bypasses couch's admission gate), private cache root per arm, virgin process per arm:
//
//   arm                                                  route answer for the four rigs
//   headless, lever unset (the old default)              404 x4, `complete=false` on every one
//   headless, lever armed                                200 x4, complete=true, 44/37/22/16 parts
//   gamescope real renderer, lever unset                 200 x4, and the armed headless bakes are
//                                                        byte-identical to it on parts[], frames[0] and pages
//   gamescope real renderer, lever ARMED                 200 x4, byte-identical to the unarmed real arm
//
// So the lever is worth arming exactly where the backend is broken, and is inert where it is not. Left
// default-off nobody could benefit: a bake worker under `--headless` is precisely the caller that cannot know to
// set a variable. Evidence: `.sts2/research/data/geoclip-headless-route-*/` in the CouchCoop checkout.
public class Sts2SpineGeoClipHeadlessRemintTests
{
    [Fact]
    public void UnsetFollowsTheRenderer()
    {
        // THE DEFAULT CHANGE, stated as a test: nothing in the environment, and the answer is the backend's.
        Assert.False(Sts2SpineGeoClipHeadlessRemint.Resolve(null, dummyRenderer: false));
        Assert.False(Sts2SpineGeoClipHeadlessRemint.Resolve(string.Empty, dummyRenderer: false));
        Assert.False(Sts2SpineGeoClipHeadlessRemint.Resolve("   ", dummyRenderer: false));

        Assert.True(Sts2SpineGeoClipHeadlessRemint.Resolve(null, dummyRenderer: true));
        Assert.True(Sts2SpineGeoClipHeadlessRemint.Resolve(string.Empty, dummyRenderer: true));
        Assert.True(Sts2SpineGeoClipHeadlessRemint.Resolve("   ", dummyRenderer: true));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("True")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData(" 1 ")]
    public void RecognisedTruthsArmOnEitherBackend(string raw)
    {
        Assert.True(Sts2SpineGeoClipHeadlessRemint.Resolve(raw, dummyRenderer: false));
        Assert.True(Sts2SpineGeoClipHeadlessRemint.Resolve(raw, dummyRenderer: true));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("off")]
    [InlineData("no")]
    [InlineData("false")]
    // Anything unrecognised reads as OFF rather than as ON: a typo must not silently rebuild the mesh pool of
    // every bake on a machine that meant to leave the lever alone. On a dummy renderer that now means a typo
    // DISARMS a bake detection would have armed — still the conservative direction, still "what this code did
    // before", and the same reading as an explicit `=0`.
    [InlineData("maybe")]
    [InlineData("2")]
    public void EverythingElseIsOffAndTheOverrideCanForceItOffOnADummyRenderer(string raw)
    {
        Assert.False(Sts2SpineGeoClipHeadlessRemint.Resolve(raw, dummyRenderer: false));
        Assert.False(Sts2SpineGeoClipHeadlessRemint.Resolve(raw, dummyRenderer: true));
    }

    [Fact]
    public void TheDummyRendererIsRecognisedByEitherBackendName()
    {
        // The NARROW signal: the defect belongs to the rendering driver, whose two mesh-update entry points are
        // empty-bodied. It is NOT what fires under `--headless` — see the display-server case below — but an
        // explicit `--rendering-driver dummy` under a real display server can only be caught here.
        Assert.True(Sts2SpineGeoClipHeadlessRemint.IsDummyRenderer("dummy", "forward_plus", "x11"));
        Assert.True(Sts2SpineGeoClipHeadlessRemint.IsDummyRenderer("Dummy", string.Empty, string.Empty));
        Assert.True(Sts2SpineGeoClipHeadlessRemint.IsDummyRenderer("  dummy  ", null, null));

        // ...and the same word in the RENDERING METHOD, the second place Godot can spell it.
        Assert.True(Sts2SpineGeoClipHeadlessRemint.IsDummyRenderer(string.Empty, "dummy", "x11"));

        // The THIRD signal, the one this assembly already refuses raster stills on: a headless display server,
        // which on this engine selects the dummy rendering driver with it. THIS is the arm that actually fires,
        // and the measurement is the reason it has to exist: a `--headless` process on this build reports
        // renderingDriver='vulkan' renderingMethod='forward_plus' — the CONFIGURED backend, not the dummy one it
        // installed — so the two arms above match nothing there.
        Assert.True(Sts2SpineGeoClipHeadlessRemint.IsDummyRenderer(string.Empty, string.Empty, "headless"));
        Assert.True(Sts2SpineGeoClipHeadlessRemint.IsDummyRenderer(null, null, "Headless"));
    }

    [Fact]
    public void ASoftwareRasterizerIsNotADummyRenderer()
    {
        // lavapipe (Vulkan) and llvmpipe (OpenGL) under Xvfb bake byte-identically to the RTX 2060 with the lever
        // OFF — they are REAL rendering drivers with no GPU, and they report a real display server ('X11').
        // Matching them here would make every bake on that profile pay for a skeleton rebuild it does not need,
        // so the rule must not key on "no GPU" or "no window", only on the backend that cannot write a mesh in
        // place. This is the case the driver/method arms are NOT allowed to widen into.
        Assert.False(Sts2SpineGeoClipHeadlessRemint.IsDummyRenderer("vulkan", "forward_plus", "x11"));
        Assert.False(Sts2SpineGeoClipHeadlessRemint.IsDummyRenderer("opengl3", "gl_compatibility", "x11"));
        Assert.False(Sts2SpineGeoClipHeadlessRemint.IsDummyRenderer("vulkan", "mobile", "wayland"));
    }

    [Fact]
    public void AnEngineThatNamesNoBackendLeavesTheLeverWhereItWas()
    {
        // Both reads are guarded and can come back blank. That has to mean "not known to be dummy", so the
        // pre-detection behaviour (unarmed) stands and no bake changes because a diagnostic accessor threw.
        Assert.False(Sts2SpineGeoClipHeadlessRemint.IsDummyRenderer(null, null, null));
        Assert.False(Sts2SpineGeoClipHeadlessRemint.IsDummyRenderer(string.Empty, string.Empty, string.Empty));
        Assert.False(Sts2SpineGeoClipHeadlessRemint.IsDummyRenderer("   ", "   ", "   "));
        Assert.False(Sts2SpineGeoClipHeadlessRemint.Resolve(
            null, Sts2SpineGeoClipHeadlessRemint.IsDummyRenderer(null, null, null)));
    }

    [Fact]
    public void ArmedReadsTheDocumentedVariable()
    {
        // The name is the contract with the harness scripts and with every archived arm's `proc-environ.txt`;
        // renaming it silently would make an armed run read as a plain headless one. It is now an OVERRIDE rather
        // than the only way in, which is why `=0` has to keep working — see the theory above.
        Assert.Equal("SPIRECTL_SPINE_GEOCLIP_HEADLESS_REMINT", Sts2SpineGeoClipHeadlessRemint.ArmEnv);
        Assert.Equal("dummy", Sts2SpineGeoClipHeadlessRemint.DummyRenderingDriver);
        Assert.Equal("headless", Sts2SpineGeoClipHeadlessRemint.HeadlessDisplayServer);
        Assert.False(Sts2SpineGeoClipHeadlessRemint.DefaultFor(dummyRenderer: false));
        Assert.True(Sts2SpineGeoClipHeadlessRemint.DefaultFor(dummyRenderer: true));
    }

    [Fact]
    public void TheDecisionSaysWhichBackendItSaw()
    {
        // The log line is the ONLY way an operator can tell an auto-armed bake from an unarmed one, so it is a
        // contract, not decoration: it has to name the decision AND the two names the decision was made on.
        // The exact triple a `--headless` process reports on this build, measured from the live game.
        var dummy = Sts2SpineGeoClipHeadlessRemint.Decide(raw: null, "vulkan", "forward_plus", "headless");
        Assert.True(dummy.Armed);
        Assert.Contains("ARMED", dummy.Description, System.StringComparison.Ordinal);
        Assert.Contains("by detection", dummy.Description, System.StringComparison.Ordinal);
        Assert.Contains("renderingDriver='vulkan'", dummy.Description, System.StringComparison.Ordinal);
        Assert.Contains("displayServer='headless'", dummy.Description, System.StringComparison.Ordinal);
        Assert.Contains("dummy=1", dummy.Description, System.StringComparison.Ordinal);

        var real = Sts2SpineGeoClipHeadlessRemint.Decide(raw: null, "vulkan", "forward_plus", "x11");
        Assert.False(real.Armed);
        Assert.Contains("not armed", real.Description, System.StringComparison.Ordinal);
        Assert.Contains("dummy=0", real.Description, System.StringComparison.Ordinal);

        // A backend the engine declined to name is reported as blank rather than guessed at.
        var unknown = Sts2SpineGeoClipHeadlessRemint.Decide(raw: null, null, null, null);
        Assert.False(unknown.Armed);
        Assert.Contains(
            "renderingDriver='' renderingMethod='' displayServer='' dummy=0",
            unknown.Description,
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void TheMintMarkStillFollowsTheReMint()
    {
        // The mark is worthless without the re-mint and harmful nowhere with it, so it has exactly one arm: the
        // re-mint's. Auto-arming the re-mint therefore auto-arms the mark, which is the intended reading — the
        // ironclad's 42-of-44 residual is a headless-only defect and is repaired on the same backend.
        Assert.True(Sts2SpineGeoClipHeadlessMintMark.Resolve(null, remintArmed: true));
        Assert.False(Sts2SpineGeoClipHeadlessMintMark.Resolve(null, remintArmed: false));
        Assert.False(Sts2SpineGeoClipHeadlessMintMark.Resolve("0", remintArmed: true));
    }
}
