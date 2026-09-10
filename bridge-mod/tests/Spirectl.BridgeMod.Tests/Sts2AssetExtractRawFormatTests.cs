#if ENABLE_STS2_LIVE_HOST
using System.Text;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// Godot-native-first `/res` switch: the extraction serves raw resource bytes by default and the GodotResource
// JSON document only for an explicit "json" caller. These cover the pure format-selection + content-type-sniff
// logic without a live host (the raw-vs-JSON dispatch itself needs the game's ResourceLoader and is exercised
// by the live curl matrix).
public sealed class Sts2AssetExtractRawFormatTests
{
    [Theory]
    [InlineData("raw", true)]
    [InlineData("RAW", true)]
    [InlineData(" raw ", true)]
    [InlineData("json", false)]
    [InlineData("auto", false)]
    [InlineData("structure", false)]
    [InlineData("png", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyRawFormatDivertsResourceDocumentsToBytes(string? format, bool expected)
    {
        Assert.Equal(expected, Sts2AssetExtractProvider.WantsRawResourceBytes(format));
    }

    [Theory]
    // "raw" is a STRUCTURE-family format: scenes stay GodotSceneState JSON and shaders stay raw `.gdshader`
    // text under it (only the AtlasTexture/Font/Material `.tres` DOCUMENTS divert to raw bytes).
    [InlineData("raw", true)]
    [InlineData("auto", true)]
    [InlineData("structure", true)]
    [InlineData("json", true)]
    [InlineData("scene-state", true)]
    [InlineData("png", false)]
    [InlineData("webp", false)]
    public void RawStaysInTheStructureFamilyForScenesAndShaders(string format, bool expected)
    {
        Assert.Equal(expected, Sts2AssetExtractProvider.WantsSceneStructureFormat(format));
    }

    [Fact]
    public void GodotTextResourceHeaderIsServedAsUtf8Text()
    {
        var bytes = Encoding.UTF8.GetBytes("[gd_resource type=\"ShaderMaterial\" format=3]\n");
        var (format, contentType) = Sts2AssetExtractProvider.ClassifyRawResourceBytes("res://materials/x.tres", bytes);

        Assert.Equal("tres", format);
        Assert.Equal("text/plain; charset=utf-8", contentType);
    }

    [Fact]
    public void GodotTextSceneHeaderIsServedAsUtf8Text()
    {
        var bytes = Encoding.UTF8.GetBytes("[gd_scene load_steps=2 format=3]\n");
        var (format, contentType) = Sts2AssetExtractProvider.ClassifyRawResourceBytes("res://scenes/x.tscn", bytes);

        Assert.Equal("tscn", format);
        Assert.Equal("text/plain; charset=utf-8", contentType);
    }

    [Theory]
    // Binary Godot resource containers (an exported `.res`/`.scn`) begin with RSRC (uncompressed) or RSCC
    // (compressed) — served as opaque octet-stream bytes.
    [InlineData("RSRC")]
    [InlineData("RSCC")]
    public void BinaryGodotResourceContainerIsServedAsOctetStream(string magic)
    {
        var bytes = Encoding.ASCII.GetBytes(magic + "\0\0\0\0");
        var (format, contentType) = Sts2AssetExtractProvider.ClassifyRawResourceBytes("res://materials/x.res", bytes);

        Assert.Equal("res", format);
        Assert.Equal("application/octet-stream", contentType);
    }

    [Fact]
    public void UnknownBytesFallBackToTheExtensionContentType()
    {
        var textByExtension = Sts2AssetExtractProvider.ClassifyRawResourceBytes("res://x.tres", [0x01, 0x02, 0x03, 0x04]);
        Assert.Equal("tres", textByExtension.Format);
        Assert.Equal("text/plain; charset=utf-8", textByExtension.ContentType);

        var binaryByExtension = Sts2AssetExtractProvider.ClassifyRawResourceBytes("res://x.bin", [0x01, 0x02, 0x03, 0x04]);
        Assert.Equal("bin", binaryByExtension.Format);
        Assert.Equal("application/octet-stream", binaryByExtension.ContentType);
    }
}

#endif
