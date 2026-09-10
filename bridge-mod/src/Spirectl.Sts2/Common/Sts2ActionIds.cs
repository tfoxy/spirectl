namespace Spirectl.Sts2;

public static class Sts2ActionIds
{
    public static string MainMenuStartRun()
        => "action:menu:start-run";

    public static string PlayCard(string? playerId, string cardId, string? targetId = null)
        => WithOptionalTarget($"action:{playerId}:play-card:{cardId}", targetId);

    public static string UsePotion(string? playerId, string potionId, string? targetId = null)
        => WithOptionalTarget($"action:{playerId}:use-potion:{potionId}", targetId);

    public static string EndTurn(string? playerId)
        => $"action:{playerId}:end-turn";

    public static string CancelEndTurn(string? playerId)
        => $"action:{playerId}:cancel-end-turn";

    public static string Choice(string scope, string choiceId)
        => Intent(scope, "choose", choiceId);

    public static string Intent(string scope, string intent, string choiceId)
        => $"action:{scope}:{intent}:{choiceId}";

    public static string IntentWithoutChoice(string scope, string intent)
        => $"action:{scope}:{intent}";

    public static string LobbyReady()
        => "action:lobby:ready";

    public static string LobbyUnready()
        => "action:lobby:unready";

    public static string LobbySelectCharacter(string characterId)
        => $"action:lobby:select-character:{characterId}";

    private static string WithOptionalTarget(string baseId, string? targetId)
        => string.IsNullOrWhiteSpace(targetId) ? baseId : $"{baseId}:{targetId}";
}
