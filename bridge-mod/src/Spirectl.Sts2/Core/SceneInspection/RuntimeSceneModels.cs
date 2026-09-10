namespace Spirectl.Sts2.Core.SceneInspection;

public sealed record RuntimeSceneNodeSnapshot(
    string NodeId,
    string NodePath,
    string Name,
    string NodeType,
    string? ParentNodePath,
    string? OwnerPath,
    string? SceneFilePath,
    string? AttachedScriptPath,
    string? AttachedScriptType,
    int ChildCount,
    IReadOnlyList<string> Notes,
    RuntimeSceneNodePropertiesSnapshot? Properties = null,
    RuntimeSceneComputedTransformSnapshot? ComputedTransform = null,
    string? NativeNodeType = null);

public sealed record RuntimeTransitionBlockerSnapshot(
    string Kind,
    string NodePath,
    string NodeType,
    string Name,
    string Status,
    string Reason,
    string? AnimationName = null,
    int? LoopsLeft = null,
    bool? Running = null,
    bool? Infinite = null);

public sealed record RuntimeSceneNodePropertiesSnapshot(
    bool? Visible,
    bool? EffectiveVisible,
    RuntimeSceneVector2Snapshot? Position,
    RuntimeSceneVector2Snapshot? GlobalPosition,
    RuntimeSceneVector2Snapshot? Scale,
    double? RotationRadians,
    RuntimeSceneVector2Snapshot? Size,
    RuntimeSceneVector2Snapshot? PivotOffset,
    RuntimeSceneAnchorsSnapshot? Anchors,
    RuntimeSceneOffsetsSnapshot? Offsets,
    int? ZIndex,
    RuntimeSceneColorSnapshot? Modulate,
    RuntimeSceneColorSnapshot? SelfModulate,
    RuntimeSceneColorSnapshot? EffectiveModulate,
    bool? ClipContents,
    RuntimeSceneTextureRectPropertiesSnapshot? TextureRect,
    RuntimeSceneMaterialPropertiesSnapshot? Material,
    RuntimeSceneLayoutPropertiesSnapshot? Layout,
    IReadOnlyList<RuntimeSceneResourceRefSnapshot> TextureRefs,
    RuntimeSceneTextPropertiesSnapshot? Text,
    RuntimeSceneNinePatchPropertiesSnapshot? NinePatch,
    IReadOnlyList<RuntimeScenePropertyNoticeSnapshot> Notices,
    bool? ShowBehindParent = null,
    bool? ZAsRelative = null,
    int? ClipChildrenMode = null,
    int? MouseFilter = null,
    int? FocusMode = null,
    int? MouseDefaultCursorShape = null);

public sealed record RuntimeSceneTextPropertiesSnapshot(
    string? Text,
    string? RawText,
    bool? RichTextEnabled,
    string Source,
    string DiagnosticSurface,
    RuntimeSceneResourceRefSnapshot? Font,
    double? FontSize,
    double? LineHeight,
    RuntimeSceneColorSnapshot? TextColor,
    RuntimeSceneColorSnapshot? OutlineColor,
    double? OutlineSize,
    RuntimeSceneTextShadowSnapshot? Shadow,
    IReadOnlyList<RuntimeSceneRichTextSpanSnapshot> RichTextSpans,
    IReadOnlyList<RuntimeScenePropertyNoticeSnapshot> Notices,
    double? LetterSpacing = null,
    string? FontWeight = null,
    string? FontStyle = null,
    RuntimeSceneTextLayoutSnapshot? Layout = null,
    RuntimeSceneTextRecipeSnapshot? Recipe = null,
    string? FontSizeSource = null,
    double? AppliedFontSize = null,
    double? ThemeFontSize = null,
    double? ConfiguredMinFontSize = null,
    double? ConfiguredMaxFontSize = null,
    RuntimeSceneTextRenderedMetricsSnapshot? RenderedMetrics = null);

public sealed record RuntimeSceneTextRecipeSnapshot(
    string Source,
    bool? AutoSizeEnabled,
    double? MinFontSizePx,
    double? MaxFontSizePx,
    double? NominalFontSizePx,
    bool? RichTextEnabled,
    string? WrapMode,
    string? BreakFlags,
    string? JustificationFlags,
    string? TextOverrunBehavior,
    bool? HorizontallyBound = null,
    bool? VerticallyBound = null);

