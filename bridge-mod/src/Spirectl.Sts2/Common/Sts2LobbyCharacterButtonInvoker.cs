using System.Reflection;

namespace Spirectl.Sts2;

internal static class Sts2LobbyCharacterButtonInvoker
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static bool TryExecute(object? button)
    {
        if (button is null)
        {
            return false;
        }

        return TryInvoke(button, "Select")
            || TryInvoke(button, "OnPress");
    }

    private static bool TryInvoke(object target, string methodName)
    {
        var method = target.GetType().GetMethod(methodName, InstanceFlags, Type.DefaultBinder, Type.EmptyTypes, null);
        if (method is null)
        {
            return false;
        }

        method.Invoke(target, null);
        return true;
    }
}
