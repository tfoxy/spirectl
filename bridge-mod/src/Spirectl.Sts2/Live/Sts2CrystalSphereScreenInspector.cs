using System.Collections;
using Godot;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live;

internal static class Sts2CrystalSphereScreenInspector
{
    public static IReadOnlyList<ResolvedCrystalSphereChoice> ResolveChoices(
        object screenObject,
        string? defaultPlayerId,
        ICollection<StateNoticeSnapshot>? notices = null)
    {
        var choices = new List<ResolvedCrystalSphereChoice>();
        var minigame = Sts2LiveIntrospection.GetMemberValue(screenObject, "_entity");
        if (minigame is null)
        {
            AddNotice(
                notices,
                "crystal-sphere-state-unavailable",
                "The Crystal Sphere screen was visible but its minigame entity could not be resolved.");
            return choices;
        }

        var divinationCount = ResolveInt(Sts2LiveIntrospection.GetMemberValue(minigame, "DivinationCount"));
        var isFinished = ResolveBool(minigame, "IsFinished");
        var canDivine = !isFinished && divinationCount > 0;

        TryAddToolChoice(
            choices,
            Sts2CrystalSphereIds.BigToolChoiceId(),
            "Big Divination",
            Sts2LiveIntrospection.GetMemberValue(screenObject, "_bigDivinationButton"),
            defaultPlayerId,
            canDivine);
        TryAddToolChoice(
            choices,
            Sts2CrystalSphereIds.SmallToolChoiceId(),
            "Small Divination",
            Sts2LiveIntrospection.GetMemberValue(screenObject, "_smallDivinationButton"),
            defaultPlayerId,
            canDivine);

        var cellControls = ResolveCellControls(screenObject);
        foreach (var cell in ResolveMinigameCells(minigame))
        {
            if (!ResolveBool(cell, "IsHidden"))
            {
                continue;
            }

            var cellControl = cellControls.FirstOrDefault(control =>
                ReferenceEquals(Sts2LiveIntrospection.GetMemberValue(control, "Entity"), cell));
            if (cellControl is null)
            {
                AddNotice(
                    notices,
                    "crystal-sphere-visible-choices-partial",
                    "A hidden Crystal Sphere cell was present in the minigame model but its visible cell control could not be resolved.");
                continue;
            }

            var x = ResolveInt(Sts2LiveIntrospection.GetMemberValue(cell, "X"));
            var y = ResolveInt(Sts2LiveIntrospection.GetMemberValue(cell, "Y"));
            var choiceId = Sts2CrystalSphereIds.CellChoiceId(x, y);
            var arguments = new ActionArgumentsSnapshot(
                defaultPlayerId,
                null,
                null,
                null,
                null,
                null,
                IntentKind: "use-crystal-sphere-control",
                Values: new Dictionary<string, string> { ["controlId"] = choiceId });
            var checkedHookPaths = new[] { "NCrystalSphereScreen.OnCellClicked(cellControl)" };
            choices.Add(new ResolvedCrystalSphereChoice(
                new ChoiceSnapshot(
                    Id: choiceId,
                    Label: $"Cell {x},{y}",
                    Kind: "crystal-sphere-cell",
                    Provisional: false,
                    OwnerPlayerId: defaultPlayerId,
                    ChoiceKind: "crystal-sphere-cell",
                    IntentKind: "use-crystal-sphere-control",
                    Perspective: ResolvePerspective(defaultPlayerId),
                    PreferredAction: "use-crystal-sphere-control",
                    Arguments: arguments,
                    Enabled: canDivine,
                    DisabledReason: canDivine ? null : "crystal-sphere-divination-unavailable",
                    PreferredActionRef: CreatePreferredActionRef(choiceId, $"Cell {x},{y}", defaultPlayerId, "use-crystal-sphere-control", SemanticActionKind.UseCrystalSphereControl, arguments, canDivine, canDivine ? null : "crystal-sphere-divination-unavailable", checkedHookPaths),
                    CheckedHookPaths: checkedHookPaths,
                    LegalityStatus: canDivine ? ActionLegalityKind.Legal : ActionLegalityKind.Illegal),
                Control: cellControl,
                Kind: CrystalSphereChoiceKind.Cell,
                IsExecutable: canDivine,
                X: x,
                Y: y));
        }

        var proceedButton = Sts2LiveIntrospection.GetMemberValue(screenObject, "_proceedButton");
        if (isFinished
            && proceedButton is not null
            && ResolveVisible(proceedButton)
            && ResolveIsExecutable(proceedButton, defaultValue: false))
        {
            var arguments = new ActionArgumentsSnapshot(defaultPlayerId, null, null, null, null, null, IntentKind: "proceed-event");
            var checkedHookPaths = new[] { "NCrystalSphereScreen.OnProceedButtonPressed(proceedButton)" };
            choices.Add(new ResolvedCrystalSphereChoice(
                new ChoiceSnapshot(
                    Id: Sts2CrystalSphereIds.ProceedChoiceId(),
                    Label: "Proceed",
                    Kind: "crystal-sphere-flow",
                    Provisional: false,
                    OwnerPlayerId: defaultPlayerId,
                    ChoiceKind: "crystal-sphere-flow",
                    IntentKind: "proceed-event",
                    Perspective: ResolvePerspective(defaultPlayerId),
                    PreferredAction: "proceed-event",
                    Arguments: arguments,
                    Enabled: true,
                    PreferredActionRef: CreatePreferredActionRef(Sts2CrystalSphereIds.ProceedChoiceId(), "Proceed", defaultPlayerId, "proceed-event", SemanticActionKind.ProceedEvent, arguments, enabled: true, disabledReason: null, checkedHookPaths),
                    CheckedHookPaths: checkedHookPaths,
                    LegalityStatus: ActionLegalityKind.Legal),
                Control: proceedButton,
                Kind: CrystalSphereChoiceKind.Proceed,
                IsExecutable: true,
                X: null,
                Y: null));
        }

        var executableCount = choices.Count(choice => choice.IsExecutable);
        var partialChoiceNotice = Sts2PartialChoiceNotice.TryCreate(
            "crystal-sphere-visible-choices-partial",
            "Visible Crystal Sphere choices can remain unavailable when divinations are spent or a control is disabled; only executable choices appear in availableActions.",
            visibleChoiceCount: choices.Count,
            executableChoiceCount: executableCount);
        if (partialChoiceNotice is not null)
        {
            notices?.Add(partialChoiceNotice);
        }

        return choices;
    }