public sealed record RuntimeSceneTextRenderedMetricsSnapshot(
    string MetricSource,
    double? FontAscentPx,
    double? FontDescentPx,
    double? FontHeightPx,
    double? ParagraphSizeWidthPx,
    double? ParagraphSizeHeightPx,
    IReadOnlyList<RuntimeSceneTextRenderedLineMetricsSnapshot> Lines,
    /// <summary>
    /// WHICH STRING the lines' <c>RangeStart</c>/<c>RangeEnd</c> index into — <c>"text"</c> for the node's own
    /// text, <c>"parsed"</c> for <see cref="ParsedText"/>, and null when no line carries a range.
    /// </summary>
    /// <remarks>
    /// This field exists because guessing it is a wrong-words bug rather than a wrong-number one. A
    /// <c>RichTextLabel</c>'s <c>Text</c> is its BBCODE, and its line ranges index into the MARKUP-STRIPPED
    /// string; slicing the bbcode at those offsets yields text that is not merely misplaced but not what the
    /// game says. A consumer that cannot resolve the named basis must lay the label out itself.
    /// </remarks>
    string? RangeBasis = null,
    /// <summary>
    /// The node's markup-stripped text (<c>RichTextLabel.get_parsed_text</c>), when it has one and differs from
    /// its <c>Text</c>. This is the string a <c>"parsed"</c> basis addresses.
    /// </summary>
    string? ParsedText = null,
    /// <summary>
    /// The length and a stable 32-bit FNV-1a hash of the exact string the ranges were computed against.
    /// </summary>
    /// <remarks>
    /// THE STALENESS SEAM, and it is load-bearing rather than defensive. A node's text is streamed on the
    /// PER-TICK (lean) path and these ranges are produced on the STATIC (non-lean) one, so a label whose words
    /// change without a static re-describe would carry ranges describing the string it used to hold. Slicing the
    /// new text at the old offsets is a WRONG-WORDS failure — the one failure a text path must never ship — and
    /// it would be silent.
    ///
    /// So the ranges are not self-evidently valid and must not be treated as such: a consumer recomputes the
    /// same hash over the string it is about to draw and uses the ranges only on a match, falling back to its own
    /// line breaking otherwise. Both halves of the length-and-hash pair are carried because the length alone is a
    /// weak discriminator (an HP counter changes digits without changing length) and the hash alone gives a
    /// reader no cheap first check.
    /// </remarks>
    int? RangeSourceLength = null,
    int? RangeSourceHash = null);

/// <summary>
/// One laid-out line of a text node, as the engine's own shaper produced it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RangeStart"/>/<see cref="RangeEnd"/> are the half-open character range this line covers in the
/// node's source string — Godot's <c>get_line_range(line)</c>, which is the result of
/// <c>TextServer.shaped_text_get_line_breaks</c> run with the node's own width, autowrap flags, justification
/// flags and overrun behaviour. They are the ONLY description of a wrap that does not require re-deriving it:
/// a consumer that slices the string at these offsets reproduces the engine's line breaking exactly, including
/// the cases a space-breaking heuristic cannot reach (unbroken scripts, tab stops, trimmed overruns).
/// </para>
/// <para>
/// Both are null when the probe could not obtain a range — an older engine, a node type that does not expose
/// one, or a paragraph the probe declined to build. A consumer must treat "no ranges" as "lay it out yourself"
/// and never as "one line".
/// </para>
/// </remarks>
public sealed record RuntimeSceneTextRenderedLineMetricsSnapshot(
    int Index,
    double? ParagraphAscentPx,
    double? ParagraphDescentPx,
    double? ParagraphLineSizeWidthPx,
    double? ParagraphLineSizeHeightPx,
    double? ParagraphLineWidthPx,
    int? RangeStart = null,
    int? RangeEnd = null);

public sealed record RuntimeSceneTextLayoutSnapshot(
    string? HorizontalAlignment,
    string? VerticalAlignment,
    double? BaselineOffsetPx,
    double? AscentPx,
    double? DescentPx,
    double? LineHeightPx,
    double? ContentWidthPx,
    double? ContentHeightPx,
    bool? ClipContents,
    IReadOnlyList<RuntimeSceneTextLineSnapshot> Lines);

public sealed record RuntimeSceneTextLineSnapshot(
    int Index,
    string? Text,
    double? X,
    double? Y,
    double? BaselineY,
    double? Width,
    double? Height,
    int? Start,
    int? End);

