using Godot;
using Spirectl.Sts2.Core.SceneInspection;

namespace Spirectl.Sts2.Live;

// Flattens a live GpuParticles2D / CpuParticles2D into the same shape gsw's ParticleSpecConfig consumes, so a
// browser client can run the real deterministic CPU simulation rather than approximating the VFX with CSS. The
// sibling of Sts2ShaderMaterialInspector.
//
// Reads the particle fields via Godot's snake_case `Resource.Get("…")`/`Node.Get("…")` rather than the C# binding
// property names: the engine accepts the snake_case names verified against gsw's build-time reader (visual-2d.ts),
// which sidesteps C#-binding-name drift across the many fields. GPU fields come from the ParticleProcessMaterial;
// CPU has no process material, so the SAME fields live on the node — with three name differences (scale_amount_*,
// scale_amount_curve, emission_rect_extents) and Vector2 (not Vector3) gravity/direction/extents.
internal static class Sts2ParticleInspector
{
    public static RuntimeSceneParticleSpecSnapshot? Inspect(Node node)
    {
        try
        {
            return node switch
            {
                GpuParticles2D gpu => DescribeGpu(gpu),
                CpuParticles2D cpu => DescribeCpu(cpu),
                _ => null,
            };
        }
        catch
        {
            // Any reflection/Godot access failure → no particle spec (the node still renders without VFX).
            return null;
        }
    }