    private static void TryAddToolChoice(
        ICollection<ResolvedCrystalSphereChoice> choices,
        string choiceId,
        string label,
        object? control,
        string? defaultPlayerId,
        bool canDivine)
    {
        if (control is null || !ResolveVisible(control))
        {
            return;
        }

        var enabled = canDivine && ResolveIsExecutable(control);
        var arguments = new ActionArgumentsSnapshot(
            defaultPlayerId,
            null,
            null,
            null,
            null,
            null,
            IntentKind: "use-crystal-sphere-control",
            Values: new Dictionary<string, string> { ["controlId"] = choiceId });
        var checkedHookPaths = choiceId == Sts2CrystalSphereIds.BigToolChoiceId()
            ? new[] { "NCrystalSphereScreen.SetBigDivination(bigButton)" }
            : new[] { "NCrystalSphereScreen.SetSmallDivination(smallButton)" };
        choices.Add(new ResolvedCrystalSphereChoice(
            new ChoiceSnapshot(
                Id: choiceId,
                Label: label,
                Kind: "crystal-sphere-tool",
                Provisional: false,
                OwnerPlayerId: defaultPlayerId,
                ChoiceKind: "crystal-sphere-tool",
                IntentKind: "use-crystal-sphere-control",
                Perspective: ResolvePerspective(defaultPlayerId),
                PreferredAction: "use-crystal-sphere-control",
                Arguments: arguments,
                Enabled: enabled,
                DisabledReason: enabled ? null : "crystal-sphere-divination-unavailable",
                PreferredActionRef: CreatePreferredActionRef(choiceId, label, defaultPlayerId, "use-crystal-sphere-control", SemanticActionKind.UseCrystalSphereControl, arguments, enabled, enabled ? null : "crystal-sphere-divination-unavailable", checkedHookPaths),
                CheckedHookPaths: checkedHookPaths,
                LegalityStatus: enabled ? ActionLegalityKind.Legal : ActionLegalityKind.Illegal),
            Control: control,
            Kind: choiceId == Sts2CrystalSphereIds.BigToolChoiceId()
                ? CrystalSphereChoiceKind.BigTool
                : CrystalSphereChoiceKind.SmallTool,
            IsExecutable: enabled,
            X: null,
            Y: null));
    }

