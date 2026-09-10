using System.Globalization;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// P5-WS-DC: baking a RIG's whole pose set in ONE scene load, and getting a rig's atlas pages out without
// decoding and re-encoding them.
//
// Everything decidable without the game lives here on purpose, because the two things this round can get
// SILENTLY wrong both are: copying the wrong file as a page (a creature drawn with someone else's atlas,
// content-addressed and cached for ever) and sharing an association across poses it does not describe (one
// slot's art baked onto another). Both are guarded by pure predicates, so both are provable offline.
public sealed class Sts2SpineGeoClipRigBakeTests
{
    // ── The source-PNG passthrough ───────────────────────────────────────────────────────────────────

    // The header reader is 24 bytes of parsing and the first gate the whole passthrough rests on: if it
    // reports a size for something that is not a PNG, every later check is comparing invented numbers.
    [Fact]
    public void ReadPngSize_ReadsTheIhdrAndRefusesAnythingElse()
    {
        var png = Png(2048, 1024);

        var size = Sts2SpineGeoClipPageSource.ReadPngSize(png);

        Assert.NotNull(size);
        Assert.Equal(2048, size!.Value.Width);
        Assert.Equal(1024, size.Value.Height);

        Assert.Null(Sts2SpineGeoClipPageSource.ReadPngSize(null));
        Assert.Null(Sts2SpineGeoClipPageSource.ReadPngSize([]));
        Assert.Null(Sts2SpineGeoClipPageSource.ReadPngSize(png[..20]));

        // A JPEG that happens to be 24 bytes long must not read as a 0x0 (or any) PNG.
        var notPng = (byte[])png.Clone();
        notPng[1] = (byte)'J';
        Assert.Null(Sts2SpineGeoClipPageSource.ReadPngSize(notPng));

        // Signature right, first chunk wrong — a PNG whose IHDR is not where it must be is not readable here.
        var wrongChunk = (byte[])png.Clone();
        wrongChunk[12] = (byte)'X';
        Assert.Null(Sts2SpineGeoClipPageSource.ReadPngSize(wrongChunk));

        Assert.Null(Sts2SpineGeoClipPageSource.ReadPngSize(Png(0, 512)));
    }

    [Fact]
    public void Decide_TakesThePassthroughWhenEveryDescriptionOfThePageAgrees()
    {
        var decision = Sts2SpineGeoClipPageSource.Decide(
            Sts2SpineGeoClipPageSource.Mode.Strict,
            "res://art/spine/byrdonis.png",
            Png(2048, 2048),
            textureWidth: 2048,
            textureHeight: 2048,
            atlasPageName: "byrdonis.png",
            atlasWidth: 2048,
            atlasHeight: 2048,
            importSidecar: Sidecar);

        Assert.True(decision.Accept);
        Assert.Equal("ok", decision.Reason);
    }

    // THE ONE THAT MATTERS. A page whose bytes do not describe the page the manifest references is a creature
    // drawn with another creature's atlas — so a disagreement with EITHER witness (the runtime texture, the
    // atlas text) refuses, and the reason names which.
    [Fact]
    public void Decide_RefusesWhenTheSourceDoesNotDescribeThePageTheManifestReferences()
    {
        Assert.Equal(
            "texture-size-mismatch:2048x2048-vs-1024x2048",
            Decide(Png(2048, 2048), textureWidth: 1024).Reason);
        Assert.Equal(
            "texture-size-mismatch:2048x2048-vs-2048x1024",
            Decide(Png(2048, 2048), textureHeight: 1024).Reason);
        Assert.Equal(
            "atlas-size-mismatch:2048x2048-vs-4096x2048",
            Decide(Png(2048, 2048), atlasWidth: 4096).Reason);
        Assert.Equal(
            "page-name-mismatch:byrdonis.png-vs-merchant.png",
            Decide(Png(2048, 2048), atlasPageName: "merchant.png").Reason);

        // An atlas that declares no size for the page is not evidence AGAINST it — the texture still is.
        Assert.True(Decide(Png(2048, 2048), atlasWidth: 0, atlasHeight: 0).Accept);

        // A runtime texture that reports nothing cannot be agreed with, so there is nothing to agree.
        Assert.Equal("texture-size-unknown", Decide(Png(2048, 2048), textureWidth: 0).Reason);
    }

    [Fact]
    public void Decide_RefusesASourceThatIsNotAReadablePngAtAll()
    {
        Assert.Equal("no-source-path", Decide(Png(2048, 2048), sourcePath: "  ").Reason);
        Assert.Equal("source-not-png", Decide(Png(2048, 2048), sourcePath: "res://art/spine/byrdonis.webp").Reason);
        Assert.Equal("source-unreadable", Decide([]).Reason);
        Assert.Equal("source-not-a-png-file", Decide(new byte[64]).Reason);
        Assert.Equal(
            "disabled",
            Sts2SpineGeoClipPageSource.Decide(
                Sts2SpineGeoClipPageSource.Mode.Disabled,
                "res://art/spine/byrdonis.png",
                Png(2048, 2048),
                2048,
                2048,
                "byrdonis.png",
                2048,
                2048,
                Sidecar).Reason);
    }

