using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// Round-8 items 8 + 13 (RUNTIME-INJECTED spine skeletons: the boss map point and the treasure chest — the two
// nodes whose `.tscn` carries no `skeleton_data_res`, so their scene-addressed clip render 404s and the producer
// sees an empty animation list when it first inspects them). Pure, Godot-free cores of:
//   * the late-static re-probe budget/re-arm gate (Sts2SpineReprobeGate), and
//   * the shader-material discriminator that keeps a shaded clip from colliding with an unshaded (or
//     differently-tinted) one under the same (scene, node, anim, skin, skel) address (Sts2SpineMaterialKey).
public sealed class Sts2SpineRuntimeSkeletonTests
{
    private const int MaxAttempts = 30;

    // THE item-13 REGRESSION. The chest's skeleton is injected from `NTreasureRoom._Ready`
    // (`MegaSprite.SetSkeletonDataRes`), which can be many ticks after the watcher first tracks the node. The old
    // gate spent one attempt per TICK while the animation list was empty, so 30 ticks of "still no skeleton"
    // exhausted the budget and the node then reported 0 animations + no skelResPath FOREVER — both clients bail
    // without an animation name, and the `&skel=` bake fallback never learns the skeleton path. Waiting must be
    // free and unbounded.
    [Fact]
    public void Decide_WaitingForARuntimeSkeleton_NeverBurnsBudget()
    {
        var lastId = 0UL;
        var attempts = MaxAttempts;

        for (var tick = 0; tick < MaxAttempts * 10; tick += 1)
        {
            var decision = Sts2SpineReprobeGate.Decide(0, lastId, attempts, MaxAttempts);
            Assert.False(decision.ShouldInspect);
            Assert.False(decision.ReArmed);
            lastId = decision.SkeletonProbeId;
            attempts = decision.AttemptsLeft;
        }

        Assert.Equal(MaxAttempts, attempts);

        // …and the tick the game finally assigns the skeleton still probes.
        var injected = Sts2SpineReprobeGate.Decide(4242, lastId, attempts, MaxAttempts);
        Assert.True(injected.ShouldInspect);
        Assert.True(injected.ReArmed);
        Assert.Equal(4242UL, injected.SkeletonProbeId);
        Assert.Equal(MaxAttempts - 1, injected.AttemptsLeft);
    }

    // Once a skeleton IS assigned, the retries (for a resource that is assigned but not yet queryable) stay
    // bounded exactly as before — the gate must not turn into an unbounded per-tick InspectStatic.
    [Fact]
    public void Decide_AfterInjection_BoundsRetriesToTheBudget()
    {
        var lastId = 0UL;
        var attempts = MaxAttempts;
        var inspects = 0;

        for (var tick = 0; tick < MaxAttempts * 3; tick += 1)
        {
            var decision = Sts2SpineReprobeGate.Decide(77, lastId, attempts, MaxAttempts);
            if (decision.ShouldInspect)
            {
                inspects += 1;
            }

            lastId = decision.SkeletonProbeId;
            attempts = decision.AttemptsLeft;
        }

        Assert.Equal(MaxAttempts, inspects);
        Assert.Equal(0, attempts);
    }

    // A SWAP to a different skeleton (a new act's chest reusing the node) re-arms the budget even after it was
    // fully spent, so the new skeleton gets its own full set of retries.
    [Fact]
    public void Decide_SkeletonSwap_ReArmsAnExhaustedBudget()
    {
        var decision = Sts2SpineReprobeGate.Decide(99, lastSkeletonInstanceId: 77, attemptsLeft: 0, MaxAttempts);

        Assert.True(decision.ReArmed);
        Assert.True(decision.ShouldInspect);
        Assert.Equal(99UL, decision.SkeletonProbeId);
        Assert.Equal(MaxAttempts - 1, decision.AttemptsLeft);
    }

    // ── Material discriminator (#8) ───────────────────────────────────────────────────────────────────

