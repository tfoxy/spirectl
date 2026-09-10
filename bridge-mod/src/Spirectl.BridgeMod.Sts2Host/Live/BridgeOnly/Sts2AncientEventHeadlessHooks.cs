using System;
using System.Reflection;
using HarmonyLib;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live;


/// <summary>
/// Headless Ancient-event crash guard.
///
/// The forced Act 2 (Hive) Ancient event (Tezcatara / Orobas / Pael) builds a visual layout when a
/// viewer enters the room, and several of its scenes carry animation / compressed-texture resources
/// that hard-crash the embedded headless host on instantiation (the run dies with a Godot StringName
/// teardown: "Unreferenced static string ... _compression / markers / name_changed", taking the
/// whole process down). The model layer — choosing an option and proceeding — is unaffected and was
/// verified stable in isolation; only the visual instantiation crashes. Two visual entry points hit
/// it, both prefixed to no-ops here:
///
/// 1. <c>NAncientEventLayout.InitializeVisuals</c> — instantiates the ancient name banner
///    (<c>NAncientNameBanner.Create</c>) and the background scene
///    (<c>AncientEventModel.CreateBackgroundScene().Instantiate&lt;Control&gt;()</c>). We replicate
///    only its first line (set the private <c>_ancientEvent</c> field, which <c>_ExitTree</c> and the
///    option/dialogue path read) and skip the rest. The banner field stays null (every access
///    null-checks it) and the bg container — populated in <c>_Ready</c>, not here — is left empty.
///
/// 2. <c>NAncientEventLayout.SetDialogue</c> — instantiates one <c>NAncientDialogueLine</c> per line
///    from <c>res://scenes/events/ancient_dialogue_line.tscn</c>. Skipped entirely. With an empty
///    dialogue list, <c>OnSetupComplete -> SetDialogueLineAndAnimate(0)</c> sees
///    <c>IsDialogueOnLastLine</c> (0 &gt;= Count-1 == -1) and immediately enables the option buttons,
///    and every child access is guarded (<c>GetChildOrNull</c> / child-count checks).
///
/// The auto-player then resolves the event through the normal option/model path. The visuals are
/// purely cosmetic for the headless bot, so this trades them for headless stability and lets the bot
/// pass the forced Ancient event (the only Act 2 entry node).
/// </summary>
internal static class Sts2AncientEventHeadlessHooks
{
    private const string LayoutTypeName = "MegaCrit.Sts2.Core.Nodes.Events.NAncientEventLayout";
    private const string BaseLayoutTypeName = "MegaCrit.Sts2.Core.Nodes.Events.NEventLayout";
    private const string EventModelTypeName = "MegaCrit.Sts2.Core.Models.EventModel";
    private const string AncientModelTypeName = "MegaCrit.Sts2.Core.Models.AncientEventModel";

    private static readonly object Sync = new();
    private static bool _installed;
    private static Type? _ancientLayoutType;
    private static Type? _ancientModelType;

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

            var layoutType = AccessTools.TypeByName(LayoutTypeName);
            _ancientLayoutType = layoutType;
            _ancientModelType = AccessTools.TypeByName(AncientModelTypeName);
            var eventModelType = AccessTools.TypeByName(EventModelTypeName);
            var baseLayoutType = AccessTools.TypeByName(BaseLayoutTypeName);
            var createScene = eventModelType is null
                ? null
                : AccessTools.Method(eventModelType, "CreateScene");
            var createScenePostfix = typeof(Sts2AncientEventHeadlessHooks).GetMethod(
                nameof(CreateScenePostfix),
                BindingFlags.NonPublic | BindingFlags.Static);
            var setDialogue = layoutType is null
                ? null
                : AccessTools.Method(layoutType, "SetDialogue");
            var initializeVisuals = layoutType is null
                ? null
                : AccessTools.Method(layoutType, "InitializeVisuals");
            var addOptions = baseLayoutType is null
                ? null
                : AccessTools.Method(baseLayoutType, "AddOptions");
            var setDialoguePrefix = typeof(Sts2AncientEventHeadlessHooks).GetMethod(
                nameof(SetDialoguePrefix),
                BindingFlags.NonPublic | BindingFlags.Static);
            var initializeVisualsPrefix = typeof(Sts2AncientEventHeadlessHooks).GetMethod(
                nameof(InitializeVisualsPrefix),
                BindingFlags.NonPublic | BindingFlags.Static);
            var addOptionsPrefix = typeof(Sts2AncientEventHeadlessHooks).GetMethod(
                nameof(AddOptionsPrefix),
                BindingFlags.NonPublic | BindingFlags.Static);

