using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Embedding;

namespace Spirectl.Sts2.Live;

/// <summary>
/// The generic "native VFX backbone": one Harmony hook captures EVERY game VFX spawn. All 150+
/// <c>N*Vfx</c> classes load a <c>res://…/*.tscn</c> and are attached to the tree through the single
/// extension method <c>GodotTreeExtensions.AddChildSafely</c> — the universal choke point. We postfix
/// it, keep only children in the game's <c>…Nodes.Vfx</c> namespace, read the scene each instances from
/// <c>Node.SceneFilePath</c> (set by Godot on a PackedScene's root), and publish a <c>vfx</c> event
/// naming that scene. A host folds the stream into <c>transient_effects</c> (kind="vfx", scene_path) and
/// the renderer mounts the captured scene — its particles/shaders animate natively, so no per-effect
/// proto/bridge/catalog work is needed to add a new effect.
///
/// <para><c>AddChildSafely</c> is a hot, fully-generic helper (every AddChild in the game routes through
/// it), so the postfix early-outs on the namespace check and never blocks (a bounded-channel write per
/// subscriber). A telemetry failure must never disrupt the game.</para>
///
/// <para>First-cut anchor resolution is best-effort: we reflect for a <c>Creature</c>-typed field on the
/// VFX and map it to the renderer creature id. When none is found the effect is centered. Precise
/// anchoring/positioning and per-VFX flood control are live-tuning follow-ups (the authored-fixture path
/// validates the render with explicit anchors without needing the live stream).</para>
/// </summary>
internal static class Sts2VfxSpawnEventHooks
{
    private static readonly object Sync = new();
    private static bool _installed;

    // res:// scene ids are catalog-relative (strip the "res://scenes/" prefix + ".tscn"), matching the
    // presentation catalog's scene ids (e.g. "res://scenes/vfx/hit_spark_vfx.tscn" -> "vfx/hit_spark_vfx").
    private const string ScenePrefix = "res://scenes/";
    private const string SceneSuffix = ".tscn";

    // The game namespace every N*Vfx lives under (MegaCrit.Sts2.Core.Nodes.Vfx[.…]).
    private const string VfxNamespaceMarker = ".Nodes.Vfx";

    // Scenes already rendered by a DEDICATED transient path, excluded from the generic vfx stream so
    // they don't double-render: floating numbers (the damage/heal events → damage transient) and card
    // movement (the snapshot-diff cardMoves fold + cardUpgrade preview). Everything else (impacts,
    // power/debuff pops, slashes, …) flows through the generic backbone.
    private static readonly HashSet<string> DedicatedPathScenes = new(StringComparer.Ordinal)
    {
        "vfx/vfx_damage_num",      // damage transient
        "vfx/vfx_heal_num",        // heal number (damage-like)
        "vfx/vfx_card_fly",        // cardMoves (hand→discard fly)
        "vfx/vfx_card_power_fly",
        "vfx/vfx_card_shuffle_fly",
        "vfx/vfx_card_upgrade",    // cardUpgrade preview transient
    };

    // Per-character card-trail scenes (vfx/card_trail_<character>) ride the card-movement path too.
    private const string CardTrailPrefix = "vfx/card_trail_";

    private static bool IsDedicatedPathScene(string scenePath)
        => DedicatedPathScenes.Contains(scenePath)
            || scenePath.StartsWith(CardTrailPrefix, StringComparison.Ordinal);

    public static bool IsInstalled
    {
        get
        {
            lock (Sync)
            {
                return _installed;
            }
        }
    }

