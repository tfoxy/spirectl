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
    private static ActionExecutionResult CombatActionUnavailable(
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
                    PlayerId: field == "player_id" ? value : ExtractDiagnosticValue(note, "activePlayerId")),
            ]);
    }

    private static ActionExecutionResult ChoiceActionUnavailable(
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
                    Screen: field == "screen" ? value : ExtractQuotedScreen(note),
                    PlayerId: field == "player_id" ? value : ExtractDiagnosticValue(note, "localPlayerId")),
            ]);
    }

    private static IReadOnlyList<ActionFailureDetail> MainMenuStartRunFailureDetails(
        string choiceId,
        MainMenuStartRunHookInspectionResult inspection)
    {
        return
        [
            new ActionFailureDetail(
                Field: "choice_id",
                Value: choiceId,
                Note: "The active main-menu screen did not expose a callable start-run hook on the checked probe paths.",
                ReasonCode: ActionFailureCode.MissingHook,
                CheckedHookPaths: inspection.CheckedProbePaths,
                FieldDiagnostics: MainMenuStartRunFieldDiagnostics(inspection)),
            new ActionFailureDetail(
                Field: "screen_class",
                Value: inspection.ActiveScreenClassName,
                Note: "Active screen class resolved during start-run hook probing.",
                ReasonCode: ActionFailureCode.MissingHook),
            new ActionFailureDetail(
                Field: "resolved_hook_path",
                Value: inspection.ResolvedHookPath ?? string.Empty,
                Note: "Resolved callable hook path from the active inspection result, if one was found.",
                ReasonCode: ActionFailureCode.MissingHook),
            new ActionFailureDetail(
                Field: "checked_probe_paths",
                Value: FormatCheckedProbePaths(inspection),
                Note: "Ordered probe paths checked while resolving the callable start-run hook.",
                ReasonCode: ActionFailureCode.MissingHook,
                CheckedHookPaths: inspection.CheckedProbePaths),
            new ActionFailureDetail(
                Field: "present_candidates",
                Value: FormatPresentCandidates(inspection),
                Note: "Callable or partially matching candidates that were actually present on the active screen.",
                ReasonCode: ActionFailureCode.MissingHook,
                FieldDiagnostics: MainMenuStartRunFieldDiagnostics(inspection)),
        ];
    }

    private static IReadOnlyList<ActionFailureFieldDiagnostic> MainMenuStartRunFieldDiagnostics(
        MainMenuStartRunHookInspectionResult inspection)
    {
        var diagnostics = new List<ActionFailureFieldDiagnostic>
        {
            new("screen_class", inspection.ActiveScreenClassName, "Active screen class resolved during hook probing."),
            new("resolved_hook_path", inspection.ResolvedHookPath ?? string.Empty, "Resolved callable hook path, if present."),
        };
        diagnostics.AddRange(inspection.PresentCandidates.Select(path =>
            new ActionFailureFieldDiagnostic("present_candidate", path, "Candidate observed while probing the active screen.")));
        return diagnostics;
    }

    private static string FormatCheckedProbePaths(MainMenuStartRunHookInspectionResult inspection)
    {
        return inspection.CheckedProbePaths.Count == 0
            ? "none"
            : string.Join(", ", inspection.CheckedProbePaths);
    }

    private static string FormatPresentCandidates(MainMenuStartRunHookInspectionResult inspection)
    {
        return inspection.PresentCandidates.Count == 0
            ? "none"
            : string.Join(", ", inspection.PresentCandidates);
    }

    private static bool TryInvokeShopChoice(
        object screenObject,
        object inventory,
        ResolvedShopChoice shopChoice,
        out bool accepted)
    {
        if (shopChoice.IsFlowChoice)
        {
            accepted = Sts2LiveIntrospection.TryInvokeParameterlessMethod(screenObject, "Close")
                || Sts2LiveIntrospection.TryInvokeParameterlessMethod(shopChoice.Control, "OnRelease");
            return accepted;
        }

        var result = shopChoice.RequiresCancelableWrapper
            ? Sts2LiveIntrospection.InvokeMethod(shopChoice.Entry, "OnTryPurchaseWrapper", inventory, false, true)
            : Sts2LiveIntrospection.InvokeMethod(shopChoice.Entry, "OnTryPurchaseWrapper", inventory, false);
        if (result is null)
        {
            accepted = false;
            return false;
        }

        accepted = true;
        switch (result)
        {
            case Task<bool> boolTask:
                if (boolTask.IsCompleted)
                {
                    accepted = boolTask.GetAwaiter().GetResult();
                }

                return true;
            case Task task:
                if (task.IsCompleted)
                {
                    task.GetAwaiter().GetResult();
                }

                return true;
            case bool boolResult:
                accepted = boolResult;
                return true;
            default:
                return true;
        }
    }

    private static bool TryExecuteMapChoice(ResolvedMapChoice mapChoice)
    {
        if (!string.Equals(mapChoice.Snapshot.Id, Sts2MapIds.BackChoiceId(), StringComparison.Ordinal))
        {
            return false;
        }

        return Sts2LiveIntrospection.TryInvokeParameterlessMethod(
            mapChoice.Control,
            "OnRelease",
            "OnPress",
            "Press");
    }

    private static bool TryExecuteEventRoomChoice(
        EventRoomActionContext eventRoomContext,
        ResolvedEventRoomChoice choice)
    {
        return choice.ExecutionKind switch
        {
            EventRoomChoiceExecutionKind.Option => choice.Option is not null
                && (Sts2LiveIntrospection.TryInvokeMethod(
                        eventRoomContext.RoomObject,
                        "OptionButtonClicked",
                        choice.Option,
                        choice.Index)
                    || Sts2LiveIntrospection.TryInvokeParameterlessMethod(choice.Button, "OnRelease")),
            EventRoomChoiceExecutionKind.FakeMerchantOpenShop
                => Sts2LiveIntrospection.TryInvokeParameterlessMethod(choice.Button, "OnRelease"),
            _ => false,
        };
    }

    private static bool TryExecuteCrystalSphereChoice(
        object screenObject,
        ResolvedCrystalSphereChoice choice)
    {
        return choice.Kind switch
        {
            CrystalSphereChoiceKind.BigTool => Sts2LiveIntrospection.TryInvokeMethod(
                screenObject,
                "SetBigDivination",
                choice.Control),
            CrystalSphereChoiceKind.SmallTool => Sts2LiveIntrospection.TryInvokeMethod(
                screenObject,
                "SetSmallDivination",
                choice.Control),
            CrystalSphereChoiceKind.Cell => TryInvokeCrystalSphereCell(screenObject, choice.Control),
            CrystalSphereChoiceKind.Proceed => Sts2LiveIntrospection.TryInvokeMethod(
                screenObject,
                "OnProceedButtonPressed",
                choice.Control),
            _ => false,
        };
    }

    private static bool TryInvokeCrystalSphereCell(object screenObject, object cellControl)
    {
        var result = Sts2LiveIntrospection.InvokeMethod(screenObject, "OnCellClicked", cellControl);
        return result switch
        {
            null => false,
            Task task => task.IsCompletedSuccessfully || !task.IsFaulted,
            _ => true,
        };
    }

    private static ActionExecutionResult LobbyActionUnavailable(
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
                    PlayerId: field == "player_id" ? value : null),
            ]);
    }

    private static ActionExecutionResult LobbyRuntimeFailure(
        SemanticActionKind kind,
        string message,
        string value,
        string note)
    {
        return ActionExecutionResult.Failure(
            kind: kind,
            code: ActionFailureCode.MissingHook,
            message: message,
            details:
            [
                new ActionFailureDetail(
                    Field: "lobby",
                    Value: value,
                    Note: note,
                    ReasonCode: ActionFailureCode.MissingHook),
            ]);
    }

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

    private static string? ExtractQuotedScreen(string note)
    {
        const string marker = "screen '";
        var start = note.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = note.IndexOf('\'', start);
        return end < 0 ? null : note[start..end];
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

    private sealed record CombatActionContext(
        ScreenLocatorResult Screen,
        string LocalPlayerId,
        string? HostPlayerId,
        IReadOnlyList<string> HostLocalPlayerIds,
        string ActionOwnerPlayerId,
        Player ActionOwnerPlayer,
        bool IsActionOwnerHostLocalSeat,
        Player LocalPlayer,
        CombatState CombatState);

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

    private sealed record EventRoomActionContext(
        ScreenLocatorResult Screen,
        object RoomObject,
        string? LocalPlayerId,
        IReadOnlyDictionary<string, ResolvedEventRoomChoice> ChoicesById);

    private sealed record TreasureRoomActionContext(
        ScreenLocatorResult Screen,
        object? RoomObject,
        object? RelicCollection,
        string? LocalPlayerId,
        IReadOnlyDictionary<string, ResolvedTreasureRoomChoice> ChoicesById);

    private sealed record RestSiteActionContext(
        ScreenLocatorResult Screen,
        object RoomObject,
        string? LocalPlayerId,
        IReadOnlyDictionary<string, ResolvedRestSiteChoice> ChoicesById);

    private sealed record ShopActionContext(
        ScreenLocatorResult Screen,
        object ScreenObject,
        string? LocalPlayerId,
        object Inventory,
        IReadOnlyDictionary<string, ResolvedShopChoice> ChoicesById);

    private sealed record CardSelectionActionContext(
        ScreenLocatorResult Screen,
        object ScreenObject,
        string? LocalPlayerId,
        IReadOnlyDictionary<string, ResolvedCardSelectionChoice> ChoicesById);

    private sealed record CrystalSphereActionContext(
        ScreenLocatorResult Screen,
        object ScreenObject,
        string? LocalPlayerId,
        IReadOnlyDictionary<string, ResolvedCrystalSphereChoice> ChoicesById);

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

    private sealed record ResolvedCombatCard(CardModel Card, string Id, string Name);

    private sealed record ResolvedCombatPotion(PotionModel Potion, string Id, string Name, int SlotIndex);

    private sealed record ResolvedCombatTarget(Creature Creature, string Id, string Name);

    private static bool IsRandomCharacterId(string characterId)
    {
        var normalized = characterId.Replace('_', '-').Trim().ToLowerInvariant();
        return normalized is "random-character" or "random";
    }

    private sealed record SelectCharacterTarget(object Button, CharacterModel? Character);
}