public sealed record RuntimeSceneTextShadowSnapshot(
    RuntimeSceneColorSnapshot? Color,
    RuntimeSceneVector2Snapshot? Offset,
    double? Size,
    double? OutlineSize,
    string Source,
    IReadOnlyList<RuntimeSceneStackedTextShadowSnapshot> StackedShadows);

public sealed record RuntimeSceneStackedTextShadowSnapshot(
    int Index,
    RuntimeSceneColorSnapshot? Color,
    RuntimeSceneVector2Snapshot? Offset,
    double? OutlineSize,
    string Source);

public sealed record RuntimeSceneRichTextSpanSnapshot(
    string Tag,
    string Text,
    RuntimeSceneColorSnapshot? Color);

public sealed record RuntimeSceneComputedTransformSnapshot(
    RuntimeSceneTransform2DSnapshot? LocalTransform,
    RuntimeSceneTransform2DSnapshot? GlobalTransform,
    RuntimeSceneRect2Snapshot? GlobalRect,
    RuntimeSceneRect2Snapshot? ViewportClippedRect,
    IReadOnlyList<RuntimeScenePropertyNoticeSnapshot> Notices);

public sealed record RuntimeSceneVector2Snapshot(double X, double Y);

// Html is the #RRGGBBAA convenience string. Nullable because the live scene watcher DEFERS it: a per-tick volatile
// color leaves it null (the change test uses numeric channels) and it is populated only when the node is emitted.
// Every serialized/consumed color has it populated; only transient in-watcher cache entries carry null.
public sealed record RuntimeSceneColorSnapshot(double R, double G, double B, double A, string? Html);

public sealed record RuntimeScenePatchMarginsSnapshot(
    double Left,
    double Top,
    double Right,
    double Bottom);

public sealed record RuntimeSceneNinePatchPropertiesSnapshot(
    RuntimeSceneResourceRefSnapshot? Texture,
    bool DrawCenter,
    RuntimeScenePatchMarginsSnapshot PatchMargins,
    string AxisStretchHorizontal,
    string AxisStretchVertical,
    RuntimeSceneColorSnapshot EffectiveModulate);

public sealed record RuntimeSceneTextureRectPropertiesSnapshot(
    string StretchMode,
    string ExpandMode,
    bool FlipH,
    bool FlipV);

public sealed record RuntimeSceneMaterialPropertiesSnapshot(
    RuntimeSceneResourceRefSnapshot? Material,
    bool? UseParentMaterial,
    RuntimeSceneResourceRefSnapshot? Shader,
    IReadOnlyList<RuntimeSceneShaderParameterSnapshot> ShaderParameters,
    string? BlendMode = null);

public sealed record RuntimeSceneThemeConstantSnapshot(
    string Name,
    double? Value);

public sealed record RuntimeSceneLayoutPropertiesSnapshot(
    RuntimeSceneVector2Snapshot? MinimumSize,
    RuntimeSceneVector2Snapshot? CombinedMinimumSize,
    RuntimeSceneVector2Snapshot? CustomMinimumSize,
    int? SizeFlagsHorizontal,
    int? SizeFlagsVertical,
    double? SizeFlagsStretchRatio,
    string? LayoutDirection,
    string? ThemeTypeVariation,
    int? ContainerAlignment,
    bool? FlowVertical,
    IReadOnlyList<RuntimeSceneThemeConstantSnapshot> ThemeConstants);

public sealed record RuntimeSceneShaderParameterSnapshot(
    string Name,
    string ValueKind,
    string? StringValue,
    double? NumberValue,
    bool? BoolValue,
    RuntimeSceneColorSnapshot? ColorValue,
    RuntimeSceneVector2Snapshot? Vector2Value,
    RuntimeSceneResourceRefSnapshot? ResourceValue);

public sealed record RuntimeSceneTransform2DSnapshot(
    RuntimeSceneVector2Snapshot XAxis,
    RuntimeSceneVector2Snapshot YAxis,
    RuntimeSceneVector2Snapshot Origin);

public sealed record RuntimeSceneRect2Snapshot(
    RuntimeSceneVector2Snapshot Position,
    RuntimeSceneVector2Snapshot Size);

public sealed record RuntimeSceneAnchorsSnapshot(
    double? Left,
    double? Top,
    double? Right,
    double? Bottom);

public sealed record RuntimeSceneOffsetsSnapshot(
    double? Left,
    double? Top,
    double? Right,
    double? Bottom);

