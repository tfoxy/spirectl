using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// Offline coverage for the GodotSceneState producer's gsw-correct ENCODING + SHAPE (the
// SceneState walk itself is live-host-gated in Sts2GodotSceneStateProducerLiveTests). These
// assert the exact contract godot-scene-web/packages/core/src/index.ts consumes: raw
// scalars, engine-native {type,args}, path-first ExtResource refs, bare parent paths, and
// omitted optionals.
public sealed class Sts2GodotSceneStateProducerTests
{
    [Theory]
    [InlineData(".", ".")]        // root's direct children keep the gsw root sentinel
    [InlineData("./Host", "Host")] // engine NodePath form is stripped to the bare path
    [InlineData("Host", "Host")]   // already-bare parent passes through
    [InlineData("Host/Inner", "Host/Inner")]
    public void NormalizeParentStripsEngineNodePathForm(string raw, string expected)
        => Assert.Equal(expected, Sts2GodotSceneStateEncoding.NormalizeParent(raw));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void NormalizeParentReturnsNullForEmpty(string? raw)
        => Assert.Null(Sts2GodotSceneStateEncoding.NormalizeParent(raw!));

    [Fact]
    public void TypeArgsEmitsEngineNativeShape()
    {
        var json = JsonSerializer.Serialize(Sts2GodotSceneStateEncoding.TypeArgs("Vector2", 12.0, -4.0));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Vector2", doc.RootElement.GetProperty("type").GetString());
        var args = doc.RootElement.GetProperty("args");
        Assert.Equal(JsonValueKind.Array, args.ValueKind);
        Assert.Equal(12.0, args[0].GetDouble());
        Assert.Equal(-4.0, args[1].GetDouble());
    }