    // LOOSE exists so a build whose atlas text names pages differently can still take the win without a rebuild.
    // It drops the NAME and the sidecar clauses and keeps every size clause — dropping those would be dropping
    // the whole guard.
    [Fact]
    public void LooseKeepsEverySizeCheckAndDropsOnlyTheNameAndSidecarOnes()
    {
        Assert.True(Decide(Png(2048, 2048), atlasPageName: "merchant.png", mode: Sts2SpineGeoClipPageSource.Mode.Loose).Accept);
        Assert.True(Decide(Png(2048, 2048), sidecar: string.Empty, mode: Sts2SpineGeoClipPageSource.Mode.Loose).Accept);
        Assert.True(Decide(Png(2048, 2048), sidecar: PremultSidecar, mode: Sts2SpineGeoClipPageSource.Mode.Loose).Accept);

        Assert.Equal(
            "texture-size-mismatch:2048x2048-vs-1024x2048",
            Decide(Png(2048, 2048), textureWidth: 1024, mode: Sts2SpineGeoClipPageSource.Mode.Loose).Reason);
        Assert.Equal(
            "atlas-size-mismatch:2048x2048-vs-2048x4096",
            Decide(Png(2048, 2048), atlasHeight: 4096, mode: Sts2SpineGeoClipPageSource.Mode.Loose).Reason);
    }

    // The sidecar clause exists for ONE setting. `fix_alpha_border` defaults ON in Godot and only rewrites RGB
    // under fully transparent pixels; refusing it would refuse every page and buy nothing.
    [Fact]
    public void OnlyAnImporterThatPremultipliesAlphaBlocksTheCopy()
    {
        Assert.Null(Sts2SpineGeoClipPageSource.ImportAltersPixels(Sidecar));
        Assert.Null(Sts2SpineGeoClipPageSource.ImportAltersPixels(null));
        Assert.Null(Sts2SpineGeoClipPageSource.ImportAltersPixels("process/premult_alpha=false"));
        Assert.Equal("import-premultiplies-alpha", Sts2SpineGeoClipPageSource.ImportAltersPixels(PremultSidecar));
        Assert.Equal("import-premultiplies-alpha", Sts2SpineGeoClipPageSource.ImportAltersPixels("process/premult_alpha=1"));

        Assert.Equal("import-premultiplies-alpha", Decide(Png(2048, 2048), sidecar: PremultSidecar).Reason);
        Assert.Equal("no-import-sidecar", Decide(Png(2048, 2048), sidecar: string.Empty).Reason);
    }

    [Fact]
    public void ParseMode_KnowsExactlyThreeAnswers()
    {
        Assert.Equal(Sts2SpineGeoClipPageSource.Mode.Strict, Sts2SpineGeoClipPageSource.ParseMode(null));
        Assert.Equal(Sts2SpineGeoClipPageSource.Mode.Strict, Sts2SpineGeoClipPageSource.ParseMode("1"));
        Assert.Equal(Sts2SpineGeoClipPageSource.Mode.Loose, Sts2SpineGeoClipPageSource.ParseMode("LOOSE"));
        Assert.Equal(Sts2SpineGeoClipPageSource.Mode.Disabled, Sts2SpineGeoClipPageSource.ParseMode("0"));
        Assert.Equal(Sts2SpineGeoClipPageSource.Mode.Disabled, Sts2SpineGeoClipPageSource.ParseMode("off"));
        Assert.Equal(Sts2SpineGeoClipPageSource.Mode.Disabled, Sts2SpineGeoClipPageSource.ParseMode("false"));
    }

    [Fact]
    public void FileName_TakesTheLastGodotPathSegment()
    {
        Assert.Equal("byrdonis.png", Sts2SpineGeoClipPageSource.FileName("res://art/spine/byrdonis.png"));
        Assert.Equal("byrdonis.png", Sts2SpineGeoClipPageSource.FileName("byrdonis.png"));
        Assert.Equal(string.Empty, Sts2SpineGeoClipPageSource.FileName(null));
    }

    // The SECOND candidate source, and often the only one: a page texture's own resource path may be a
    // sub-resource of the imported atlas rather than a file, but the atlas names its pages and the packer writes
    // them beside it.
    [Fact]
    public void SiblingPath_ResolvesTheAtlasDeclaredPageNameNextToTheAtlas()
    {
        Assert.Equal(
            "res://art/spine/byrdonis.png",
            Sts2SpineGeoClipPageSource.SiblingPath("res://art/spine/byrdonis.atlas", "byrdonis.png"));
        Assert.Equal(
            "res://art/spine/merchant2.png",
            Sts2SpineGeoClipPageSource.SiblingPath("res://art/spine/merchant.atlas", " merchant2.png "));

        Assert.Equal(string.Empty, Sts2SpineGeoClipPageSource.SiblingPath(null, "byrdonis.png"));
        Assert.Equal(string.Empty, Sts2SpineGeoClipPageSource.SiblingPath("res://a/b.atlas", null));

        // A page NAME carrying a separator is not a name; resolving it would let the atlas text address any file
        // in the project, and the whole guard here is that a page comes from one predictable place.
        Assert.Equal(string.Empty, Sts2SpineGeoClipPageSource.SiblingPath("res://a/b.atlas", "../../secrets.png"));
        Assert.Equal(string.Empty, Sts2SpineGeoClipPageSource.SiblingPath("res://a/b.atlas", @"sub\page.png"));
    }

