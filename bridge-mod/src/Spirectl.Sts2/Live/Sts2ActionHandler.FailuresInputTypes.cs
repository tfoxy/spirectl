using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using Godot;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace Spirectl.Sts2.Live;

public sealed partial class Sts2ActionHandler
{
    // The members an embedded-profile dispatch arm reaches. The rest of this area is in
    // Sts2ActionHandler.FailuresInputTypes.Full.cs, moved verbatim, which the embedded profile does not compile.

    private static ActionExecutionResult MapActionUnavailable(
        SemanticActionKind kind,
        string message,
        string field,
        string value,
        string note)
    {
        var code = InferLegalityFailureCode(field, note);
        return ActionExecutionResult.Failure(
            kind: kind,
            code: code,
            message: message,
            details:
            [
                new ActionFailureDetail(
                    Field: field,
                    Value: value,
                    Note: note,
                    ReasonCode: code,
                    Screen: ExtractDiagnosticValue(note, "screen"),
                    PlayerId: field == "player_id" ? value : ExtractDiagnosticValue(note, "localPlayerId")),
            ]);
    }

    private static ActionFailureCode InferLegalityFailureCode(string field, string note)
    {
        if (string.Equals(field, "player_id", StringComparison.Ordinal))
        {
            return ActionFailureCode.WrongPlayer;
        }

        if (string.Equals(field, "screen", StringComparison.Ordinal)
            || note.Contains("state.screen.id", StringComparison.OrdinalIgnoreCase)
            || note.Contains("screen=", StringComparison.OrdinalIgnoreCase))
        {
            return ActionFailureCode.WrongScreen;
        }

        if (note.Contains("current target ids", StringComparison.OrdinalIgnoreCase)
            || note.Contains("currently valid targets", StringComparison.OrdinalIgnoreCase)
            || note.Contains("currently playable", StringComparison.OrdinalIgnoreCase)
            || note.Contains("currently usable", StringComparison.OrdinalIgnoreCase)
            || note.Contains("currently executable", StringComparison.OrdinalIgnoreCase)
            || note.Contains("currently travelable", StringComparison.OrdinalIgnoreCase)
            || note.Contains("not already queued", StringComparison.OrdinalIgnoreCase)
            || note.Contains("does not currently have enough gold", StringComparison.OrdinalIgnoreCase))
        {
            return ActionFailureCode.NotEnabled;
        }

        if (note.Contains("no longer", StringComparison.OrdinalIgnoreCase)
            || note.Contains("state likely changed", StringComparison.OrdinalIgnoreCase))
        {
            return ActionFailureCode.StaleId;
        }

        if (note.Contains("visible", StringComparison.OrdinalIgnoreCase)
            || note.Contains("stable", StringComparison.OrdinalIgnoreCase))
        {
            return ActionFailureCode.NotVisible;
        }

        return ActionFailureCode.InvalidAction;
    }

    private static string? ExtractDiagnosticValue(string note, string key)
    {
        var prefix = key + "=";
        var start = note.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += prefix.Length;
        var end = note.IndexOfAny([';', ',', '.'], start);
        return end < 0 ? note[start..].Trim() : note[start..end].Trim();
    }

    private static InputEventMouseButton CreateMouseClickEvent(Vector2 position, MouseButton button, bool pressed)
    {
        return new InputEventMouseButton
        {
            Position = position,
            GlobalPosition = position,
            WindowId = 0,
            ButtonIndex = button,
            ButtonMask = pressed ? ToMouseButtonMask(button) : 0,
            Factor = 1.0f,
            Pressed = pressed,
        };
    }

    private static MouseButton ToGodotMouseButton(RawMouseButtonKind button)
    {
        return button switch
        {
            RawMouseButtonKind.Right => MouseButton.Right,
            RawMouseButtonKind.Middle => MouseButton.Middle,
            RawMouseButtonKind.WheelUp => MouseButton.WheelUp,
            RawMouseButtonKind.WheelDown => MouseButton.WheelDown,
            _ => MouseButton.Left,
        };
    }

    private static MouseButtonMask ToMouseButtonMask(MouseButton button)
    {
        return button switch
        {
            MouseButton.Right => MouseButtonMask.Right,
            MouseButton.Middle => MouseButtonMask.Middle,
            // Wheel buttons don't hold — no mask (a scroll tick is a momentary press+release).
            MouseButton.WheelUp or MouseButton.WheelDown => (MouseButtonMask)0,
            _ => MouseButtonMask.Left,
        };
    }

    private sealed record MapActionContext(
        ScreenLocatorResult Screen,
        string? LocalPlayerId,
        NMapScreen MapScreen,
        IReadOnlyDictionary<string, ResolvedMapNode> NodesById,
        IReadOnlyDictionary<string, ResolvedMapChoice> ChoicesById);

    private sealed record RewardActionContext(
        ScreenLocatorResult Screen,
        object ScreenObject,
        string? LocalPlayerId,
        IReadOnlyDictionary<string, ResolvedRewardChoice> ChoicesById);

    private sealed record LobbyActionContext(
        ScreenLocatorResult Screen,
        object ScreenObject,
        LobbyStateSnapshot Lobby,
        StartRunLobby? StartRunLobby,
        LoadRunLobby? LoadRunLobby);

    private sealed record ActionOwnershipContext(
        string? RequestedPlayerId,
        string? ResolvedOwnerPlayerId,
        string? LocalPlayerId,
        string? HostPlayerId,
        IReadOnlyList<string> HostLocalPlayerIds,
        string Screen,
        string Action);
}
