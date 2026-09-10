using System.Globalization;
using Godot;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live;

internal static class Sts2RestSiteScreenInspector
{
    public const string RestSiteRoomType = "MegaCrit.Sts2.Core.Nodes.Rooms.NRestSiteRoom";
    private const string RestSiteButtonType = "MegaCrit.Sts2.Core.Nodes.RestSite.NRestSiteButton";

    public static IReadOnlyList<ResolvedRestSiteChoice> ResolveChoices(
        object restSiteRoom,
        string? defaultPlayerId,
        ICollection<StateNoticeSnapshot>? notices = null)
    {
        var choices = new List<ResolvedRestSiteChoice>();
        var container = Sts2LiveIntrospection.GetMemberValue(restSiteRoom, "_choicesContainer") as Node;
        var optionIndex = 0;
        foreach (var child in container?.GetChildren() ?? [])
        {
            // Browser/headless mode does not report these room sub-controls as engine-Visible even when
            // the option is active, so gate on the button TYPE and a populated Option (below) rather than
            // the Visible flag; per-option IsEnabled still gates whether it can be executed.
            if (child is not Control control
                || !Sts2LiveIntrospection.IsType(child, RestSiteButtonType))
            {
                continue;
            }

            var option = Sts2LiveIntrospection.GetMemberValue(child, "Option");
            if (option is null)
            {
                optionIndex++;
                continue;
            }

            var playerId = ResolveOwnerPlayerId(option, defaultPlayerId, notices);
            var optionId = ResolveOptionId(option, optionIndex, notices);
            var isEnabled = ResolveBool(option, "IsEnabled", defaultValue: true);
            var requiresConfirmation = ResolveBool(option, "RequiresConfirmation", defaultValue: false)
                || ResolveBool(option, "RequireConfirmation", defaultValue: false);
            var isExecutable = isEnabled && !requiresConfirmation;
            var preferredAction = ResolvePreferredAction(optionId);
            var choiceId = Sts2RestSiteIds.ChoiceId(playerId, optionId, optionIndex);
            choices.Add(new ResolvedRestSiteChoice(
                new ChoiceSnapshot(
                    Id: choiceId,
                    Label: ResolveLabel(option, optionId),
                    Kind: "rest-site-option",
                    Provisional: false,
                    OwnerPlayerId: playerId,
                    ChoiceKind: "rest-site-option",
                    IntentKind: preferredAction,
                    Perspective: ResolvePerspective(playerId),
                    PreferredAction: preferredAction,
                    Arguments: RestSiteArguments(playerId, choiceId, optionId, preferredAction),
                    Enabled: isExecutable,
                    DisabledReason: !isEnabled ? "not-enabled" : requiresConfirmation ? "confirmation-required" : null,
                    PreferredActionRef: CreatePreferredActionRef(preferredAction, playerId, choiceId, optionId, isExecutable, !isEnabled ? "not-enabled" : requiresConfirmation ? "confirmation-required" : null),
                    LegalityStatus: isExecutable ? ActionLegalityKind.Legal : ActionLegalityKind.Illegal,
                    CheckedHookPaths: ["restSiteButton.OnRelease"]),
                child,
                playerId,
                IsExecutable: isExecutable,
                IsFlowChoice: false,
                RestOptionId: optionId,
                PreferredAction: preferredAction));
            optionIndex++;
        }

        if (TryResolveProceedChoice(restSiteRoom, defaultPlayerId, out var proceedChoice))
        {
            choices.Add(proceedChoice);
        }

        return choices;
    }

    private static bool TryResolveProceedChoice(
        object restSiteRoom,
        string? defaultPlayerId,
        out ResolvedRestSiteChoice choice)
    {
        // Browser/headless mode doesn't report the proceed button as engine-Visible; gate on it being
        // enabled (the real canProceed signal) rather than the Visible flag.
        if ((Sts2LiveIntrospection.GetMemberValue(restSiteRoom, "ProceedButton")
                ?? Sts2LiveIntrospection.GetMemberValue(restSiteRoom, "_proceedButton")) is not Control control
            || !ResolveBool(control, "IsEnabled", defaultValue: false))
        {
            choice = default!;
            return false;
        }

        choice = new ResolvedRestSiteChoice(
            new ChoiceSnapshot(
                Id: Sts2RestSiteIds.ProceedChoiceId(),
                Label: "Proceed",
                Kind: "rest-site-flow",
                Provisional: false,
                OwnerPlayerId: defaultPlayerId,
                ChoiceKind: "rest-site-flow",
                IntentKind: "proceed-rest-site",
                Perspective: ResolvePerspective(defaultPlayerId),
                PreferredAction: "proceed-rest-site",
                Arguments: new ActionArgumentsSnapshot(defaultPlayerId, null, null, null, null, null, IntentKind: "proceed-rest-site"),
                Enabled: true,
                PreferredActionRef: new VisibleActionReferenceSnapshot(
                    Sts2ActionIds.Intent("rest-site", "proceed-rest-site", Sts2RestSiteIds.ProceedChoiceId()),
                    "Proceed",
                    Enabled: true,
                    OwnerPlayerId: defaultPlayerId,
                    ActionKind: SemanticActionKind.ProceedRestSite,
                    IntentKind: "proceed-rest-site",
                    Arguments: new ActionArgumentsSnapshot(defaultPlayerId, null, null, null, null, null, IntentKind: "proceed-rest-site"),
                    LegalityStatus: ActionLegalityKind.Legal,
                    Perspective: ResolvePerspective(defaultPlayerId),
                    CheckedHookPaths: ["restSiteRoom.OnProceedButtonReleased"]),
                LegalityStatus: ActionLegalityKind.Legal,
                CheckedHookPaths: ["restSiteRoom.OnProceedButtonReleased"]),
            control,
            defaultPlayerId,
            IsExecutable: true,
            IsFlowChoice: true);
        return true;
    }

