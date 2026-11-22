using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using Spirectl.Sts2.Embedding;

namespace Spirectl.Sts2.Live;

// Kept separate from animation hints: raw timing must not change their existing endpoint fold.
internal static class Sts2TweenScheduleHooks
{
    private static readonly ConditionalWeakTable<Tween, TweenScheduleRecord> Records = new();
    private static readonly ConditionalWeakTable<Tweener, TweenScheduleRecord.Step> Steps = new();
    private static readonly TweenScheduleRegistry Registry = new();
    private static EmbeddableTweenScheduleHub Hub => EmbeddableTweenScheduleHub.Shared;

    internal static void Install()
    {
        var harmony = new Harmony("spirectl.tween-schedules");
        Patch(typeof(Tween), nameof(Tween.TweenProperty), nameof(Property));
        Patch(typeof(Tween), nameof(Tween.TweenInterval), nameof(Interval));
        Patch(typeof(Tween), nameof(Tween.TweenCallback), nameof(Callback));
        Patch(typeof(Tween), nameof(Tween.TweenMethod), nameof(Method));
        Patch(typeof(Tween), nameof(Tween.SetParallel), nameof(SetParallel));
        Patch(typeof(Tween), nameof(Tween.Parallel), nameof(Parallel));
        Patch(typeof(Tween), nameof(Tween.Chain), nameof(Chain));
        Patch(typeof(Tween), nameof(Tween.SetLoops), nameof(Loops));
        Patch(typeof(Tween), nameof(Tween.SetTrans), nameof(DefaultTrans));
        Patch(typeof(Tween), nameof(Tween.SetEase), nameof(DefaultEase));
        Patch(typeof(Tween), nameof(Tween.Kill), nameof(Kill));
        Patch(typeof(Tween), nameof(Tween.Stop), nameof(Stop));
        Patch(typeof(Tween), nameof(Tween.Pause), nameof(Pause));
        Patch(typeof(Tween), nameof(Tween.SetSpeedScale), nameof(SpeedScale));
        Patch(typeof(Engine), nameof(Engine.SetTimeScale), nameof(TimeScale));
        Patch(typeof(PropertyTweener), nameof(PropertyTweener.From), nameof(From));
        Patch(typeof(PropertyTweener), nameof(PropertyTweener.FromCurrent), nameof(FromCurrent));
        Patch(typeof(PropertyTweener), nameof(PropertyTweener.AsRelative), nameof(Relative));
        Patch(typeof(PropertyTweener), nameof(PropertyTweener.SetTrans), nameof(Trans));
        Patch(typeof(PropertyTweener), nameof(PropertyTweener.SetEase), nameof(Ease));
        foreach (var type in new[] { typeof(PropertyTweener), typeof(IntervalTweener), typeof(CallbackTweener), typeof(MethodTweener) })
            if (type.GetMethod("SetDelay") is not null) Patch(type, "SetDelay", nameof(Delay));
        Patch(typeof(MethodTweener), nameof(MethodTweener.SetTrans), nameof(Trans));
        Patch(typeof(MethodTweener), nameof(MethodTweener.SetEase), nameof(Ease));
        void Patch(Type type, string name, string postfix) => harmony.Patch(type.GetMethod(name),
            postfix: new HarmonyMethod(typeof(Sts2TweenScheduleHooks).GetMethod(postfix, BindingFlags.NonPublic | BindingFlags.Static)));
    }

    private static TweenScheduleRecord Record(Tween tween)
    {
        if (Records.TryGetValue(tween, out var record)) return record;
        record = new TweenScheduleRecord(Time.GetTicksUsec());
        Records.Add(tween, record);
        // The native completion signal also retires announcer bookkeeping without a timer.
        tween.Finished += () =>
        {
            try { record.End(Hub, TweenEndReason.Finished); }
            catch { /* Observation must never interrupt the game. */ }
        };
        Callable.From(() =>
        {
            try { record.Publish(Hub, Engine.TimeScale, Registry); }
            catch { /* Observation must never interrupt the game. */ }
        }).CallDeferred();
        return record;
    }

    private static void Property(Tween __instance, PropertyTweener __result, GodotObject @object,
        NodePath property, Variant finalVal, double duration)
    {
        if (!Hub.HasSubscribers) return;
        try
        {
            var step = Record(__instance).Append(duration * 1000);
            step.Property = property.ToString();
            step.ObjectId = @object.GetInstanceId();
            step.IsNode2D = @object is Node2D;
            step.To = Components(finalVal);
            step.Kind = ValueKind(finalVal);
            Steps.AddOrUpdate(__result, step);
        }
        catch { /* Observation must never interrupt the game. */ }
    }

