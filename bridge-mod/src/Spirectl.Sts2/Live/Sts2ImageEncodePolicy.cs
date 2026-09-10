using System;

namespace Spirectl.Sts2.Live;

/// <summary>
/// Which encoder a single-image asset render uses, and with what settings. Godot-free and always compiled (like
/// <see cref="Sts2RenderPhaseProfile"/>) so the DECISIONS — is this codec supported, does this quality count, does
/// the alpha channel survive — are offline-unit-testable; the Godot-typed lane is left with a bare mapping from
/// <see cref="ImageCodec"/> to the matching <c>Image.Save*ToBuffer</c> call.
/// </summary>
/// <remarks>
/// The browser-decodable set is exactly PNG / WebP / JPEG, because those are the buffer encoders Godot ships
/// (<c>SavePngToBuffer</c>, <c>SaveWebpToBuffer</c>, <c>SaveJpgToBuffer</c>; Exr/Dds are not web formats). There is
/// no AVIF, QOI or JPEG-XL encoder to reach for, so a codec question is answered inside this set or by rendering
/// fewer pixels.
/// </remarks>
public static class Sts2ImageEncodePolicy
{
    /// <summary>
    /// The JPEG quality used when a jpg request names none — Godot's own <c>Image.SaveJpg</c> default, so an
    /// unqualified request is not silently a different encoder setting than the engine's.
    /// </summary>
    public const float JpegDefaultQuality = 0.75f;

    public enum ImageCodec
    {
        /// <summary>Lossless, alpha-capable, and the slowest of the three at large sizes.</summary>
        Png,

        /// <summary>Lossless when no quality is named, lossy at that quality when one is.</summary>
        Webp,

        /// <summary>Always lossy, and NEVER carries alpha — see <see cref="Plan.Opaque"/>.</summary>
        Jpeg,

        /// <summary>An unrecognized requested format: encode PNG and say so in a note.</summary>
        PngFallback,
    }

    /// <summary>
    /// The resolved encode settings. <see cref="ActualFormat"/> is what the result reports it produced, which is
    /// not always what was asked for (an unknown format falls back to PNG).
    /// </summary>
    public readonly record struct Plan(ImageCodec Codec, float? Quality, bool Opaque)
    {
        /// <summary>The format string the encoded result carries (and the content type is derived from).</summary>
        public string ActualFormat => Codec switch
        {
            ImageCodec.Webp => "webp",
            ImageCodec.Jpeg => "jpeg",
            _ => "png",
        };

        /// <summary>Whether the encoder discards information — the fidelity question a bench has to ask.</summary>
        public bool IsLossy => Codec == ImageCodec.Jpeg || (Codec == ImageCodec.Webp && Quality is not null);

        /// <summary>Quality to hand the encoder, with JPEG's default filled in. Meaningless for PNG.</summary>
        public float EffectiveQuality => Codec == ImageCodec.Jpeg ? Quality ?? JpegDefaultQuality : Quality ?? 1f;
    }

    /// <summary>
    /// Resolve a request's <c>OutputFormat</c> / <c>ImageQuality</c> / <c>ImageOpaque</c> into one plan. Absent
    /// quality and opaque reproduce the pre-R21 behaviour exactly: PNG stays PNG, webp stays LOSSLESS, and the
    /// alpha channel is untouched.
    /// </summary>
    public static Plan Resolve(string? outputFormat, float? requestedQuality = null, bool requestedOpaque = false)
    {
        var format = (outputFormat ?? string.Empty).Trim().ToLowerInvariant();
        var codec = format switch
        {
            "png" => ImageCodec.Png,
            "webp" => ImageCodec.Webp,
            "jpg" or "jpeg" => ImageCodec.Jpeg,
            _ => ImageCodec.PngFallback,
        };

        var quality = NormalizeQuality(requestedQuality);
        // PNG has no quality dial at all; carrying one would make two identical renders look like different
        // measurements in a bench table that labels its blocks by codec AND quality.
        if (codec is ImageCodec.Png or ImageCodec.PngFallback)
        {
            quality = null;
        }

        // JPEG cannot carry alpha, so the drop is forced rather than left to whatever the encoder assumes.
        var opaque = requestedOpaque || codec == ImageCodec.Jpeg;
        return new Plan(codec, quality, opaque);
    }

    /// <summary>
    /// A quality in (0, 1], or null. Out-of-range is IGNORED rather than clamped: a caller that sent 85 meaning
    /// "q85" gets the encoder's default call — visible in the reported bytes — instead of a silent q=1.0 that
    /// would look like a deliberate setting in a comparison table.
    /// </summary>
    public static float? NormalizeQuality(float? quality)
        => quality is { } value && !float.IsNaN(value) && value > 0f && value <= 1f ? value : null;
}