    public static void Install(ILogStream logStream)
    {
        lock (Sync)
        {
            if (_installed)
            {
                return;
            }

            Sts2MonoModNativeDependencies.EnsureLoaded(logStream);
            // Arm the subscriber gate every capture postfix in this file early-outs on.
            Sts2CombatEventCapture.EnsureArmed();

            var target = typeof(GodotTreeExtensions).GetMethod(
                nameof(GodotTreeExtensions.AddChildSafely),
                BindingFlags.Public | BindingFlags.Static);
            var postfix = typeof(Sts2VfxSpawnEventHooks).GetMethod(
                nameof(AddChildSafelyPostfix),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (target is null || postfix is null)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.combat.vfx-events",
                    "Unable to install VFX-spawn hook; GodotTreeExtensions.AddChildSafely was not found.");
                return;
            }

            try
            {
                var harmony = new Harmony("spirectl.combat-vfx-events");
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            }
            catch (Exception ex)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.combat.vfx-events",
                    $"Skipping VFX-spawn hook because Harmony patching failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            _installed = true;
            logStream.Write(
                BridgeLogLevel.Info,
                "bridge.combat.vfx-events",
                "Installed VFX-spawn hook; live combat VFX now stream over WatchCombatEvents.");
        }
    }

    // Harmony binds by name to GodotTreeExtensions.AddChildSafely(this Node parent, Node? child).
    private static void AddChildSafelyPostfix(Node parent, Node? child)
    {
        // THE FIRST STATEMENT, and it must stay first. AddChildSafely is the game's generic add-child
        // helper, so this postfix runs on EVERY node attach in the process — the single hottest thing the
        // bridge instruments. With nobody watching WatchCombatEvents, all of it below is waste: a
        // GetType(), a namespace scan, a scene-path resolve, a creature walk, a deferred Callable
        // allocation and the hub's fan-out lock. A volatile bool read is what an unmodded game should pay.
        if (!Sts2CombatEventCapture.Active)
        {
            return;
        }

        try
        {
            if (child is null)
            {
                return;
            }

            var type = child.GetType();
            // Fast early-out: AddChildSafely is the game's generic add-child helper, so this postfix runs
            // on every node attach. Only VFX nodes are of interest.
            if (type.Namespace is not { } ns || !ns.Contains(VfxNamespaceMarker, StringComparison.Ordinal))
            {
                return;
            }

            var scenePath = ResolveScenePath(child);
            if (scenePath is null || IsDedicatedPathScene(scenePath))
            {
                return;
            }

            var spawnedAt = DateTimeOffset.UtcNow;

            // Exact anchor: a VFX that holds its target creature (a Creature / NCreature field) resolves now.
            var anchor = ResolveCreatureFieldAnchor(child);
            if (anchor is not null)
            {
                EmbeddableCombatEventHub.Shared.PublishVfx(scenePath, anchor, amount: 0, spawnedAt);
                return;
            }

            // Position-only VFX (a bare Sprite2D/Node2D positioned by its spawner, e.g. WithHitFx slashes):
            // the spawner sets GlobalPosition AFTER attaching, so it isn't readable in this postfix yet.
            // Resolve the nearest creature on the next idle frame — the game's own deferred-VFX pattern
            // (NCreature.OnPowerRemoved et al.) — once the spawn position has been applied.
            if (child is Node2D node2d)
            {
                Callable.From(() =>
                {
                    try
                    {
                        var deferredAnchor = GodotObject.IsInstanceValid(node2d)
                            ? ResolveNearestCreatureId(node2d.GlobalPosition)
                            : null;
                        EmbeddableCombatEventHub.Shared.PublishVfx(scenePath, deferredAnchor, amount: 0, spawnedAt);
                    }
                    catch
                    {
                        // VFX telemetry must never disrupt the game.
                    }
                }).CallDeferred();
                return;
            }

            // Non-positioned VFX (full-screen overlays, etc.): centered.
            EmbeddableCombatEventHub.Shared.PublishVfx(scenePath, anchorCreatureId: null, amount: 0, spawnedAt);
        }
        catch
        {
            // VFX telemetry must never disrupt the game.
        }
    }

    // The scene a VFX instances, as a catalog scene id. Godot sets Node.SceneFilePath on the root of a
    // node instanced from a PackedScene; we reach it by reflection to avoid a hard Godot-version coupling.
    private static string? ResolveScenePath(Node child)
    {
        var raw = typeof(Node).GetProperty("SceneFilePath")?.GetValue(child) as string;
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        var path = raw;
        if (path.StartsWith(ScenePrefix, StringComparison.Ordinal))
        {
            path = path[ScenePrefix.Length..];
        }

        if (path.EndsWith(SceneSuffix, StringComparison.Ordinal))
        {
            path = path[..^SceneSuffix.Length];
        }

        return string.IsNullOrEmpty(path) ? null : path;
    }

    // Exact renderer creature id when the VFX HOLDS its target: impact/status VFX keep either a Creature or
    // (more commonly) the NCreature node they spawned over in a `_creatureNode`-style field; we resolve both
    // (NCreature.Entity is the Creature). Null => no creature reference (the caller falls back to the spawn
    // position) or a genuinely centered effect.
    private static string? ResolveCreatureFieldAnchor(Node child)
    {
        const BindingFlags flags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var current = child.GetType(); current is not null; current = current.BaseType)
        {
            foreach (var field in current.GetFields(flags))
            {
                var value = field.GetValue(child);
                var creature = value switch
                {
                    Creature direct => direct,
                    NCreature node => node.Entity,
                    _ => null,
                };
                if (creature is not null)
                {
                    return Sts2CombatIds.CreatureId(creature, 0);
                }
            }
        }

        return null;
    }

    // Godot world units. A VFX whose spawn point is farther than this from every creature is treated as a
    // genuinely centered/full-screen effect (anchor=null) rather than mis-anchored to a distant creature.
    private const float MaxAnchorDistanceSquared = 600f * 600f;

    // Nearest live combat creature to a spawn position, as a renderer creature id (or null when none is
    // within MaxAnchorDistanceSquared / not in combat). Compares against each NCreature.VfxSpawnPosition,
    // the same point the game uses to place these VFX.
    private static string? ResolveNearestCreatureId(Vector2 position)
    {
        var creatures = NCombatRoom.Instance?.CreatureNodes;
        if (creatures is null)
        {
            return null;
        }

        Creature? nearest = null;
        var nearestDistanceSquared = MaxAnchorDistanceSquared;
        foreach (var node in creatures)
        {
            if (node?.Entity is not { } entity)
            {
                continue;
            }

            var distanceSquared = position.DistanceSquaredTo(node.VfxSpawnPosition);
            if (distanceSquared <= nearestDistanceSquared)
            {
                nearestDistanceSquared = distanceSquared;
                nearest = entity;
            }
        }

        return nearest is null ? null : Sts2CombatIds.CreatureId(nearest, 0);
    }
}