    internal static TweenValueKind ValueKind(Variant value) => value.VariantType switch
    {
        Variant.Type.Int => TweenValueKind.Int,
        Variant.Type.Float => TweenValueKind.Float,
        Variant.Type.Vector2 => TweenValueKind.Vector2,
        Variant.Type.Vector3 => TweenValueKind.Vector3,
        Variant.Type.Vector4 => TweenValueKind.Vector4,
        Variant.Type.Color => TweenValueKind.Color,
        _ => TweenValueKind.Unknown
    };

    internal static IReadOnlyList<double> Components(Variant value) => value.VariantType switch
    {
        Variant.Type.Int => [value.AsInt64()],
        Variant.Type.Float => [value.AsDouble()],
        Variant.Type.Vector2 => [value.AsVector2().X, value.AsVector2().Y],
        Variant.Type.Vector3 => [value.AsVector3().X, value.AsVector3().Y, value.AsVector3().Z],
        Variant.Type.Vector4 => [value.AsVector4().X, value.AsVector4().Y, value.AsVector4().Z, value.AsVector4().W],
        Variant.Type.Color => [value.AsColor().R, value.AsColor().G, value.AsColor().B, value.AsColor().A],
        _ => []
    };

    private static void Interval(Tween __instance, IntervalTweener __result, double time) => Append(__instance, __result, time);
    private static void Method(Tween __instance, MethodTweener __result, double duration) => Append(__instance, __result, duration);
    private static void Callback(Tween __instance, CallbackTweener __result) => Append(__instance, __result, 0);
    private static void Append(Tween tween, Tweener result, double seconds)
    {
        if (!Hub.HasSubscribers) return;
        try { Steps.AddOrUpdate(result, Record(tween).Append(seconds * 1000)); } catch { }
    }
    private static void SetParallel(Tween __instance, bool parallel)
    {
        if (!Hub.HasSubscribers) return;
        try
        {
            var record = Record(__instance);
            record.DefaultParallel = record.PendingParallel = parallel;
        }
        catch { }
    }

    private static void Parallel(Tween __instance)
    {
        if (!Hub.HasSubscribers) return;
        try { Record(__instance).PendingParallel = true; } catch { }
    }

    private static void Chain(Tween __instance)
    {
        if (!Hub.HasSubscribers) return;
        try { Record(__instance).PendingParallel = false; } catch { }
    }

    private static void Loops(Tween __instance, int loops)
    {
        if (!Hub.HasSubscribers) return;
        try { Record(__instance).Loops = loops; } catch { }
    }

    private static void DefaultTrans(Tween __instance, Tween.TransitionType trans)
    {
        if (!Hub.HasSubscribers) return;
        try { Record(__instance).Trans = (int)trans; } catch { }
    }

    private static void DefaultEase(Tween __instance, Tween.EaseType ease)
    {
        if (!Hub.HasSubscribers) return;
        try { Record(__instance).Ease = (int)ease; } catch { }
    }

    private static void Trans(Tweener __instance, Tween.TransitionType trans)
    {
        if (Hub.HasSubscribers && Steps.TryGetValue(__instance, out var step)) step.Trans = (int)trans;
    }

    private static void Ease(Tweener __instance, Tween.EaseType ease)
    {
        if (Hub.HasSubscribers && Steps.TryGetValue(__instance, out var step)) step.Ease = (int)ease;
    }

    private static void Delay(Tweener __instance, double delay)
    {
        if (Hub.HasSubscribers && Steps.TryGetValue(__instance, out var step)) step.DelayMs = delay * 1000;
    }

    private static void From(PropertyTweener __instance, Variant value)
    {
        if (!Hub.HasSubscribers || !Steps.TryGetValue(__instance, out var step)) return;
        try { step.From = Components(value); } catch { }
    }

    private static void FromCurrent(PropertyTweener __instance)
    {
        if (Hub.HasSubscribers && Steps.TryGetValue(__instance, out var step)) step.From = null;
    }

    private static void Relative(PropertyTweener __instance)
    {
        if (Hub.HasSubscribers && Steps.TryGetValue(__instance, out var step)) step.Relative = true;
    }

    private static void TimeScale(double __0)
    {
        if (!Hub.HasSubscribers) return;
        try { Registry.TimeScaleChanged(Hub, __0); } catch { }
    }

    private static void Kill(Tween __instance) => End(__instance, TweenEndReason.Killed);
    private static void Stop(Tween __instance) => End(__instance, TweenEndReason.Stopped);
    private static void Pause(Tween __instance) => End(__instance, TweenEndReason.Paused);
    private static void SpeedScale(Tween __instance, float speed)
    {
        if (speed != 1) End(__instance, TweenEndReason.Retimed);
    }

    private static void End(Tween tween, TweenEndReason reason)
    {
        if (!Hub.HasSubscribers) return;
        try { Record(tween).End(Hub, reason); } catch { }
    }
}