    // ── The IMPORTED-TEXTURE passthrough (`.ctex`) ───────────────────────────────────────────────────
    //
    // The source-PNG passthrough above declined 401 times out of 401 on a shipped build, and structurally so:
    // a Godot EXPORT strips source images (the shipped pck holds ONE .png in 12 327 entries, the app icon).
    // What it ships is the imported `.ctex` the `.import` sidecar names, whose payload is already a lossless
    // WebP. Everything here is the guard on reading that container, because the failure mode is unchanged:
    // publishing the wrong bytes as a content-addressed page caches a creature drawn with someone else's atlas.

    [Fact]
    public void CtexParseMode_IsArmedUnlessItIsSpeltOff()
    {
        // Default ARMED as of the phase-6 WS-5 gate. An unset environment variable is the shipped path, so it is
        // the one asserted first; `nonsense` is here to pin that an unrecognised value falls through to the
        // default rather than to the kill switch, which is the polarity Sts2SpineGeoClipPageSource already uses.
        Assert.Equal(Sts2SpineGeoClipCtexSource.Mode.Webp, Sts2SpineGeoClipCtexSource.ParseMode(null));
        Assert.Equal(Sts2SpineGeoClipCtexSource.Mode.Webp, Sts2SpineGeoClipCtexSource.ParseMode(string.Empty));
        Assert.Equal(Sts2SpineGeoClipCtexSource.Mode.Webp, Sts2SpineGeoClipCtexSource.ParseMode("nonsense"));

        // The kill switch has to keep working, and has to keep working through the same spellings the other
        // page-source switch accepts, or an operator turning one off will believe they turned both off.
        Assert.Equal(Sts2SpineGeoClipCtexSource.Mode.Disabled, Sts2SpineGeoClipCtexSource.ParseMode("0"));
        Assert.Equal(Sts2SpineGeoClipCtexSource.Mode.Disabled, Sts2SpineGeoClipCtexSource.ParseMode(" OFF "));
        Assert.Equal(Sts2SpineGeoClipCtexSource.Mode.Disabled, Sts2SpineGeoClipCtexSource.ParseMode("false"));
        Assert.Equal(Sts2SpineGeoClipCtexSource.Mode.Disabled, Sts2SpineGeoClipCtexSource.ParseMode("no"));

        // The spellings that armed it while it was opt-in must still arm it, not disable it.
        Assert.Equal(Sts2SpineGeoClipCtexSource.Mode.Webp, Sts2SpineGeoClipCtexSource.ParseMode("1"));
        Assert.Equal(Sts2SpineGeoClipCtexSource.Mode.Webp, Sts2SpineGeoClipCtexSource.ParseMode(" ON "));
        Assert.Equal(Sts2SpineGeoClipCtexSource.Mode.Webp, Sts2SpineGeoClipCtexSource.ParseMode("webp"));
        Assert.Equal(Sts2SpineGeoClipCtexSource.Mode.PngName, Sts2SpineGeoClipCtexSource.ParseMode("PNG"));

        // The extension is the whole difference between the two armed modes, and the compat arm exists only
        // because a consumer's artifact whitelist may be spelled in `.png`.
        Assert.Equal("webp", Sts2SpineGeoClipCtexSource.ExtensionFor(Sts2SpineGeoClipCtexSource.Mode.Webp));
        Assert.Equal("png", Sts2SpineGeoClipCtexSource.ExtensionFor(Sts2SpineGeoClipCtexSource.Mode.PngName));
    }

    [Fact]
    public void CtexPathAndNameCheck_FollowTheSidecarAndRefuseAnotherTexturesContainer()
    {
        const string Sidecar = """
            [remap]

            importer="texture"
            type="CompressedTexture2D"
            uid="uid://cc7y61dd6f0b5"
            path="res://.godot/imported/byrdonis.png-312fa81c.ctex"
            """;

        Assert.Equal(
            "res://.godot/imported/byrdonis.png-312fa81c.ctex",
            Sts2SpineGeoClipCtexSource.CtexPathFromSidecar(Sidecar));
        Assert.Equal(string.Empty, Sts2SpineGeoClipCtexSource.CtexPathFromSidecar(null));
        Assert.Equal(string.Empty, Sts2SpineGeoClipCtexSource.CtexPathFromSidecar("[remap]\nimporter=\"texture\""));

        // Godot names an imported file "<source file name>-<hash>.ctex", so the source name is a prefix. This is
        // what stops a sidecar that was mis-parsed (or belongs to another resource) from being copied as a page.
        Assert.True(Sts2SpineGeoClipCtexSource.CtexNamesSource(
            "res://.godot/imported/byrdonis.png-312fa81c.ctex", "res://art/spine/byrdonis.png"));
        Assert.False(Sts2SpineGeoClipCtexSource.CtexNamesSource(
            "res://.godot/imported/merchant.png-312fa81c.ctex", "res://art/spine/byrdonis.png"));
        Assert.False(Sts2SpineGeoClipCtexSource.CtexNamesSource(
            "res://.godot/imported/byrdonis.png.ctex", "res://art/spine/byrdonis.png"));
        Assert.False(Sts2SpineGeoClipCtexSource.CtexNamesSource(
            "res://.godot/imported/byrdonis.png-312fa81c.spatlas", "res://art/spine/byrdonis.png"));
        Assert.False(Sts2SpineGeoClipCtexSource.CtexNamesSource(null, "res://art/spine/byrdonis.png"));
        Assert.False(Sts2SpineGeoClipCtexSource.CtexNamesSource("res://.godot/imported/a.png-1.ctex", null));
    }