    // A node with NO shader material reports no key, so its clip URL/cache key stays byte-identical to today.
    [Fact]
    public void MaterialKey_WithoutAShaderPath_IsNull()
    {
        Assert.Null(Sts2SpineMaterialKey.Compute(null, []));
        Assert.Null(Sts2SpineMaterialKey.Compute("   ", [("map_color", "1,0,0,1")]));
    }

    // THE item-8 COLLISION the discriminator exists to prevent: the boss map point's clip is addressed by
    // (scene, node, anim, skin, skel) only, so an UNSHADED bake and a shaded one — and act 1's tint vs act 2's,
    // and untraveled vs traveled — all used to land on the same cache key.
    [Fact]
    public void MaterialKey_DiscriminatesShadedFromUnshadedAndTintFromTint()
    {
        const string shader = "res://shaders/boss_map_point.gdshader";
        var untraveled = Sts2SpineMaterialKey.Compute(
            shader,
            [("map_color", "0.671,0.58,0.478,1"), ("black_layer_color", "0,0,0,1")]);
        var traveled = Sts2SpineMaterialKey.Compute(
            shader,
            [("map_color", "0.671,0.58,0.478,1"), ("black_layer_color", "0.33,0.29,0.24,1")]);
        var otherShader = Sts2SpineMaterialKey.Compute(
            "res://shaders/vfx/boss_map_point_unavailable.gdshader",
            [("map_color", "0.671,0.58,0.478,1"), ("black_layer_color", "0,0,0,1")]);

        Assert.NotNull(untraveled);
        Assert.NotEqual(untraveled, traveled);      // same shader, runtime-retinted → distinct clips
        Assert.NotEqual(untraveled, otherShader);   // different shader → distinct clips
        Assert.Null(Sts2SpineMaterialKey.Compute(null, []));  // …and unshaded is distinct from all of them
    }

    // The key rides an ON-DISK cache key, so it must be deterministic (no per-process salt) and independent of
    // the order Godot happens to enumerate the uniform list in.
    [Fact]
    public void MaterialKey_IsStableAndOrderInsensitive()
    {
        const string shader = "res://shaders/boss_map_point.gdshader";
        var forward = Sts2SpineMaterialKey.Compute(
            shader, [("black_layer_color", "0,0,0,1"), ("map_color", "0.671,0.58,0.478,1")]);
        var reversed = Sts2SpineMaterialKey.Compute(
            shader, [("map_color", "0.671,0.58,0.478,1"), ("black_layer_color", "0,0,0,1")]);

        Assert.Equal(forward, reversed);
        Assert.Equal(forward, Sts2SpineMaterialKey.Compute(shader, [("black_layer_color", "0,0,0,1"), ("map_color", "0.671,0.58,0.478,1")]));
        Assert.Equal(16, forward!.Length);
        Assert.Matches("^[0-9a-f]{16}$", forward);
    }

    // ── R9 LIVE-UNIFORM CARRY (Sts2SpineLiveMaterialStore) ────────────────────────────────────────────
    //
    // Round-8 shipped the discriminator above (the `&mat=` key hashes the LIVE node's uniform values) but rendered
    // through a material read off an OFFLINE `PackedScene.Instantiate()`. `boss_map_point.tscn` declares
    // `resource_local_to_scene = true` on that ShaderMaterial, so each instantiate gets a private copy at the
    // AUTHORED values — key live, pixels authored. The store below closes that gap: the inspector records the very
    // uniform values it hashed, and the bake resolves them back by the same signature.