    private static RuntimeSceneParticleSpecSnapshot DescribeGpu(GpuParticles2D gpu)
    {
        var ppm = gpu.ProcessMaterial as ParticleProcessMaterial;

        // Material readers via snake_case Get (null material → gsw-equivalent defaults).
        double MD(string name, double dflt) => ppm is null ? dflt : ppm.Get(name).AsDouble();
        bool MB(string name, bool dflt) => ppm is null ? dflt : ppm.Get(name).AsBool();
        int MI(string name, int dflt) => ppm is null ? dflt : (int)ppm.Get(name).AsInt64();
        Vector2 MV3(string name, Vector2 dflt)
        {
            if (ppm is null)
            {
                return dflt;
            }
            var v = ppm.Get(name).AsVector3();
            return new Vector2(v.X, v.Y);
        }
        GodotObject? MO(string name) => ppm?.Get(name).AsGodotObject();

        var texture = gpu.Texture;
        RuntimeSceneResourceRefSnapshot? textureRef = null;
        double texWidth = 0, texHeight = 0;
        if (texture is not null)
        {
            textureRef = ToResourceRef("ParticleTexture", texture);
            try
            {
                var size = texture.GetSize();
                texWidth = size.X;
                texHeight = size.Y;
            }
            catch
            {
                // Size unavailable — gsw falls back to the default dot size.
            }
        }

        var (blendMode, hframes, vframes, animLoop) = ReadCanvasMaterial(gpu.Material);

        var baseColor = ppm is null ? Colors.White : ppm.Get("color").AsColor();
        var seed = ReadSeed(gpu, "GPUParticles2D", textureRef?.ResourcePath);

        return new RuntimeSceneParticleSpecSnapshot(
            Kind: "GPUParticles2D",
            Amount: gpu.Amount,
            AmountRatio: gpu.AmountRatio,
            Lifetime: gpu.Lifetime,
            LifetimeRandomness: MD("lifetime_randomness", 0),
            OneShot: gpu.OneShot,
            Explosiveness: gpu.Explosiveness,
            Randomness: gpu.Randomness,
            Preprocess: gpu.Preprocess,
            SpeedScale: gpu.SpeedScale,
            FixedFps: gpu.FixedFps,
            LocalCoords: gpu.LocalCoords,
            DrawOrder: (int)gpu.DrawOrder,
            Seed: seed,
            EmissionShape: MI("emission_shape", 0),
            EmissionOffset: ToVec2(MV3("emission_shape_offset", Vector2.Zero)),
            EmissionScale: ToVec2(MV3("emission_shape_scale", Vector2.One)),
            EmissionSphereRadius: MD("emission_sphere_radius", 0),
            EmissionRingRadius: MD("emission_ring_radius", 0),
            EmissionRingInnerRadius: MD("emission_ring_inner_radius", 0),
            EmissionRingHeight: MD("emission_ring_height", 0),
            EmissionBoxExtents: ToVec2(MV3("emission_box_extents", Vector2.Zero)),
            Direction: ToVec2(MV3("direction", new Vector2(1, 0))),
            Spread: MD("spread", 0),
            InitialVelocityMin: MD("initial_velocity_min", 0),
            InitialVelocityMax: MD("initial_velocity_max", 0),
            AngleMin: MD("angle_min", 0),
            AngleMax: MD("angle_max", 0),
            AngularVelocityMin: MD("angular_velocity_min", 0),
            AngularVelocityMax: MD("angular_velocity_max", 0),
            Gravity: ToVec2(MV3("gravity", new Vector2(0, 98))),
            LinearAccelMin: MD("linear_accel_min", 0),
            LinearAccelMax: MD("linear_accel_max", 0),
            RadialAccelMin: MD("radial_accel_min", 0),
            RadialAccelMax: MD("radial_accel_max", 0),
            TangentialAccelMin: MD("tangential_accel_min", 0),
            TangentialAccelMax: MD("tangential_accel_max", 0),
            DampingMin: MD("damping_min", 0),
            DampingMax: MD("damping_max", 0),
            DampingAsFriction: MB("particle_flag_damping_as_friction", false),
            OrbitVelocityMin: MD("orbit_velocity_min", 0),
            OrbitVelocityMax: MD("orbit_velocity_max", 0),
            ScaleMin: MD("scale_min", 1),
            ScaleMax: MD("scale_max", 1),
            HueVariationMin: MD("hue_variation_min", 0),
            HueVariationMax: MD("hue_variation_max", 0),
            AlignY: MB("particle_flag_align_y", false),
            BaseColor: ToColor(baseColor),
            OriginX: 0,
            OriginY: 0,
            Texture: textureRef,
            TextureWidth: texWidth,
            TextureHeight: texHeight,
            Hframes: hframes,
            Vframes: vframes,
            AnimLoop: animLoop,
            AnimSpeedMin: MD("anim_speed_min", 0),
            AnimSpeedMax: MD("anim_speed_max", 0),
            AnimOffsetMin: MD("anim_offset_min", 0),
            AnimOffsetMax: MD("anim_offset_max", 0),
            BlendMode: blendMode,
            ColorRamp: ExtractGradient(MO("color_ramp")),
            ColorInitialRamp: ExtractGradient(MO("color_initial_ramp")),
            ScaleCurve: ExtractCurve(MO("scale_curve")),
            ScaleCurveX: null,
            ScaleCurveY: null,
            AlphaCurve: ExtractCurve(MO("alpha_curve")),
            HueCurve: ExtractCurve(MO("hue_variation_curve")));
    }