    [Fact]
    public void Ctex_HandsBackTheEmbeddedWebpWhenEveryDescriptionOfThePageAgrees()
    {
        var decision = Sts2SpineGeoClipCtexSource.Extract(Ctex(2048, 1024), 2048, 1024, 2048, 1024);

        Assert.True(decision.Accept);
        Assert.Equal("ok", decision.Reason);
        Assert.Equal(2048, decision.Payload!.Width);
        Assert.Equal(1024, decision.Payload.Height);

        // The bytes handed back are the payload ONLY — the container header must not reach the page file.
        Assert.Equal(Webp(2048, 1024), decision.Payload.Bytes);
        Assert.Equal([(byte)'R', (byte)'I', (byte)'F', (byte)'F'], decision.Payload.Bytes[..4]);

        // An atlas that declares no size for the page is not evidence AGAINST it — the texture still is.
        Assert.True(Sts2SpineGeoClipCtexSource.Extract(Ctex(2048, 1024), 2048, 1024, 0, 0).Accept);
    }

    // THE ONE THAT MATTERS. Every way the container can fail to be the page the manifest references, each with
    // its own token, because a live run that shows no saving has to say which clause declined.
    [Fact]
    public void Ctex_DeclinesCleanlyOnEveryShapeItDoesNotUnderstand()
    {
        Assert.Equal("ctex-unreadable", Reason(null));
        Assert.Equal("ctex-unreadable", Reason([]));
        Assert.Equal("ctex-too-short:20", Reason(Ctex(2048, 1024)[..20]));

        var wrongMagic = Ctex(2048, 1024);
        wrongMagic[1] = (byte)'X';
        Assert.Equal("ctex-magic", Reason(wrongMagic));

        Assert.Equal("ctex-version:2", Reason(Ctex(2048, 1024, version: 2)));

        // dataFormat 0 is a RAW image — what a VRAM-compressed (BPTC) page imports to. There is nothing a
        // browser could do with those bytes, and the size field this reader would read is not a size field.
        Assert.Equal("ctex-data-format:0", Reason(Ctex(2048, 1024, dataFormat: 0)));
        Assert.Equal("ctex-data-format:1", Reason(Ctex(2048, 1024, dataFormat: 1)));

        Assert.Equal("ctex-mipmaps:9", Reason(Ctex(2048, 1024, mipmaps: 9)));
        Assert.Equal("ctex-image-format:4", Reason(Ctex(2048, 1024, imageFormat: 4)));

        // The container states the image size twice; a VRAM import is where they disagree (the payload is padded
        // up to the block size), so they have to agree before either is believed.
        Assert.Equal(
            "ctex-header-size-mismatch:2048x1020-vs-2048x1024",
            Reason(Ctex(2048, 1024, containerHeight: 1020)));

        Assert.Equal("ctex-payload-empty", Reason(Ctex(2048, 1024, payloadSizeOverride: 0)));
        Assert.Equal(
            "ctex-payload-truncated:100056-vs-" + Ctex(2048, 1024).Length.ToString(CultureInfo.InvariantCulture),
            Reason(Ctex(2048, 1024, payloadSizeOverride: 100_000)));

        var notWebp = Ctex(2048, 1024);
        notWebp[56 + 8] = (byte)'X';
        Assert.Equal("ctex-payload-not-webp", Reason(notWebp));

        // The RIFF container states its own length. Checking it against the ctex's makes a truncated or
        // over-long payload a refusal rather than half an image, without decoding anything.
        var shortRiff = Ctex(2048, 1024);
        shortRiff[56 + 4] -= 4;
        Assert.Equal(
            "ctex-riff-size-mismatch:"
            + (Webp(2048, 1024).Length - 4).ToString(CultureInfo.InvariantCulture)
            + "-vs-" + Webp(2048, 1024).Length.ToString(CultureInfo.InvariantCulture),
            Reason(shortRiff));

        Assert.Equal("texture-size-unknown", Reason(Ctex(2048, 1024), textureWidth: 0));
        Assert.Equal(
            "ctex-texture-size-mismatch:2048x1024-vs-1024x1024",
            Reason(Ctex(2048, 1024), textureWidth: 1024));
        Assert.Equal(
            "ctex-texture-size-mismatch:2048x1024-vs-2048x512",
            Reason(Ctex(2048, 1024), textureHeight: 512));
        Assert.Equal(
            "ctex-atlas-size-mismatch:2048x1024-vs-4096x1024",
            Reason(Ctex(2048, 1024), atlasWidth: 4096));
        Assert.Equal(
            "ctex-atlas-size-mismatch:2048x1024-vs-2048x4096",
            Reason(Ctex(2048, 1024), atlasHeight: 4096));
    }

    // ── Page identity: what "the caller already holds this page" is keyed on ──────────────────────────

    [Fact]
    public void ContentId_IsTheLowercaseSha256OfTheBytesAndNothingElse()
    {
        // The known SHA-256 of the empty input, so this pins the ALGORITHM rather than agreeing with itself.
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            Sts2SpineGeoClipPageId.ContentId([]));

