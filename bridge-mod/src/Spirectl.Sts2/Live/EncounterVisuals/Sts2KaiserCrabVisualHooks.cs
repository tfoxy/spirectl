using HarmonyLib;
using Spirectl.Sts2.Core.Logging;
using System.Reflection;

namespace Spirectl.Sts2.Live.EncounterVisuals;

public sealed class Sts2KaiserCrabVisualHooks
{
    private const string PackageId = "composed://encounters/kaiser_crab_boss/scene-package";
    private const string TargetTypeName = "NKaiserCrabBossBackground";

    private readonly Sts2EncounterVisualEventStore _events;
    private readonly ILogStream _logStream;
    private readonly Harmony _harmony;
    private bool _installed;
    private static Sts2EncounterVisualEventStore? s_events;

    public Sts2KaiserCrabVisualHooks(Sts2EncounterVisualEventStore events, ILogStream logStream)
    {
        _events = events;
        _logStream = logStream;
        _harmony = new Harmony("spirectl.encounter-visuals.kaiser-crab");
    }

    public void Install()
    {
        if (_installed)
        {
            return;
        }

        Sts2MonoModNativeDependencies.EnsureLoaded(_logStream);

        try
        {
            var targetType = FindTargetType();
            PatchAll(targetType);
            _installed = true;
            _logStream.Write(
                BridgeLogLevel.Info,
                "bridge.encounter-visuals",
                "Installed Kaiser Crab encounter visual transition hooks.");
        }
        catch (Exception ex)
        {
            _logStream.Write(
                BridgeLogLevel.Warn,
                "bridge.encounter-visuals",
                $"Kaiser Crab encounter visual hooks were not installed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void InstallForTest(Type targetType)
    {
        if (_installed)
        {
            return;
        }

        PatchAll(targetType);
        _installed = true;
    }

    private void PatchAll(Type targetType)
    {
        s_events = _events;
        Patch(targetType, "PlayHurtAnim", nameof(RecordHurt));
        Patch(targetType, "PlayArmDeathAnim", nameof(RecordArmDeath));
        Patch(targetType, "PlayRightSideChargeUpAnim", nameof(RecordRocketChargeUp));
        Patch(targetType, "PlayRightSideHeavy", nameof(RecordRocketHeavy));
        Patch(targetType, "PlayRightRecharge", nameof(RecordRocketRecharge));
        Patch(targetType, "PlayBodyDeathAnim", nameof(RecordBodyDeath));
    }

    private void Patch(Type targetType, string targetMethodName, string postfixMethodName)
    {
        var targets = targetType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => string.Equals(method.Name, targetMethodName, StringComparison.Ordinal))
            .ToArray();
        var target = targets.Length switch
        {
            1 => targets[0],
            0 => throw new MissingMethodException(targetType.FullName, targetMethodName),
            _ => throw new AmbiguousMatchException(
                $"Found {targets.Length} overloads for {targetType.FullName}.{targetMethodName}; Kaiser Crab visual hooks require a single target method."),
        };
        var postfix = typeof(Sts2KaiserCrabVisualHooks)
            .GetMethod(postfixMethodName, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(targetType.FullName, targetMethodName);
        _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
    }

    private static Type FindTargetType()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetTypes()
                .FirstOrDefault(candidate => string.Equals(candidate.Name, TargetTypeName, StringComparison.Ordinal));
            if (type is not null)
            {
                return type;
            }
        }

        throw new InvalidOperationException($"Could not find runtime type '{TargetTypeName}'.");
    }

    private static void RecordHurt(object __0)
    {
        var side = NormalizeSide(__0);
        if (side == "left")
        {
            Record("hurt-left", ["crusher"], "hurt-left", $"{TargetTypeName}.PlayHurtAnim");
        }
        else
        {
            Record("hurt-right", ["rocket"], "hurt-right", $"{TargetTypeName}.PlayHurtAnim");
        }
    }

    private static void RecordArmDeath(object __0)
    {
        var side = NormalizeSide(__0);
        if (side == "left")
        {
            Record("arm-death-left", ["crusher"], "arm-death-left", $"{TargetTypeName}.PlayArmDeathAnim");
        }
        else
        {
            Record("arm-death-right", ["rocket"], "arm-death-right", $"{TargetTypeName}.PlayArmDeathAnim");
        }
    }

    private static void RecordRocketChargeUp()
    {
        Record("rocket-charge-up", ["rocket"], "rocket-charge-up", $"{TargetTypeName}.PlayRightSideChargeUpAnim");
    }

    private static void RecordRocketHeavy()
    {
        Record("rocket-heavy", ["rocket"], "rocket-heavy", $"{TargetTypeName}.PlayRightSideHeavy");
    }

    private static void RecordRocketRecharge()
    {
        Record("rocket-recharge", ["rocket"], "rocket-recharge", $"{TargetTypeName}.PlayRightRecharge");
    }

    private static void RecordBodyDeath()
    {
        Record("body-death", ["body"], "body-death", $"{TargetTypeName}.PlayBodyDeathAnim");
    }

    private static void Record(
        string transitionId,
        IReadOnlyList<string> affectedPartIds,
        string activeStateId,
        string sourceHook)
    {
        s_events?.Record(PackageId, transitionId, affectedPartIds, activeStateId, sourceHook);
    }

    private static string NormalizeSide(object side)
    {
        var value = side.ToString() ?? string.Empty;
        return value.Contains("left", StringComparison.OrdinalIgnoreCase)
            ? "left"
            : "right";
    }
}
