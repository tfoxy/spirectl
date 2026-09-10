namespace Spirectl.Sts2.Core.SceneInspection;

/// <summary>
/// The published split of <see cref="RuntimeSceneNodeDelta"/> into the fields the scene watcher emits on
/// EVERY delta and the fields it emits only on an add or a keyframe.
/// </summary>
/// <remarks>
/// <para>This exists because embedders re-project the delta and have to know the difference. A consumer that
/// keeps only the volatile fields between keyframes drops a node's name, fonts and anchors; a consumer that
/// treats a static field as volatile reads the record's DEFAULT (null / false / 0) as "the game changed it to
/// nothing" on every non-keyframe tick and un-styles the node. Both mistakes are silent, and both were being
/// avoided by hand-copied field lists in downstream repos that no test could hold to this one.</para>
///
/// <para>The split is defined by one thing and nothing else: whether the argument the watcher passes for that
/// field is gated on its <c>includeStatic</c> flag. <see cref="StaticFieldNames"/> is exactly the gated set;
/// <see cref="VolatileFieldNames"/> is exactly the rest, so the two are disjoint and their union is every
/// property on the record. <c>RuntimeSceneNodeDeltaFieldsTests</c> asserts both halves against the record's
/// reflected property list AND against the watcher's own source text, so adding a field to the record without
/// classifying it here fails the build's test leg rather than shipping an unclassified field.</para>
///
/// <para>Two shadings the names alone do not carry. A handful of "volatile" fields are STICKY rather than
/// per-tick — the watcher ships them when their value changes and relies on the consumer's merge to carry them
/// forward in between (intent frames, the Line2D stroke unit) — which is a superset of volatile behaviour and
/// safe for a consumer that simply keeps every volatile field. And four legacy fields
/// (<c>ScaleX</c>/<c>ScaleY</c>/<c>PivotX</c>/<c>PivotY</c>) are not passed at all any more: they ride the
/// record's default on every emission, which is ungated, so they sit on the volatile side.</para>
/// </remarks>
public static class RuntimeSceneNodeDeltaFields
{
    /// <summary>
    /// Fields the watcher emits ONLY on an add or a keyframe (its <c>includeStatic</c> block). A consumer must
    /// retain the last non-default value it saw for each of these and ignore the record default in between.
    /// </summary>
    public static readonly IReadOnlyList<string> StaticFieldNames =
    [
        "Name",
        "NodeType",
        "ShowBehindParent",
        "ClipChildren",
        "ClipContents",
        "MouseFilter",
        "NinePatchMargins",
        "Font",
        "Shadow",
        "RichText",
        "Material",
        "Shader",
        "FontWeight",
        "FontStyle",
        "TextureStretchMode",
        "TextureFlipH",
        "TextureFlipV",
        "CanvasBlendMode",
        "ParticleSpec",
        "Spine",
        "SceneFilePath",
        "AnchorLeft",
        "AnchorRight",
        "AnchorOwnerId",
        "ContainerLayout",
        "ContentKey",
        "RichBoldFont",
        "RichItalicFont",
        "RichBoldItalicFont",
        "RichBoldFontSizePx",
        "RichItalicFontSizePx",
        "RichBoldItalicFontSizePx",
        "RichBoldFontSpacingPx",
        "RichItalicFontSpacingPx",
        "RichBoldItalicFontSpacingPx",
        "TextLineRanges",
        "TextLineBasis",
        "TextParsedText",
        "TextLineSourceLength",
        "TextLineSourceHash",
    ];

    /// <summary>
    /// Fields the watcher passes ungated, i.e. on every emitted delta (identity, placement, per-tick styling,
    /// and the sticky units described in the type remarks).
    /// </summary>
    public static readonly IReadOnlyList<string> VolatileFieldNames =
    [
        "Id",
        "ParentId",
        "Rect",
        "Visible",
        "Opacity",
        "ZIndex",
        "Rotation",
        "Texture",
        "NinePatch",
        "Text",
        "Modulate",
        "SelfModulate",
        "ScaleX",
        "ScaleY",
        "PivotX",
        "PivotY",
        "FillColor",
        "RangeValue",
        "RangeMin",
        "RangeMax",
        "OutlineColor",
        "OutlineSize",
        "ShaderParameters",
        "Transform",
        "LocalRect",
        "TextureRegion",
        "TextureMargin",
        "ParticleEmitting",
        "ParticleRestartEpoch",
        "SpineCurrentAnim",
        "SpineTrackTime",
        "SpineLooping",
        "SpineSkin",
        "SpineMat",
        "SpinePaused",
        "IntentFrames",
        "PinnedLoopAnim",
        "LinePoints",
        "LineWidth",
        "LineColor",
        "Focused",
    ];
}