    private static VisibleActionReferenceSnapshot CreatePreferredActionRef(
        string choiceId,
        string label,
        string? playerId,
        string preferredAction,
        SemanticActionKind kind,
        ActionArgumentsSnapshot arguments,
        bool enabled,
        string? disabledReason,
        IReadOnlyList<string> checkedHookPaths)
        => new(
            Sts2ActionIds.Intent("crystal-sphere", preferredAction, choiceId),
            label,
            Enabled: enabled,
            OwnerPlayerId: playerId,
            ActionKind: kind,
            IntentKind: preferredAction,
            Arguments: arguments,
            LegalityStatus: enabled ? ActionLegalityKind.Legal : ActionLegalityKind.Illegal,
            DisabledReason: disabledReason,
            Perspective: ResolvePerspective(playerId),
            CheckedHookPaths: checkedHookPaths);

    private static IReadOnlyList<object> ResolveCellControls(object screenObject)
    {
        var cellContainer = Sts2LiveIntrospection.GetMemberValue(screenObject, "_cellContainer");
        return EnumerateChildren(cellContainer)
            .Where(ResolveVisible)
            .ToArray();
    }

    private static IEnumerable<object> ResolveMinigameCells(object minigame)
    {
        var cells = Sts2LiveIntrospection.GetMemberValue(minigame, "cells") as IEnumerable;
        foreach (var cell in cells ?? Array.Empty<object>())
        {
            if (cell is not null)
            {
                yield return cell;
            }
        }
    }

    private static IEnumerable<object> EnumerateChildren(object? target)
    {
        var children = Sts2LiveIntrospection.GetMemberValue(target, "Children") as IEnumerable;
        if (children is not null)
        {
            foreach (var child in children)
            {
                if (child is not null)
                {
                    yield return child;
                }
            }

            yield break;
        }

        if (target is Node node)
        {
            foreach (var child in node.GetChildren())
            {
                if (child is not null)
                {
                    yield return child;
                }
            }
        }
    }

    private static bool ResolveVisible(object target)
        => Sts2LiveIntrospection.GetMemberValue(target, "Visible") switch
        {
            bool visible => visible,
            _ => true,
        };

    private static bool ResolveIsExecutable(object control, bool defaultValue = true)
    {
        return Sts2LiveIntrospection.GetMemberValue(control, "Disabled") switch
        {
            bool disabled => !disabled,
            _ => Sts2LiveIntrospection.GetMemberValue(control, "IsEnabled") switch
            {
                bool isEnabled => isEnabled,
                _ => Sts2LiveIntrospection.GetMemberValue(control, "IsDisabled") switch
                {
                    bool isDisabled => !isDisabled,
                    _ => defaultValue,
                },
            },
        };
    }

    private static bool ResolveBool(object target, string memberName, bool defaultValue = false)
        => Sts2LiveIntrospection.GetMemberValue(target, memberName) switch
        {
            bool value => value,
            _ => defaultValue,
        };

    private static string ResolvePerspective(string? playerId)
        => string.IsNullOrWhiteSpace(playerId) ? "shared" : "local";

    private static int ResolveInt(object? value)
        => value switch
        {
            int intValue => intValue,
            long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
            short shortValue => shortValue,
            byte byteValue => byteValue,
            string stringValue when int.TryParse(stringValue, out var parsed) => parsed,
            _ => 0,
        };

    private static void AddNotice(
        ICollection<StateNoticeSnapshot>? notices,
        string code,
        string message)
    {
        if (notices is null || notices.Any(notice => notice.Code == code))
        {
            return;
        }

        Sts2StateNotice.AddPartialOnce(notices, code, message, "choices", nameof(Sts2CrystalSphereScreenInspector));
    }
}

internal sealed record ResolvedCrystalSphereChoice(
    ChoiceSnapshot Snapshot,
    object Control,
    CrystalSphereChoiceKind Kind,
    bool IsExecutable,
    int? X,
    int? Y);

internal enum CrystalSphereChoiceKind
{
    BigTool,
    SmallTool,
    Cell,
    Proceed,
}
