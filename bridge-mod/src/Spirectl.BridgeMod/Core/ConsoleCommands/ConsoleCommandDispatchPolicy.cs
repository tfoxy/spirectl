namespace Spirectl.Sts2.Core.ConsoleCommands;


public enum ConsoleCommandDispatchPath
{
    Direct,
    Networked,
}

public static class ConsoleCommandDispatchPolicy
{
    public static ConsoleCommandDispatchPath Resolve(
        bool isRealMultiplayer,
        bool isNetworkedCommand,
        bool hasRunState,
        bool hasLocalPlayer)
    {
        return isRealMultiplayer && isNetworkedCommand && hasRunState && hasLocalPlayer
            ? ConsoleCommandDispatchPath.Networked
            : ConsoleCommandDispatchPath.Direct;
    }
}