    // The carry is exact BY CONSTRUCTION: what comes back for a signature is what was hashed into it, so a clip can
    // never be rendered at one tint and cached under another's key.
    [Fact]
    public void LiveMaterials_ResolveReturnsTheSnapshotTheSignatureWasComputedFrom()
    {
        const string shader = "res://shaders/boss_map_point.gdshader";
        // The values NBossMapPoint.RefreshColorInstantly pushes on the Hive map, untraveled (MapBgColor 9B9562 /
        // MapUntraveledColor 6E7750) — NOT the .tscn's authored 0.671,0.58,0.478 / 0,0,0.
        (string Name, string Value)[] live =
        [
            ("map_color", "0.60784316,0.58431375,0.38431373,1"),
            ("black_layer_color", "0.43137255,0.46666667,0.3137255,1"),
        ];
        var signature = Sts2SpineMaterialKey.Compute(shader, live);

        var store = new Sts2SpineLiveMaterialStore<string>();
        Assert.True(store.Record(signature, shader, live));

        var resolved = store.Resolve(signature);
        Assert.NotNull(resolved);
        Assert.Equal(shader, resolved!.ShaderResPath);
        Assert.Equal(live, resolved.Uniforms);

        // …and the AUTHORED tint hashes to a different signature, which the store has never seen — so a bake asked
        // for it falls back to the offline material instead of silently serving the live one.
        var authored = Sts2SpineMaterialKey.Compute(
            shader, [("map_color", "0.671,0.58,0.478,1"), ("black_layer_color", "0,0,0,1")]);
        Assert.NotEqual(signature, authored);
        Assert.Null(store.Resolve(authored));
    }

    // A miss must be a clean null (→ the caller falls back to the offline material), never a throw or a wrong hit.
    [Fact]
    public void LiveMaterials_UnrecordableInputsAndUnknownSignaturesMiss()
    {
        var store = new Sts2SpineLiveMaterialStore<string>();

        Assert.False(store.Record(null, "res://a.gdshader", [("x", "1")]));
        Assert.False(store.Record("  ", "res://a.gdshader", [("x", "1")]));
        Assert.False(store.Record("sig", null, [("x", "1")]));
        Assert.False(store.Record("sig", "res://a.gdshader", null));
        Assert.False(store.Record("sig", "res://a.gdshader", []));   // no uniforms ⇒ nothing to carry
        Assert.Equal(0, store.Count);

        Assert.Null(store.Resolve(null));
        Assert.Null(store.Resolve(""));
        Assert.Null(store.Resolve("0123456789abcdef"));
    }

    // Re-reading the SAME signature every tick (the steady state — the inspector reads the material on each spine
    // tick) must not grow the store or churn eviction order.
    [Fact]
    public void LiveMaterials_RecordingAKnownSignatureIsIdempotent()
    {
        var store = new Sts2SpineLiveMaterialStore<string>(capacity: 2);
        (string Name, string Value)[] uniforms = [("map_color", "1,0,0,1")];

        for (var tick = 0; tick < 50; tick += 1)
        {
            Assert.True(store.Record("sig-a", "res://a.gdshader", uniforms));
        }

        Assert.Equal(1, store.Count);
        Assert.NotNull(store.Resolve("sig-a"));
    }

    // Bounded: a hypothetical churny material (capped per node at MaxSignatureChanges, but several nodes can each
    // contribute) drops the OLDEST identities rather than growing without limit. A dropped identity degrades to the
    // offline material — the round-8 behaviour — never to a WRONG tint.
    [Fact]
    public void LiveMaterials_EvictsOldestBeyondCapacity()
    {
        var store = new Sts2SpineLiveMaterialStore<string>(capacity: 3);
        for (var i = 0; i < 5; i += 1)
        {
            store.Record($"sig-{i}", "res://a.gdshader", [("map_color", $"{i},0,0,1")]);
        }

        Assert.Equal(3, store.Count);
        Assert.Null(store.Resolve("sig-0"));
        Assert.Null(store.Resolve("sig-1"));
        Assert.NotNull(store.Resolve("sig-2"));
        Assert.NotNull(store.Resolve("sig-4"));
        Assert.Equal("4,0,0,1", store.Resolve("sig-4")!.Uniforms[0].Value);
    }
}