    // CpuParticles2D has NO ParticleProcessMaterial — every field lives on the node, read via snake_case Get. The
    // field set matches GPU except: scale_amount_min/max (vs scale_min/max), scale_amount_curve (a bare Curve, vs
    // scale_curve CurveTexture), emission_rect_extents (Vector2, vs emission_box_extents Vector3), gravity/direction
    // are Vector2 (vs Vector3), color_ramp/color_initial_ramp are bare Gradients, baseColor is the node `color`, and
    // there is no emission_shape_offset/scale (default 0 / 1).
    private static RuntimeSceneParticleSpecSnapshot DescribeCpu(CpuParticles2D cpu)
    {
        double ND(string name, double dflt) { var v = cpu.Get(name); return v.VariantType == Variant.Type.Nil ? dflt : v.AsDouble(); }
        bool NB(string name, bool dflt) { var v = cpu.Get(name); return v.VariantType == Variant.Type.Nil ? dflt : v.AsBool(); }
        int NI(string name, int dflt) { var v = cpu.Get(name); return v.VariantType == Variant.Type.Nil ? dflt : (int)v.AsInt64(); }
        Vector2 NV2(string name, Vector2 dflt) { var v = cpu.Get(name); return v.VariantType == Variant.Type.Nil ? dflt : v.AsVector2(); }
        GodotObject? NO(string name) => cpu.Get(name).AsGodotObject();

        var texture = cpu.Texture;
        RuntimeSceneResourceRefSnapshot? textureRef = null;
        double texWidth = 0, texHeight = 0;
        if (texture is not null)
        {
            textureRef = ToResourceRef("ParticleTexture", texture);
            try
            {
                var size = texture.GetSize();
                texWidth = size.X;
                texHeight = size.Y;
            }
            catch
            {
                // Size unavailable — gsw falls back to the default dot size.
            }
        }

        var (blendMode, hframes, vframes, animLoop) = ReadCanvasMaterial(cpu.Material);
        var seed = ReadSeed(cpu, "CPUParticles2D", textureRef?.ResourcePath);

        return new RuntimeSceneParticleSpecSnapshot(
            Kind: "CPUParticles2D",
            Amount: cpu.Amount,
            AmountRatio: ND("amount_ratio", 1),
            Lifetime: cpu.Lifetime,
            LifetimeRandomness: ND("lifetime_randomness", 0),
            OneShot: cpu.OneShot,
            Explosiveness: cpu.Explosiveness,
            Randomness: cpu.Randomness,
            Preprocess: cpu.Preprocess,
            SpeedScale: cpu.SpeedScale,
            FixedFps: cpu.FixedFps,
            LocalCoords: cpu.LocalCoords,
            DrawOrder: (int)cpu.DrawOrder,
            Seed: seed,
            EmissionShape: NI("emission_shape", 0),
            EmissionOffset: ToVec2(Vector2.Zero),
            EmissionScale: ToVec2(Vector2.One),
            EmissionSphereRadius: ND("emission_sphere_radius", 0),
            EmissionRingRadius: ND("emission_ring_radius", 0),
            EmissionRingInnerRadius: ND("emission_ring_inner_radius", 0),
            EmissionRingHeight: ND("emission_ring_height", 0),
            EmissionBoxExtents: ToVec2(NV2("emission_rect_extents", Vector2.Zero)),
            Direction: ToVec2(NV2("direction", new Vector2(1, 0))),
            Spread: ND("spread", 0),
            InitialVelocityMin: ND("initial_velocity_min", 0),
            InitialVelocityMax: ND("initial_velocity_max", 0),
            AngleMin: ND("angle_min", 0),
            AngleMax: ND("angle_max", 0),
            AngularVelocityMin: ND("angular_velocity_min", 0),
            AngularVelocityMax: ND("angular_velocity_max", 0),
            Gravity: ToVec2(NV2("gravity", new Vector2(0, 98))),
            LinearAccelMin: ND("linear_accel_min", 0),
            LinearAccelMax: ND("linear_accel_max", 0),
            RadialAccelMin: ND("radial_accel_min", 0),
            RadialAccelMax: ND("radial_accel_max", 0),
            TangentialAccelMin: ND("tangential_accel_min", 0),
            TangentialAccelMax: ND("tangential_accel_max", 0),
            DampingMin: ND("damping_min", 0),
            DampingMax: ND("damping_max", 0),
            DampingAsFriction: NB("damping_as_friction", false),
            OrbitVelocityMin: ND("orbit_velocity_min", 0),
            OrbitVelocityMax: ND("orbit_velocity_max", 0),
            ScaleMin: ND("scale_amount_min", 1),
            ScaleMax: ND("scale_amount_max", 1),
            HueVariationMin: ND("hue_variation_min", 0),
            HueVariationMax: ND("hue_variation_max", 0),
            AlignY: NB("particle_flag_align_y", false),
            BaseColor: ToColor(cpu.Color),
            OriginX: 0,
            OriginY: 0,
            Texture: textureRef,
            TextureWidth: texWidth,
            TextureHeight: texHeight,
            Hframes: hframes,
            Vframes: vframes,
            AnimLoop: animLoop,
            AnimSpeedMin: ND("anim_speed_min", 0),
            AnimSpeedMax: ND("anim_speed_max", 0),
            AnimOffsetMin: ND("anim_offset_min", 0),
            AnimOffsetMax: ND("anim_offset_max", 0),
            BlendMode: blendMode,
            ColorRamp: ExtractGradient(NO("color_ramp")),
            ColorInitialRamp: ExtractGradient(NO("color_initial_ramp")),
            ScaleCurve: ExtractCurve(NO("scale_amount_curve")),
            ScaleCurveX: null,
            ScaleCurveY: null,
            AlphaCurve: ExtractCurve(NO("alpha_curve")),
            HueCurve: ExtractCurve(NO("hue_variation_curve")));
    }

