using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace Spirectl.Sts2.Live;

/// <summary>
/// Null-safe access to the live <see cref="NTargetManager"/>. The game's
/// <c>NTargetManager.Instance</c> getter dereferences <c>NRun.Instance.GlobalUi</c>
/// unguarded, so it throws whenever a run is not fully constructed — including the
/// embark window where <c>RunManager.State</c> is already set but the run scene has
/// not been created, where state observation runs from a Godot callback and the
/// fault kills the game process. Always resolve the manager through this helper
/// instead of the game getter.
/// </summary>
internal static class Sts2TargetManagerAccess
{
    public static NTargetManager? InstanceOrNull => NRun.Instance?.GlobalUi?.TargetManager;

    public static bool IsInSelection => InstanceOrNull?.IsInSelection == true;
}