    [Fact]
    public void ExtResourceRefIsPathFirst()
    {
        var json = JsonSerializer.Serialize(Sts2GodotSceneStateEncoding.ExtResourceRef("res://scenes/card.tscn"));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("ExtResource", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("res://scenes/card.tscn", doc.RootElement.GetProperty("path").GetString());
        Assert.False(doc.RootElement.TryGetProperty("id", out _)); // runtime producer emits path, not id
    }

    [Theory]
    // External files (fetchable by path) — no `::`.
    [InlineData("res://scenes/card.tscn", true)]
    [InlineData("res://materials/additive.tres", true)]
    // Inline sub-resources: a runtime-loaded scene-local resource carries a `::SubId` path; an
    // anonymous resource has no path. Neither is a fetchable file.
    [InlineData("res://scenes/char_select.tscn::Gradient_w0doc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsExternalResourcePathTreatsScopeLocalAsInline(string? path, bool external)
        => Assert.Equal(external, Sts2GodotSceneStateEncoding.IsExternalResourcePath(path));

    [Theory]
    [InlineData("res://scenes/char_select.tscn::Gradient_w0doc", "Gradient_w0doc")]
    [InlineData("res://a.tscn::CanvasItemMaterial_xyz", "CanvasItemMaterial_xyz")]
    [InlineData("res://scenes/card.tscn", null)] // external file -> no scene-local id
    [InlineData("", null)]
    [InlineData(null, null)]
    public void SubResourceIdParsesScopeLocalSuffix(string? path, string? expected)
        => Assert.Equal(expected, Sts2GodotSceneStateEncoding.SubResourceId(path));

    [Fact]
    public void SubResourceRefIsIdKeyed()
    {
        var json = JsonSerializer.Serialize(Sts2GodotSceneStateEncoding.SubResourceRef("SubResource_1"));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("SubResource", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("SubResource_1", doc.RootElement.GetProperty("id").GetString());
        Assert.False(doc.RootElement.TryGetProperty("path", out _)); // inline sub-resource -> id, not path
    }

    [Fact]
    public void SerializeEmbedsInlineSubResourceGradient()
    {
        // An inline particle `color_ramp`: the node property is a `{type:"SubResource", id}` ref,
        // and the doc's `subResources` table carries the Gradient body with colors/offsets encoded
        // as the `{type,args}` call-values gsw's packedColorValues/packedNumberArray + gradientColorStops
        // consume. Locks the exact shape that makes a white->red ember ramp resolve (vs dropping to white).
        var gradient = new GodotSubResourceDto(
            Id: "SubResource_1",
            Type: "Gradient",
            Attributes: new Dictionary<string, object?>(),
            Properties: new Dictionary<string, object?>
            {
                ["offsets"] = Sts2GodotSceneStateEncoding.TypeArgs("PackedFloat32Array", 0.0, 1.0),
                ["colors"] = Sts2GodotSceneStateEncoding.TypeArgs("PackedColorArray",
                    1.0, 1.0, 1.0, 1.0, /* stop 0: white */
                    1.0, 0.0, 0.0, 1.0 /* stop 1: red */),
            });

        var dto = new GodotSceneStateDto(
            Kind: "scene",
            Nodes: new List<GodotSceneStateNodeDto>
            {
                new(
                    Index: 0,
                    SiblingIndex: 0,
                    Name: "ash3",
                    Type: "CPUParticles2D",
                    Parent: null,
                    Owner: null,
                    Instance: null,
                    InstancePlaceholder: null,
                    Groups: System.Array.Empty<string>(),
                    Properties: new List<GodotOrderedPropertyDto>
                    {
                        new("color_ramp", Sts2GodotSceneStateEncoding.SubResourceRef("SubResource_1")),
                    }),
            },
            Connections: System.Array.Empty<GodotSceneStateConnectionDto>(),
            ExtResources: System.Array.Empty<object>(),
            SubResources: new object[] { gradient },
            EditableInstances: System.Array.Empty<string>(),
            BasePath: "res://scenes/screens/char_select/char_select_bg_ironclad.tscn",
            Diagnostics: System.Array.Empty<GodotParseDiagnosticDto>());

        var bytes = Sts2GodotSceneStateEncoding.Serialize(dto);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
        var root = doc.RootElement;

        // The node property references the sub-resource by id.
        var colorRamp = root.GetProperty("nodes")[0].GetProperty("properties")[0].GetProperty("value");
        Assert.Equal("SubResource", colorRamp.GetProperty("type").GetString());
        Assert.Equal("SubResource_1", colorRamp.GetProperty("id").GetString());

        // The sub-resource table entry: id/type + a keyed properties map with the encoded ramp.
        var sub = root.GetProperty("subResources")[0];
        Assert.Equal("SubResource_1", sub.GetProperty("id").GetString());
        Assert.Equal("Gradient", sub.GetProperty("type").GetString());
        var subProps = sub.GetProperty("properties");
        Assert.Equal("PackedFloat32Array", subProps.GetProperty("offsets").GetProperty("type").GetString());
        Assert.Equal(0.0, subProps.GetProperty("offsets").GetProperty("args")[0].GetDouble());
        Assert.Equal(1.0, subProps.GetProperty("offsets").GetProperty("args")[1].GetDouble());
        var colors = subProps.GetProperty("colors");
        Assert.Equal("PackedColorArray", colors.GetProperty("type").GetString());
        // Flat r,g,b,a quads: stop 1 (red) is args[4..8].
        Assert.Equal(8, colors.GetProperty("args").GetArrayLength());
        Assert.Equal(1.0, colors.GetProperty("args")[4].GetDouble()); // red R
        Assert.Equal(0.0, colors.GetProperty("args")[5].GetDouble()); // red G
    }

    [Fact]
    public void SerializeProducesGswGodotSceneStateShape()
    {
        // A 2-node tree (root + child) carrying one raw scalar, one engine-native value,
        // and an ExtResource ref — the union gsw's type guards + value accessors expect.
        var dto = new GodotSceneStateDto(
            Kind: "scene",
            Nodes: new List<GodotSceneStateNodeDto>
            {
                new(
                    Index: 0,
                    SiblingIndex: 0,
                    Name: "Root",
                    Type: "Control",
                    Parent: null,            // root -> omitted
                    Owner: null,
                    Instance: null,
                    InstancePlaceholder: null,
                    Groups: new[] { "ui" },
                    Properties: new List<GodotOrderedPropertyDto>
                    {
                        new("visible", true),
                    }),
                new(
                    Index: 1,
                    SiblingIndex: 0,
                    Name: "Icon",
                    Type: "TextureRect",
                    Parent: ".",             // direct child of root
                    Owner: null,
                    Instance: Sts2GodotSceneStateEncoding.ExtResourceRef("res://scenes/icon.tscn"),
                    InstancePlaceholder: null,
                    Groups: System.Array.Empty<string>(),
                    Properties: new List<GodotOrderedPropertyDto>
                    {
                        new("position", Sts2GodotSceneStateEncoding.TypeArgs("Vector2", 1.0, 2.0)),
                        new("scale", 0.5),
                        new("texture", Sts2GodotSceneStateEncoding.ExtResourceRef("res://art/icon.png")),
                    }),
            },
            Connections: System.Array.Empty<GodotSceneStateConnectionDto>(),
            ExtResources: System.Array.Empty<object>(),
            SubResources: System.Array.Empty<object>(),
            EditableInstances: System.Array.Empty<string>(),
            BasePath: "res://scenes/card.tscn",
            Diagnostics: System.Array.Empty<GodotParseDiagnosticDto>());

        var bytes = Sts2GodotSceneStateEncoding.Serialize(dto);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
        var root = doc.RootElement;

        // Top-level gsw GodotSceneState shape (camelCase keys, required arrays present).
        Assert.Equal("scene", root.GetProperty("kind").GetString());
        Assert.Equal("res://scenes/card.tscn", root.GetProperty("basePath").GetString());
        foreach (var key in new[] { "nodes", "connections", "extResources", "subResources", "editableInstances", "diagnostics" })
        {
            Assert.Equal(JsonValueKind.Array, root.GetProperty(key).ValueKind);
        }

        var nodes = root.GetProperty("nodes");
        Assert.Equal(2, nodes.GetArrayLength());

        // Root: parent omitted (undefined => gsw treats as scene root), groups present.
        var rootNode = nodes[0];
        Assert.Equal("Root", rootNode.GetProperty("name").GetString());
        Assert.False(rootNode.TryGetProperty("parent", out _));
        Assert.False(rootNode.TryGetProperty("instance", out _));
        Assert.Equal(JsonValueKind.True, rootNode.GetProperty("properties")[0].GetProperty("value").ValueKind);

        // Child: bare parent ".", instance ExtResource path-first, and a properties array
        // of {name,value} carrying a raw scalar + an engine-native {type,args}.
        var child = nodes[1];
        Assert.Equal(".", child.GetProperty("parent").GetString());
        Assert.Equal("ExtResource", child.GetProperty("instance").GetProperty("type").GetString());
        Assert.Equal("res://scenes/icon.tscn", child.GetProperty("instance").GetProperty("path").GetString());

        var props = child.GetProperty("properties");
        var position = props[0];
        Assert.Equal("position", position.GetProperty("name").GetString());
        Assert.Equal("Vector2", position.GetProperty("value").GetProperty("type").GetString());
        Assert.Equal(1.0, position.GetProperty("value").GetProperty("args")[0].GetDouble());

        var scale = props[1];
        Assert.Equal(JsonValueKind.Number, scale.GetProperty("value").ValueKind); // raw scalar, not type-tagged
        Assert.Equal(0.5, scale.GetProperty("value").GetDouble());

        var texture = props[2];
        Assert.Equal("ExtResource", texture.GetProperty("value").GetProperty("type").GetString());
        Assert.Equal("res://art/icon.png", texture.GetProperty("value").GetProperty("path").GetString());
    }

    [Fact]
    public void SerializeResourceProducesGswGodotResourceShape()
    {
        // A FontVariation document as the resource producer emits it: `kind:"resource"`,
        // header carrying the Godot class, and a KEYED properties map (not the ordered array
        // scene nodes use) with base_font as a path-first ExtResource ref + a raw scalar.
        var dto = new GodotResourceDto(
            Kind: "resource",
            Type: "FontVariation",
            Header: new GodotResourceHeaderDto(
                "gd_resource",
                new Dictionary<string, object?> { ["type"] = "FontVariation" }),
            ExtResources: System.Array.Empty<object>(),
            SubResources: System.Array.Empty<object>(),
            Properties: new Dictionary<string, object?>
            {
                ["base_font"] = Sts2GodotSceneStateEncoding.ExtResourceRef("res://fonts/kreon_bold.ttf"),
                ["spacing_glyph"] = 2,
            },
            Diagnostics: System.Array.Empty<GodotParseDiagnosticDto>());

        var bytes = Sts2GodotSceneStateEncoding.Serialize(dto);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
        var root = doc.RootElement;

        Assert.Equal("resource", root.GetProperty("kind").GetString());
        Assert.Equal("FontVariation", root.GetProperty("type").GetString());
        Assert.Equal("FontVariation", root.GetProperty("header").GetProperty("attributes").GetProperty("type").GetString());
        foreach (var key in new[] { "extResources", "subResources", "diagnostics" })
        {
            Assert.Equal(JsonValueKind.Array, root.GetProperty(key).ValueKind);
        }

        // properties is a keyed object; base_font is a path-first ExtResource, spacing_glyph raw.
        var props = root.GetProperty("properties");
        Assert.Equal(JsonValueKind.Object, props.ValueKind);
        var baseFont = props.GetProperty("base_font");
        Assert.Equal("ExtResource", baseFont.GetProperty("type").GetString());
        Assert.Equal("res://fonts/kreon_bold.ttf", baseFont.GetProperty("path").GetString());
        Assert.False(baseFont.TryGetProperty("id", out _));
        Assert.Equal(JsonValueKind.Number, props.GetProperty("spacing_glyph").ValueKind);
        Assert.Equal(2, props.GetProperty("spacing_glyph").GetInt32());
    }
}