    private static string ResolveOwnerPlayerId(
        object option,
        string? defaultPlayerId,
        ICollection<StateNoticeSnapshot>? notices)
    {
        if (Sts2LiveIntrospection.GetMemberValue(option, "Owner") is { } player)
        {
            return Sts2CombatIds.PlayerId(player);
        }

        if (!string.IsNullOrWhiteSpace(defaultPlayerId))
        {
            AddNotice(
                notices,
                "rest-site-choice-player-fallback",
                "Rest-site choice owners fall back to the local player id when the option does not expose an owner.");
            return defaultPlayerId!;
        }

        AddNotice(
            notices,
            "rest-site-choice-player-fallback",
            "Rest-site choice owners fall back to an unknown player id when the option does not expose an owner.");
        return "p:unknown";
    }

    private static string ResolveOptionId(
        object option,
        int optionIndex,
        ICollection<StateNoticeSnapshot>? notices)
    {
        var optionId = Normalize(Sts2LiveIntrospection.GetMemberValue(option, "OptionId")?.ToString());
        if (!string.IsNullOrWhiteSpace(optionId))
        {
            return optionId!.ToLowerInvariant();
        }

        AddNotice(
            notices,
            "rest-site-choice-id-fallback",
            "Rest-site choice ids fall back to screen-instance ordering when options do not expose stable ids.");
        return $"option-{optionIndex}";
    }

    private static string ResolveLabel(object option, string optionId)
    {
        var title = Sts2LiveIntrospection.InvokeMethod(
            Sts2LiveIntrospection.GetMemberValue(option, "Title"),
            "GetFormattedText") as string;
        var normalizedTitle = Normalize(title);
        if (!string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return normalizedTitle!;
        }

        return string.Join(
            " ",
            optionId.Split('-', '_', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(part.ToLowerInvariant())));
    }

    private static bool ResolveBool(object target, string memberName, bool defaultValue)
    {
        return Sts2LiveIntrospection.GetMemberValue(target, memberName) switch
        {
            bool value => value,
            _ => defaultValue,
        };
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.ReplaceLineEndings(" ").Trim();

    private static string ResolvePreferredAction(string optionId)
        => optionId.ToLowerInvariant() switch
        {
            "heal" or "rest" => "rest",
            "smith" or "upgrade" => "smith",
            _ => "use-rest-site-option",
        };

    private static ActionArgumentsSnapshot RestSiteArguments(
        string playerId,
        string choiceId,
        string optionId,
        string intentKind)
        => new(
            playerId,
            null,
            null,
            intentKind == "use-rest-site-option" ? null : choiceId,
            null,
            null,
            IntentKind: intentKind,
            Values: new Dictionary<string, string> { ["restOptionId"] = optionId });

    private static VisibleActionReferenceSnapshot CreatePreferredActionRef(
        string preferredAction,
        string playerId,
        string choiceId,
        string optionId,
        bool enabled,
        string? disabledReason)
        => new(
            Sts2ActionIds.Intent("rest-site", preferredAction, choiceId),
            preferredAction == "rest" ? "Rest" : preferredAction == "smith" ? "Smith" : "Use Rest Site Option",
            Enabled: enabled,
            OwnerPlayerId: playerId,
            ActionKind: preferredAction switch
            {
                "rest" => SemanticActionKind.Rest,
                "smith" => SemanticActionKind.Smith,
                _ => SemanticActionKind.UseRestSiteOption,
            },
            IntentKind: preferredAction,
            Arguments: RestSiteArguments(playerId, choiceId, optionId, preferredAction),
            LegalityStatus: enabled ? ActionLegalityKind.Legal : ActionLegalityKind.Illegal,
            DisabledReason: disabledReason,
            Perspective: ResolvePerspective(playerId),
            CheckedHookPaths: ["restSiteButton.OnRelease"]);

    private static string? ResolvePerspective(string? playerId)
        => string.IsNullOrWhiteSpace(playerId) ? null : $"player:{playerId}";

    private static void AddNotice(
        ICollection<StateNoticeSnapshot>? notices,
        string code,
        string message)
    {
        if (notices is null || notices.Any(notice => notice.Code == code))
        {
            return;
        }

        Sts2StateNotice.AddPartialOnce(notices, code, message, "choices", nameof(Sts2RestSiteScreenInspector));
    }
}

internal sealed record ResolvedRestSiteChoice(
    ChoiceSnapshot Snapshot,
    object Control,
    string? PlayerId,
    bool IsExecutable,
    bool IsFlowChoice,
    string? RestOptionId = null,
    int DisplayOrder = 0,
    string PreferredAction = "choose");