public sealed record RuntimeSceneResourceRefSnapshot(
    string Field,
    string ResourcePath,
    string ResourceType,
    string ResourceName);

// One ShaderMaterial uniform value, so a browser client can run the real shader (Godot streams only the
// shader/material resource path otherwise). `Kind` is one of number/bool/string/color/vector2/resource — plus
// the Godot-native-first extended kinds vector3/vector4/rect2/transform2d and the *Array kinds
// (vector3Array/vector4Array/vector2Array/floatArray/intArray). The matching value field is populated;
// `Resource` carries a sampler texture's path/type. A raw Godot client re-hydrates every kind; the web
// consumes number/bool/string/color/vector2/resource and IGNORES the extended kinds (additive-optional wire).
public sealed record RuntimeSceneShaderParamSnapshot(
    string Name,
    string Kind,
    double? Number = null,
    bool? Bool = null,
    string? String = null,
    RuntimeSceneColorSnapshot? Color = null,
    RuntimeSceneVector2Snapshot? Vector2 = null,
    RuntimeSceneResourceRefSnapshot? Resource = null,
    RuntimeSceneVector3Snapshot? Vector3 = null,
    RuntimeSceneVector4Snapshot? Vector4 = null,
    RuntimeSceneShaderRectSnapshot? Rect2 = null,
    // Transform2D as a 6-tuple [a,b,c,d,tx,ty] — same column-major (xAxis, yAxis, origin) convention as the
    // node `transform` field, so a native client applies it directly.
    IReadOnlyList<double>? Transform2D = null,
    // Flattened numeric array uniform (row-major); the element stride is implied by `Kind`.
    IReadOnlyList<double>? NumberArray = null,
    // A `resource` sampler uniform that is a PROCEDURAL ramp, resolved to its authored data so a client can
    // reproduce the shader without fetching (and decoding) the baked texture. Set alongside `Resource`, never
    // instead of it — a client that ignores these still gets the path.
    //
    // `GradientStops` = a Gradient / GradientTexture1D|2D (STS2's VFX `lut` uniform: the particle shaders do
    // `COLOR = vec4(texture(lut, texture_color.rr).rgb, erosion) * vertex_color`, i.e. a per-TEXEL colour lookup
    // indexed by the source texture's RED channel — nothing about it is derivable from the sampler path alone).
    // `GradientInterpolation` mirrors Godot `Gradient.interpolation_mode` and is omitted for the default 0
    // (LINEAR); 1 = CONSTANT (hold each stop, i.e. nearest), 2 = CUBIC. NOTE: a `use_hdr` GradientTexture1D can
    // hold out-of-range channels; the stops are the AUTHORED values, so nothing is clipped here.
    // `CurvePoints` = a Curve / CurveTexture sampler (the VFX `erosion_curve` / `flipbook_curve` uniforms).
    IReadOnlyList<RuntimeSceneParticleGradientStopSnapshot>? GradientStops = null,
    int? GradientInterpolation = null,
    IReadOnlyList<RuntimeSceneParticleCurvePointSnapshot>? CurvePoints = null);

public sealed record RuntimeSceneVector3Snapshot(double X, double Y, double Z);

public sealed record RuntimeSceneVector4Snapshot(double X, double Y, double Z, double W);

// A Rect2 uniform as the flat {x, y, width, height} shape (distinct from RuntimeSceneRect2Snapshot's
// nested position/size, which the local-rect/region pipeline uses).
public sealed record RuntimeSceneShaderRectSnapshot(double X, double Y, double Width, double Height);

// One authored stop of a particle over-life Gradient (color_ramp / color_initial_ramp). `Offset` is 0..1.
public sealed record RuntimeSceneParticleGradientStopSnapshot(double Offset, RuntimeSceneColorSnapshot Color);

// One authored point of a particle over-life Curve (scale/alpha/hue). `X` is the normalized lifetime 0..1.
public sealed record RuntimeSceneParticleCurvePointSnapshot(double X, double Y);