    // CanvasItem.Material → (blendMode 0 mix/1 add, hframes, vframes, animLoop). A CanvasItemMaterial carries the
    // blend mode + flipbook directly; an additive VFX ShaderMaterial (the grayscale-particle shader) is detected
    // by a `blend_add` render_mode in its shader code (gsw has no per-particle fragment shader — Phase 4b accepts
    // the additive textured-quad approximation).
    private static (int BlendMode, int Hframes, int Vframes, bool AnimLoop) ReadCanvasMaterial(Material? material)
    {
        if (material is CanvasItemMaterial cim)
        {
            var blend = cim.BlendMode == CanvasItemMaterial.BlendModeEnum.Add ? 1 : 0;
            if (cim.ParticlesAnimation)
            {
                return (blend, Math.Max(1, cim.ParticlesAnimHFrames), Math.Max(1, cim.ParticlesAnimVFrames), cim.ParticlesAnimLoop);
            }

            return (blend, 1, 1, false);
        }

        if (material is ShaderMaterial { Shader: { } shader })
        {
            try
            {
                var code = shader.Code ?? string.Empty;
                if (code.Contains("blend_add", StringComparison.OrdinalIgnoreCase))
                {
                    return (1, 1, 1, false);
                }
            }
            catch
            {
                // Shader code unreadable — default to mix.
            }
        }

        return (0, 1, 1, false);
    }

    // Stable seed: the node's fixed seed when enabled, else an FNV-1a hash of identity strings (deterministic
    // across keyframes for the same node, matching gsw's hashString fallback).
    private static long ReadSeed(Node node, string kind, string? texturePath)
    {
        try
        {
            if (node.Get("use_fixed_seed").AsBool())
            {
                return node.Get("seed").AsInt64();
            }
        }
        catch
        {
            // No fixed-seed support — fall through to the hash.
        }

        return FnvHash($"{node.Name}|{kind}|{texturePath}");
    }

    private static long FnvHash(string s)
    {
        uint hash = 2166136261;
        foreach (var c in s)
        {
            hash ^= c;
            hash *= 16777619;
        }

        return hash;
    }

    // Ramp unwrapping lives in Sts2RampExtractor — the same Gradient/Curve resources also arrive as ShaderMaterial
    // sampler uniforms (the VFX `lut`), so the scene watcher's shader-parameter path shares this implementation.
    private static IReadOnlyList<RuntimeSceneParticleCurvePointSnapshot>? ExtractCurve(GodotObject? obj)
        => Sts2RampExtractor.ExtractCurvePoints(obj);

    private static IReadOnlyList<RuntimeSceneParticleGradientStopSnapshot>? ExtractGradient(GodotObject? obj)
        => Sts2RampExtractor.ExtractGradientStops(obj);

    private static RuntimeSceneVector2Snapshot ToVec2(Vector2 v) => new(v.X, v.Y);

    private static RuntimeSceneColorSnapshot ToColor(Color c) => new(c.R, c.G, c.B, c.A, $"#{c.ToHtml(includeAlpha: true)}");

    private static RuntimeSceneResourceRefSnapshot ToResourceRef(string field, Resource resource)
        => new(
            Field: field,
            ResourcePath: string.IsNullOrWhiteSpace(resource.ResourcePath) ? string.Empty : resource.ResourcePath,
            ResourceType: resource.GetType().FullName ?? resource.GetType().Name,
            ResourceName: resource.ResourceName.ToString());
}