            if (setDialogue is null || initializeVisuals is null || addOptions is null
                || createScene is null || _ancientModelType is null
                || setDialoguePrefix is null || initializeVisualsPrefix is null
                || addOptionsPrefix is null || createScenePostfix is null)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.assets.headless-ancient",
                    "Unable to install headless Ancient-event guard; required NEventRoom/NEventLayout/EventModel members were not found.");
                return;
            }

            try
            {
                var harmony = new Harmony("spirectl.assets.headless-ancient-event");
                // Primary guard: ancient events return a trivial layout scene, so NEventRoom.Layout is
                // null and NEventRoom.SetupLayout returns early — skipping ALL visual setup. The
                // method-level prefixes below are belt-and-suspenders in case Layout is ever non-null.
                harmony.Patch(createScene, postfix: new HarmonyMethod(createScenePostfix));
                harmony.Patch(initializeVisuals, prefix: new HarmonyMethod(initializeVisualsPrefix));
                harmony.Patch(setDialogue, prefix: new HarmonyMethod(setDialoguePrefix));
                harmony.Patch(addOptions, prefix: new HarmonyMethod(addOptionsPrefix));
            }
            catch (Exception ex)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.assets.headless-ancient",
                    $"Skipping headless Ancient-event guard because Harmony patching failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            _installed = true;
            logStream.Write(
                BridgeLogLevel.Info,
                "bridge.assets.headless-ancient",
                "Installed headless Ancient-event guard; the Ancient dialogue layout is skipped (options resolve normally).");
        }
    }

    // For ancient events, replace the layout scene (ancient_event_layout.tscn) with a trivial empty
    // Control scene. The event room still instantiates and mounts it, but its typed Layout reference then
    // resolves to null, so the room's layout setup returns at its own null guard — skipping every visual step
    // that can crash the headless host. The event model (options/effects) is unaffected, so the auto-player
    // resolves it via the
    // model layer. Non-ancient events are untouched.
    private static void CreateScenePostfix(object __instance, ref Godot.PackedScene __result)
    {
        if (_ancientModelType is null || !_ancientModelType.IsInstanceOfType(__instance))
        {
            return;
        }

        Godot.GD.Print("[AUTOPLAY] ancient-guard: CreateScene -> trivial layout (Layout will be null)");
        var root = new Godot.Control();
        var scene = new Godot.PackedScene();
        scene.Pack(root);
        root.Free();
        __result = scene;
    }

    // Skip the original SetDialogue entirely: do not instantiate any NAncientDialogueLine scenes.
    private static bool SetDialoguePrefix()
    {
        Godot.GD.Print("[AUTOPLAY] ancient-guard: SetDialogue skipped");
        return false;
    }

    // Skip option-button instantiation for Ancient layouts only: NEventOptionButton.Create
    // instantiates res://scenes/events/ancient_event_option_button.tscn for AncientEventModel, which
    // crashes the headless host like the other ancient scenes. The model options and their
    // BeforeChosen wiring are set up in NEventRoom.SetOptions BEFORE AddOptions runs, so the
    // auto-player still resolves the event via the model layer; only the (cosmetic) visual buttons are
    // skipped. Non-Ancient events build their option buttons normally.
    private static bool AddOptionsPrefix(object __instance)
    {
        if (_ancientLayoutType is not null && _ancientLayoutType.IsInstanceOfType(__instance))
        {
            Godot.GD.Print("[AUTOPLAY] ancient-guard: AddOptions skipped");
            return false;
        }

        return true;
    }

    // Replace InitializeVisuals: do only its safe first line (set _ancientEvent from the base _event
    // field, which _ExitTree and the option/dialogue path read) and skip the crashing banner +
    // background-scene instantiation. Returns false to skip the original body.
    private static bool InitializeVisualsPrefix(object __instance)
    {
        Godot.GD.Print("[AUTOPLAY] ancient-guard: InitializeVisuals skipped");
        var traverse = Traverse.Create(__instance);
        var ev = traverse.Field("_event").GetValue();
        traverse.Field("_ancientEvent").SetValue(ev);
        return false;
    }
}