        var id = Sts2SpineGeoClipPageId.ContentId(Png(2048, 2048));
        Assert.Equal(64, id.Length);
        Assert.Equal(id, id.ToLowerInvariant());
        Assert.Equal(id, Sts2SpineGeoClipPageId.ContentId(Png(2048, 2048)));
        Assert.NotEqual(id, Sts2SpineGeoClipPageId.ContentId(Png(2048, 1024)));
    }

    // ── The rig plan ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NormalizeAnimations_PutsThePrimaryFirstAndKeepsTheRestInOrder()
    {
        Assert.Equal(
            ["idle_loop", "attack", "die"],
            Sts2SpineGeoClipBatch.NormalizeAnimations("idle_loop", ["attack", "die"]));

        // Repeating the primary in the list is the obvious way to write "bake these N" and means the same thing.
        Assert.Equal(
            ["idle_loop", "attack"],
            Sts2SpineGeoClipBatch.NormalizeAnimations("idle_loop", ["idle_loop", "attack", "attack"]));

        Assert.Equal(["idle_loop"], Sts2SpineGeoClipBatch.NormalizeAnimations("idle_loop", null));
        Assert.Equal(["idle_loop"], Sts2SpineGeoClipBatch.NormalizeAnimations(" idle_loop ", ["", "   "]));
        Assert.Empty(Sts2SpineGeoClipBatch.NormalizeAnimations(null, null));
    }

    [Fact]
    public void ChooseAcquisitionStop_TakesTheWidestPoseAndBreaksTiesEarly()
    {
        Assert.Equal(2, Sts2SpineGeoClipBatch.ChooseAcquisitionStop([27, 29, 44, 30]));
        Assert.Equal(0, Sts2SpineGeoClipBatch.ChooseAcquisitionStop([44, 44, 12]));
        Assert.Equal(0, Sts2SpineGeoClipBatch.ChooseAcquisitionStop([]));
        Assert.Equal(3, Sts2SpineGeoClipBatch.ChooseAcquisitionStop([0, 0, 0, 1]));
    }

    // A SINGLE-ANIMATION bake must keep the natural frame order. The round's acceptance criterion is that
    // association PAIRS are identical to the pre-change bake, and which frame a slot is probed at is exactly what
    // decides which mesh moves — so a reordering that leaked into the single path would change answers nobody
    // asked to change.
    [Fact]
    public void ProbeOrder_IsUntouchedForOneAnimationAndAcquisitionFirstForARig()
    {
        Assert.Equal([0, 1, 2, 3, 4], Sts2SpineGeoClipBatch.ProbeOrder(5, acquisitionStop: 3, batched: false));
        Assert.Equal([0], Sts2SpineGeoClipBatch.ProbeOrder(1, acquisitionStop: 0, batched: false));

        Assert.Equal([3, 0, 1, 2, 4], Sts2SpineGeoClipBatch.ProbeOrder(5, acquisitionStop: 3, batched: true));
        Assert.Equal([0, 1, 2], Sts2SpineGeoClipBatch.ProbeOrder(3, acquisitionStop: 0, batched: true));

        // Defensive: an acquisition stop that is not a stop cannot be allowed to drop or duplicate one.
        Assert.Equal([0, 1, 2], Sts2SpineGeoClipBatch.ProbeOrder(3, acquisitionStop: 9, batched: true));
        Assert.Equal([0, 1, 2], Sts2SpineGeoClipBatch.ProbeOrder(3, acquisitionStop: -1, batched: true));
        Assert.Empty(Sts2SpineGeoClipBatch.ProbeOrder(0, acquisitionStop: 0, batched: true));

        // Every stop appears exactly once, whichever arm ran — a probe order that lost a stop would silently
        // stop probing the slots that first appear there.
        var order = Sts2SpineGeoClipBatch.ProbeOrder(6, acquisitionStop: 4, batched: true);
        Assert.Equal(6, order.Length);
        Assert.Equal(Enumerable.Range(0, 6), order.OrderBy(stop => stop));
    }

    // THE GUARD THE staleMeshFrames COUNTER CANNOT BE. A slot that draws attachment A where the association is
    // measured and attachment B elsewhere may be drawn by a DIFFERENT mesh there, and the stale counter only
    // sees that when the old mesh reads back EMPTY — a recycled RID that reads back populated is the same fault
    // with no symptom.
    [Fact]
    public void AttachmentDrift_FindsSlotsThatSwapAttachmentAndNothingElse()
    {
        var acquisition = new Dictionary<int, string> { [0] = "body", [1] = "head_calm", [2] = "arm" };

        Assert.Empty(Sts2SpineGeoClipBatch.AttachmentDrift(acquisition, acquisition));

        Assert.Equal(
            [1],
            Sts2SpineGeoClipBatch.AttachmentDrift(
                acquisition,
                new Dictionary<int, string> { [0] = "body", [1] = "head_angry", [2] = "arm" }));

        // A slot HIDDEN at this pose has no geometry here, so it is not drift.
        Assert.Empty(
            Sts2SpineGeoClipBatch.AttachmentDrift(
                acquisition,
                new Dictionary<int, string> { [0] = "body" }));

        // A slot NEWLY VISIBLE here is not drift either — the association pass probes it at its own stop.
        Assert.Empty(
            Sts2SpineGeoClipBatch.AttachmentDrift(
                acquisition,
                new Dictionary<int, string> { [0] = "body", [7] = "weapon" }));

        Assert.Equal(
            [0, 2],
            Sts2SpineGeoClipBatch.AttachmentDrift(
                acquisition,
                new Dictionary<int, string> { [0] = "body_hurt", [2] = "arm_raised", [7] = "weapon" }));
    }

    [Fact]
    public void BatchRefusal_ReportsAStaleMeshAheadOfDriftAndNothingWhenAllThreeAreClean()
    {
        Assert.Null(Sts2SpineGeoClipBatch.BatchRefusal(0, 0));
        Assert.Equal("staleMeshFrames=3", Sts2SpineGeoClipBatch.BatchRefusal(3, 0));
        Assert.Equal("attachmentDrift=2", Sts2SpineGeoClipBatch.BatchRefusal(0, 2));

        // A rig that re-mints RIDs makes every batched pose suspect, so that verdict is the one reported even
        // when drift is also present.
        Assert.Equal("staleMeshFrames=1", Sts2SpineGeoClipBatch.BatchRefusal(1, 5));
    }

    // A BATCH SETS MORE ANIMATIONS, so it can validate meshes no probed slot answers for — and `foreignMeshes` is
    // fatal downstream. Blocking on it by default is what keeps a batched pose's verdict from being harsher than
    // the per-target bake it replaces; the lever exists because whether it ever fires is a live question.
    [Fact]
    public void BatchRefusal_BlocksOnAForeignMeshByDefaultAndYieldsToTheLever()
    {
        Assert.Equal("foreignMeshes=8", Sts2SpineGeoClipBatch.BatchRefusal(0, 0, foreignMeshes: 8));
        Assert.Null(Sts2SpineGeoClipBatch.BatchRefusal(0, 0, foreignMeshes: 8, foreignBlocks: false));
        Assert.Null(Sts2SpineGeoClipBatch.BatchRefusal(0, 0, foreignMeshes: 0));

        // Ordering: a foreign mesh never masks the two verdicts that say something about the RIG's behaviour.
        Assert.Equal("staleMeshFrames=2", Sts2SpineGeoClipBatch.BatchRefusal(2, 0, foreignMeshes: 8));
        Assert.Equal("attachmentDrift=1", Sts2SpineGeoClipBatch.BatchRefusal(0, 1, foreignMeshes: 8));

        // The lever relaxes ONLY the foreign arm.
        Assert.Equal("staleMeshFrames=2", Sts2SpineGeoClipBatch.BatchRefusal(2, 0, 8, foreignBlocks: false));
        Assert.Equal("attachmentDrift=1", Sts2SpineGeoClipBatch.BatchRefusal(0, 1, 8, foreignBlocks: false));
    }

    // ── The env lane's opt-in grouping ───────────────────────────────────────────────────────────────

    [Fact]
    public void GroupTargets_FusesNothingUnlessAskedAndOnlyConsecutiveSiblingsWhenItIs()
    {
        var byrdonisIdle = Target("res://byrdonis.tscn", "Visuals", "idle_loop");
        var byrdonisAttack = Target("res://byrdonis.tscn", "Visuals", "attack");
        var byrdonisOtherNode = Target("res://byrdonis.tscn", "Shadow", "idle_loop");
        var merchant = Target("res://merchant.tscn", "Visuals", "idle_loop");

        var ungrouped = Sts2SpineGeoClipBatch.GroupTargets(
            [byrdonisIdle, byrdonisAttack, merchant], group: false);
        Assert.Equal([1, 1, 1], ungrouped.Select(g => g.Count));

        var grouped = Sts2SpineGeoClipBatch.GroupTargets(
            [byrdonisIdle, byrdonisAttack, merchant], group: true);
        Assert.Equal([2, 1], grouped.Select(g => g.Count));
        Assert.Equal(["idle_loop", "attack"], grouped[0].Select(t => t.Animation));

        // A different NODE of the same scene is a different rig, and the split must not swallow it.
        Assert.Equal(
            [1, 1],
            Sts2SpineGeoClipBatch.GroupTargets([byrdonisIdle, byrdonisOtherNode], group: true).Select(g => g.Count));

        // Non-consecutive is not fused: a spec's order is the operator's order and re-sorting it would move
        // which bake runs when.
        Assert.Equal(
            [1, 1, 1],
            Sts2SpineGeoClipBatch.GroupTargets([byrdonisIdle, merchant, byrdonisAttack], group: true)
                .Select(g => g.Count));

        Assert.Empty(Sts2SpineGeoClipBatch.GroupTargets([], group: true));
    }

    // ── The request seam ─────────────────────────────────────────────────────────────────────────────

    // THE SINGLE-ANIMATION SEAM STILL WORKS. The on-demand provider path sends one animation and no list, and it
    // has to plan exactly the one target it always planned.
    [Fact]
    public void PlanConfig_StillPlansExactlyOneTargetForASingleAnimationRequest()
    {
        var config = Sts2SpineGeoClipRequestLane.PlanConfig(
            new SpineGeoClipBakeRequestSnapshot(
                "res://byrdonis.tscn",
                "Visuals",
                "idle_loop",
                SampleTimeSeconds: null,
                OutputDirectory: "/tmp/staging"),
            "Spirectl.Sts2",
            out var rejected);

        Assert.Null(rejected);
        Assert.NotNull(config);
        var target = Assert.Single(config!.Targets);
        Assert.Equal("idle_loop", target.Animation);
        Assert.Equal("res://byrdonis.tscn?anim=idle_loop&node=Visuals&pose=1", target.Raw);
        Assert.True(target.PoseOnly);
        Assert.Null(config.KnownPageContentIds);
    }

    [Fact]
    public void PlanConfig_TurnsAnAnimationListIntoOneTargetPerAnimationOfTheSameRig()
    {
        var config = Sts2SpineGeoClipRequestLane.PlanConfig(
            new SpineGeoClipBakeRequestSnapshot(
                "res://byrdonis.tscn",
                "Visuals",
                "idle_loop",
                SampleTimeSeconds: null,
                OutputDirectory: "/tmp/staging",
                AnimationNames: ["attack", "idle_loop", "die"],
                KnownPageContentIds: new HashSet<string>(StringComparer.Ordinal) { "abc123" }),
            "Spirectl.Sts2",
            out var rejected);

        Assert.Null(rejected);
        Assert.NotNull(config);
        Assert.Equal(["idle_loop", "attack", "die"], config!.Targets.Select(target => target.Animation));
        Assert.All(config.Targets, target => Assert.Equal("res://byrdonis.tscn", target.ScenePath));
        Assert.All(config.Targets, target => Assert.Equal("Visuals", target.NodePath));
        Assert.All(config.Targets, target => Assert.True(target.PoseOnly));

        // Every target's `Raw` names ITS OWN animation — it is what every log line labels the target with, and a
        // rig bake whose eight targets all logged the first animation's name would be unreadable.
        Assert.Equal("res://byrdonis.tscn?anim=die&node=Visuals&pose=1", config.Targets[2].Raw);

        Assert.NotNull(config.KnownPageContentIds);
        Assert.Contains("abc123", config.KnownPageContentIds!);
    }

    // ── The result seam ──────────────────────────────────────────────────────────────────────────────

    // `Poses` is populated for a SINGLE bake too, so a caller never needs a special case for N=1 — and the
    // top-level fields keep meaning what they always meant.
    [Fact]
    public void ToSnapshot_GivesASingleBakeAOnePoseListThatRestatesTheTopLevel()
    {
        var snapshot = Sts2SpineGeoClipRequestLane.ToSnapshot(Outcome("idle_loop", complete: true, associated: 28));

        Assert.True(snapshot.Success);
        Assert.Equal(1, snapshot.ScenesLoaded);
        Assert.Equal("single", snapshot.BatchNote);
        var pose = Assert.Single(snapshot.Poses!);
        Assert.Equal("idle_loop", pose.AnimationName);
        Assert.Equal(snapshot.ManifestPath, pose.ManifestPath);
        Assert.Equal(snapshot.Associated, pose.Associated);
        Assert.Equal(snapshot.Complete, pose.Complete);
        Assert.False(pose.Batched);
    }

    [Fact]
    public void ToSnapshot_CarriesEveryPoseOfARigBakeInRequestOrder()
    {
        var rig = new GeoClipRigBakeOutcome(
            [
                Outcome("idle_loop", complete: true, associated: 44, batched: true),
                Outcome("attack", complete: true, associated: 44, batched: true),
                Outcome("die", complete: false, associated: 27, batched: false, drift: 3),
            ],
            ScenesLoaded: 2,
            "batched+fallback:attachmentDrift=3",
            ElapsedMs: 730d);

        var snapshot = Sts2SpineGeoClipRequestLane.ToSnapshot(rig);

        // The PRIMARY animation is what the top level describes — the contract every caller predating batching
        // reads.
        Assert.True(snapshot.Success);
        Assert.Equal(44, snapshot.Associated);
        Assert.Equal(2, snapshot.ScenesLoaded);
        Assert.Equal("batched+fallback:attachmentDrift=3", snapshot.BatchNote);

        // The RIG's wall time, not the primary pose's 210 ms — the amortized-per-pose number the round is graded
        // on is this divided by the poses, and a figure that stopped at pose 1 would flatter it by 3x.
        Assert.Equal(730d, snapshot.ElapsedMs);

        Assert.Equal(["idle_loop", "attack", "die"], snapshot.Poses!.Select(pose => pose.AnimationName));
        Assert.Equal([true, true, false], snapshot.Poses!.Select(pose => pose.Complete));
        Assert.Equal([true, true, false], snapshot.Poses!.Select(pose => pose.Batched));
        Assert.Equal(3, snapshot.Poses![2].AttachmentDriftSlots);
        Assert.Equal(27, snapshot.Poses![2].Associated);
    }

    [Fact]
    public void ToSnapshot_KeepsAFailedRigBakesReasonOnTheOnlyPoseItHas()
    {
        var rig = new GeoClipRigBakeOutcome(
            [GeoClipBakeOutcome.Failed("the skeleton exposed no slots.", animationName: "idle_loop")],
            ScenesLoaded: 1,
            "single");

        var snapshot = Sts2SpineGeoClipRequestLane.ToSnapshot(rig);

        Assert.False(snapshot.Success);
        Assert.NotNull(snapshot.Error);
        Assert.Contains("no slots", snapshot.Error!.Message, StringComparison.Ordinal);
        var pose = Assert.Single(snapshot.Poses!);
        Assert.Equal("idle_loop", pose.AnimationName);
        Assert.False(pose.Success);
        Assert.Equal("the skeleton exposed no slots.", pose.FailureReason);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────────────────────

    private const string Sidecar = """
    [remap]
    importer="texture"
    type="CompressedTexture2D"
    path="res://.godot/imported/byrdonis.png-2f1a9c.ctex"

    [params]
    compress/mode=2
    process/fix_alpha_border=true
    process/premult_alpha=false
    mipmaps/generate=false
    """;

    private const string PremultSidecar = """
    [params]
    process/fix_alpha_border=true
    process/premult_alpha=true
    """;

    private static Sts2SpineGeoClipPageSource.Decision Decide(
        byte[] bytes,
        string? sourcePath = "res://art/spine/byrdonis.png",
        int textureWidth = 2048,
        int textureHeight = 2048,
        string? atlasPageName = "byrdonis.png",
        int atlasWidth = 2048,
        int atlasHeight = 2048,
        string? sidecar = Sidecar,
        Sts2SpineGeoClipPageSource.Mode mode = Sts2SpineGeoClipPageSource.Mode.Strict)
        => Sts2SpineGeoClipPageSource.Decide(
            mode, sourcePath, bytes, textureWidth, textureHeight, atlasPageName, atlasWidth, atlasHeight, sidecar);

    private static string Reason(
        byte[]? ctex,
        int textureWidth = 2048,
        int textureHeight = 1024,
        int atlasWidth = 2048,
        int atlasHeight = 1024)
        => Sts2SpineGeoClipCtexSource.Extract(ctex, textureWidth, textureHeight, atlasWidth, atlasHeight).Reason;

    /// <summary>
    /// A synthetic Godot <c>GST2</c> v1 container: the 56-byte header this reader parses, then a WebP payload.
    /// The offsets and the enum values are the ones verified byte for byte against real imported textures out of
    /// the shipped pck (see the round's research note); nothing here is a guess about the layout.
    /// </summary>
    private static byte[] Ctex(
        int width,
        int height,
        int version = 1,
        int dataFormat = 2,
        int mipmaps = 0,
        int imageFormat = 5,
        int? containerWidth = null,
        int? containerHeight = null,
        int? payloadSizeOverride = null)
    {
        var payload = Webp(width, height);
        var bytes = new byte[56 + payload.Length];
        bytes[0] = (byte)'G';
        bytes[1] = (byte)'S';
        bytes[2] = (byte)'T';
        bytes[3] = (byte)'2';
        WriteUInt32(bytes, 4, version);
        WriteUInt32(bytes, 8, containerWidth ?? width);
        WriteUInt32(bytes, 12, containerHeight ?? height);
        WriteUInt32(bytes, 16, 0x0D00_0000); // detect-3d/normal/roughness flags; not read
        WriteUInt32(bytes, 20, unchecked((int)0xFFFF_FFFF)); // mipmap limit -1
        WriteUInt32(bytes, 36, dataFormat);
        bytes[40] = (byte)width;
        bytes[41] = (byte)(width >> 8);
        bytes[42] = (byte)height;
        bytes[43] = (byte)(height >> 8);
        WriteUInt32(bytes, 44, mipmaps);
        WriteUInt32(bytes, 48, imageFormat);
        WriteUInt32(bytes, 52, payloadSizeOverride ?? payload.Length);
        payload.CopyTo(bytes, 56);
        return bytes;
    }

    /// <summary>A minimal well-formed WebP: the RIFF envelope, its self-declared length, and a body.</summary>
    private static byte[] Webp(int width, int height)
    {
        var body = new byte[64];
        for (var i = 0; i < body.Length; i += 1)
        {
            body[i] = (byte)((width * 7) + (height * 13) + i);
        }

        var bytes = new byte[12 + body.Length];
        bytes[0] = (byte)'R';
        bytes[1] = (byte)'I';
        bytes[2] = (byte)'F';
        bytes[3] = (byte)'F';
        WriteUInt32(bytes, 4, bytes.Length - 8);
        bytes[8] = (byte)'W';
        bytes[9] = (byte)'E';
        bytes[10] = (byte)'B';
        bytes[11] = (byte)'P';
        body.CopyTo(bytes, 12);
        return bytes;
    }

    private static void WriteUInt32(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16);
        bytes[offset + 3] = (byte)(value >> 24);
    }

    /// <summary>A minimal well-formed PNG PREFIX: the signature plus an IHDR declaring this size.</summary>
    private static byte[] Png(int width, int height)
    {
        byte[] bytes =
        [
            0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A,
            0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
            (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height,
            8, 6, 0, 0, 0,
        ];
        return bytes;
    }

    private static GeoClipTarget Target(string scene, string node, string animation)
        => new(scene, node, animation, $"{scene}?anim={animation}&node={node}");

    private static GeoClipBakeOutcome Outcome(
        string animation,
        bool complete,
        int associated,
        bool batched = false,
        int drift = 0)
        => new(
            Success: true,
            ManifestPath: $"/tmp/staging/rig--Visuals--{animation}/manifest.json",
            PageFileNames: ["page-0.png"],
            PartCount: associated,
            FrameCount: 1,
            SampleTimeSeconds: 1.5d,
            SampleTimeSource: "mid",
            ElapsedMs: 210d,
            Slots: 44,
            SlotsVisible: associated,
            Associated: associated,
            Unassociated: 0,
            ForeignMeshes: 0,
            Complete: complete,
            FailureReason: null,
            AnimationName: animation,
            StaleMeshFrames: 0,
            AttachmentDriftSlots: drift,
            Batched: batched);
}
