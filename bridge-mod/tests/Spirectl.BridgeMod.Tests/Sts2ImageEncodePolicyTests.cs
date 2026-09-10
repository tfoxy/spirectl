using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// R21: which encoder a single-image asset render reaches for, and with what settings. The load-bearing property is
// the FIRST test — a request that names no quality and no opacity must resolve to exactly the pre-R21 encoder call,
// because every shipped caller is such a request and the round's contract is "the offload changes no bytes".
public sealed class Sts2ImageEncodePolicyTests
{
    [Fact]
    public void AbsentQualityAndOpacityReproduceTheShippedEncoderCalls()
    {
        var png = Sts2ImageEncodePolicy.Resolve("png");
        Assert.Equal(Sts2ImageEncodePolicy.ImageCodec.Png, png.Codec);
        Assert.Null(png.Quality);
        Assert.False(png.Opaque);
        Assert.False(png.IsLossy);

        // webp with no quality is LOSSLESS — the shipped clip/bench call — not "lossy at some default".
        var webp = Sts2ImageEncodePolicy.Resolve("webp");
        Assert.Equal(Sts2ImageEncodePolicy.ImageCodec.Webp, webp.Codec);
        Assert.Null(webp.Quality);
        Assert.False(webp.Opaque);
        Assert.False(webp.IsLossy);
    }

    [Fact]
    public void FormatIsMatchedCaseAndWhitespaceInsensitively()
    {
        Assert.Equal(Sts2ImageEncodePolicy.ImageCodec.Png, Sts2ImageEncodePolicy.Resolve("  PNG ").Codec);
        Assert.Equal(Sts2ImageEncodePolicy.ImageCodec.Webp, Sts2ImageEncodePolicy.Resolve("WebP").Codec);
        Assert.Equal(Sts2ImageEncodePolicy.ImageCodec.Jpeg, Sts2ImageEncodePolicy.Resolve("JPG").Codec);
        Assert.Equal(Sts2ImageEncodePolicy.ImageCodec.Jpeg, Sts2ImageEncodePolicy.Resolve("jpeg").Codec);
    }

    // "auto", an empty format, and anything the encoder set does not cover all take the PNG fallback — which
    // REPORTS png, so a consumer is never told it received bytes in a format that was not produced.
    [Fact]
    public void UnknownFormatsFallBackToPngAndSaySo()
    {
        foreach (var format in new[] { "auto", "", "   ", "avif", "qoi", "jxl", null })
        {
            var plan = Sts2ImageEncodePolicy.Resolve(format);
            Assert.Equal(Sts2ImageEncodePolicy.ImageCodec.PngFallback, plan.Codec);
            Assert.Equal("png", plan.ActualFormat);
            Assert.Null(plan.Quality);
        }
    }

    [Fact]
    public void WebpWithAQualityEncodesLossy()
    {
        var plan = Sts2ImageEncodePolicy.Resolve("webp", 0.85f);
        Assert.Equal(Sts2ImageEncodePolicy.ImageCodec.Webp, plan.Codec);
        Assert.Equal(0.85f, plan.Quality!.Value, 4);
        Assert.True(plan.IsLossy);
        Assert.Equal("webp", plan.ActualFormat);
    }

    // JPEG cannot carry alpha, so the drop is FORCED rather than left to whatever the encoder assumes under the
    // transparent pixels — and it is forced by the policy, where it is visible, not inside the Godot lane.
    [Fact]
    public void JpegAlwaysEncodesOpaqueAndLossy()
    {
        var plan = Sts2ImageEncodePolicy.Resolve("jpg");
        Assert.True(plan.Opaque);
        Assert.True(plan.IsLossy);
        Assert.Equal(Sts2ImageEncodePolicy.JpegDefaultQuality, plan.EffectiveQuality, 4);
        Assert.Equal("jpeg", plan.ActualFormat);

        Assert.Equal(0.9f, Sts2ImageEncodePolicy.Resolve("jpeg", 0.9f).EffectiveQuality, 4);
    }

    [Fact]
    public void OpacityCanBeRequestedForAnAlphaCapableCodec()
    {
        Assert.True(Sts2ImageEncodePolicy.Resolve("png", requestedOpaque: true).Opaque);
        Assert.True(Sts2ImageEncodePolicy.Resolve("webp", requestedOpaque: true).Opaque);
        // …and stripping alpha is not by itself lossy in the codec sense: a PNG stays byte-exact per channel.
        Assert.False(Sts2ImageEncodePolicy.Resolve("png", requestedOpaque: true).IsLossy);
    }

    // PNG has no quality dial. Carrying one anyway would make two identical renders look like two different
    // measurements in a bench table that labels its blocks by codec AND quality.
    [Fact]
    public void PngDropsAnyRequestedQuality()
    {
        Assert.Null(Sts2ImageEncodePolicy.Resolve("png", 0.5f).Quality);
        Assert.Null(Sts2ImageEncodePolicy.Resolve("auto", 0.5f).Quality);
    }

    // Out of range is IGNORED, not clamped: a caller that sent 85 meaning "q85" gets the encoder's default call —
    // visible in the reported bytes — instead of a silent q=1.0 that reads like a deliberate setting.
    [Fact]
    public void OutOfRangeQualityIsIgnoredRatherThanClamped()
    {
        Assert.Null(Sts2ImageEncodePolicy.NormalizeQuality(0f));
        Assert.Null(Sts2ImageEncodePolicy.NormalizeQuality(-0.5f));
        Assert.Null(Sts2ImageEncodePolicy.NormalizeQuality(1.5f));
        Assert.Null(Sts2ImageEncodePolicy.NormalizeQuality(85f));
        Assert.Null(Sts2ImageEncodePolicy.NormalizeQuality(float.NaN));
        Assert.Null(Sts2ImageEncodePolicy.NormalizeQuality(null));
        Assert.Equal(1f, Sts2ImageEncodePolicy.NormalizeQuality(1f)!.Value, 4);
        Assert.Equal(0.01f, Sts2ImageEncodePolicy.NormalizeQuality(0.01f)!.Value, 4);

        // …and a garbled quality on webp therefore falls back to LOSSLESS, never to a lossy q=1.
        var plan = Sts2ImageEncodePolicy.Resolve("webp", 85f);
        Assert.Null(plan.Quality);
        Assert.False(plan.IsLossy);
    }
}