// A GpuParticles2D/CpuParticles2D system flattened into the same shape gsw's ParticleSpecConfig consumes, so
// the browser client can run the real deterministic CPU simulation. Probed ONCE on add (static): the shape /
// velocity / forces / texture / curves never change at runtime for STS2 particles. `Emitting` rides separately
// as a volatile delta field. Values are in Godot units (px / degrees / seconds); ramps/curves are null when the
// material has none (gsw treats absent as constant).
// A SpineSprite's canonical clip address + available animations, probed once on add. The client
// fetches a rendered clip via `spine://{SceneResPath sans res://}?node={NodePath}&anim={name}`; the
// producer also streams the live current animation + track time (volatile, on the node delta) so the
// client can sync playback. (SceneResPath, NodePath) is the canonical (scene, scene-relative path)
// identity — shared across the mirror + structured views and every reuse of the same scene. NodePath
// null => the SpineSprite is the scene root (or the sole/first SpineSprite in the scene).
public sealed record RuntimeSceneSpineSnapshot(
    string SceneResPath,
    string? NodePath,
    IReadOnlyList<string> Animations,
    // The res:// path of the node's skeleton-data resource (#8), captured on add. Lets the client retry a failed
    // scene-addressed clip fetch with `&skel=<res-path>` so the bake can Load the skeleton directly and render a
    // standalone lane — the fix for a dynamically-spawned spine (the treasure chest) whose scene/node address
    // won't resolve offline. Null when the node exposes no readable skeleton resource path (→ client omits
    // `&skel=`, keeping today's URL byte-identical). Appended with a default so existing construction stays valid.
    string? SkelResPath = null);

public sealed record RuntimeSceneParticleSpecSnapshot(
    string Kind,
    double Amount,
    double AmountRatio,
    double Lifetime,
    double LifetimeRandomness,
    bool OneShot,
    double Explosiveness,
    double Randomness,
    double Preprocess,
    double SpeedScale,
    double FixedFps,
    bool LocalCoords,
    int DrawOrder,
    long Seed,
    int EmissionShape,
    RuntimeSceneVector2Snapshot EmissionOffset,
    RuntimeSceneVector2Snapshot EmissionScale,
    double EmissionSphereRadius,
    double EmissionRingRadius,
    double EmissionRingInnerRadius,
    double EmissionRingHeight,
    RuntimeSceneVector2Snapshot EmissionBoxExtents,
    RuntimeSceneVector2Snapshot Direction,
    double Spread,
    double InitialVelocityMin,
    double InitialVelocityMax,
    double AngleMin,
    double AngleMax,
    double AngularVelocityMin,
    double AngularVelocityMax,
    RuntimeSceneVector2Snapshot Gravity,
    double LinearAccelMin,
    double LinearAccelMax,
    double RadialAccelMin,
    double RadialAccelMax,
    double TangentialAccelMin,
    double TangentialAccelMax,
    double DampingMin,
    double DampingMax,
    bool DampingAsFriction,
    double OrbitVelocityMin,
    double OrbitVelocityMax,
    double ScaleMin,
    double ScaleMax,
    double HueVariationMin,
    double HueVariationMax,
    bool AlignY,
    RuntimeSceneColorSnapshot BaseColor,
    double OriginX,
    double OriginY,
    RuntimeSceneResourceRefSnapshot? Texture,
    double TextureWidth,
    double TextureHeight,
    int Hframes,
    int Vframes,
    bool AnimLoop,
    double AnimSpeedMin,
    double AnimSpeedMax,
    double AnimOffsetMin,
    double AnimOffsetMax,
    int BlendMode,
    IReadOnlyList<RuntimeSceneParticleGradientStopSnapshot>? ColorRamp,
    IReadOnlyList<RuntimeSceneParticleGradientStopSnapshot>? ColorInitialRamp,
    IReadOnlyList<RuntimeSceneParticleCurvePointSnapshot>? ScaleCurve,
    IReadOnlyList<RuntimeSceneParticleCurvePointSnapshot>? ScaleCurveX,
    IReadOnlyList<RuntimeSceneParticleCurvePointSnapshot>? ScaleCurveY,
    IReadOnlyList<RuntimeSceneParticleCurvePointSnapshot>? AlphaCurve,
    IReadOnlyList<RuntimeSceneParticleCurvePointSnapshot>? HueCurve);

public sealed record RuntimeScenePropertyNoticeSnapshot(
    string Code,
    string Field,
    string Message);

public sealed record RuntimeSceneHoverLabelSnapshot(
    string NodePath,
    string Name,
    string Text);

public sealed record RuntimeSceneHoverTipSnapshot(
    bool Visible,
    string? NodePath,
    string? Title,
    string? Text,
    IReadOnlyList<RuntimeSceneHoverLabelSnapshot> Labels,
    IReadOnlyList<string> Notes);
