using System.Collections;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live;

internal static class Sts2EventRoomScreenInspector
{
    public const string EventRoomType = "MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom";
    private const string FakeMerchantType = "MegaCrit.Sts2.Core.Nodes.Events.Custom.NFakeMerchant";

    public static object? ResolveActiveRoom()
    {
        var screenObject = Sts2LiveIntrospection.ResolveCurrentScreenObject();
        if (Sts2LiveIntrospection.IsType(screenObject, EventRoomType))
        {
            return screenObject;
        }

        return NEventRoom.Instance;
    }

    public static IReadOnlyList<ResolvedEventRoomChoice> ResolveChoices(
        object eventRoom,
        string? defaultPlayerId,
        ICollection<StateNoticeSnapshot>? notices = null)
    {
        var layout = Sts2LiveIntrospection.GetMemberValue(eventRoom, "Layout");
        var optionButtons = Sts2LiveIntrospection.GetMemberValue(layout, "OptionButtons") as IEnumerable;

        var choices = new List<ResolvedEventRoomChoice>();
        var index = 0;
        if (optionButtons is not null)
        {
            foreach (var buttonObject in optionButtons)
            {
                if (buttonObject is null || !ResolveVisible(buttonObject))
                {
                    index++;
                    continue;
                }

                var option = Sts2LiveIntrospection.GetMemberValue(buttonObject, "Option");
                if (option is null)
                {
                    index++;
                    continue;
                }

                var optionId = ResolveOptionId(option, index, notices);
                var titleText = ResolveRichText(Sts2LiveIntrospection.GetMemberValue(option, "Title"), "loc-string");
                var descriptionText = ResolveRichText(Sts2LiveIntrospection.GetMemberValue(option, "Description"), "loc-string");
                var buttonText = ResolveEventOptionButtonText(buttonObject);
                var label = titleText?.Text ?? FirstLine(buttonText?.Text) ?? ResolveLabel(option, optionId);
                descriptionText ??= RemainingLines(buttonText);
                var isProceed = ResolveBool(option, "IsProceed")
                    || string.Equals(optionId, "proceed", StringComparison.Ordinal)
                    || string.Equals(optionId, Sts2EventRoomIds.ProceedChoiceId(), StringComparison.Ordinal);
                var preferredAction = isProceed ? "proceed-event" : "select-event-option";
                var isExecutable = !ResolveBool(option, "IsLocked")
                    && !ResolveBool(option, "WasChosen")
                    && (isProceed || !IsControlDisabled(buttonObject));

                var choiceId = Sts2EventRoomIds.ChoiceId(optionId, index);
                var arguments = new ActionArgumentsSnapshot(
                    defaultPlayerId,
                    null,
                    null,
                    null,
                    null,
                    null,
                    IntentKind: preferredAction,
                    Values: new Dictionary<string, string> { ["eventOptionId"] = choiceId });
                var checkedHookPaths = new[] { "NEventRoom.OptionButtonClicked(option,index)", "button.OnRelease" };
                choices.Add(new ResolvedEventRoomChoice(
                    new ChoiceSnapshot(
                        Id: choiceId,
                        Label: label,
                        Kind: "event-option",
                        Provisional: false,
                        OwnerPlayerId: defaultPlayerId,
                        ChoiceKind: "event-option",
                        IntentKind: preferredAction,
                        Perspective: ResolvePerspective(defaultPlayerId),
                        PreferredAction: preferredAction,
                        Arguments: arguments,
                        Enabled: isExecutable,
                        DisabledReason: isExecutable ? null : "event-option-not-enabled",
                        PreferredActionRef: CreatePreferredActionRef(choiceId, label, defaultPlayerId, preferredAction, ResolveActionKind(preferredAction), arguments, isExecutable, isExecutable ? null : "event-option-not-enabled", checkedHookPaths),
                        CheckedHookPaths: checkedHookPaths,
                        LegalityStatus: isExecutable ? ActionLegalityKind.Legal : ActionLegalityKind.Illegal),
                    Button: buttonObject,
                    Option: option,
                    Index: index,
                    IsExecutable: isExecutable,
                    ExecutionKind: EventRoomChoiceExecutionKind.Option,
                    PreferredAction: preferredAction,
                    DescriptionText: descriptionText));
                index++;
            }
        }

        if (choices.Count > 0)
        {
            return choices;
        }

        AddFakeMerchantEntryChoice(eventRoom, defaultPlayerId, choices);
        return choices;
    }

    private static void AddFakeMerchantEntryChoice(
        object eventRoom,
        string? defaultPlayerId,
        ICollection<ResolvedEventRoomChoice> choices)
    {
        var fakeMerchant = FindDescendant(eventRoom, child => Sts2LiveIntrospection.IsType(child, FakeMerchantType));
        var merchantButton = ResolveFakeMerchantOpenButton(fakeMerchant);
        if (merchantButton is null || !ResolveVisible(merchantButton))
        {
            return;
        }

        var choiceId = Sts2EventRoomIds.FakeMerchantOpenShopChoiceId();
        var enabled = !IsControlDisabled(merchantButton);
        var arguments = new ActionArgumentsSnapshot(
            defaultPlayerId,
            null,
            null,
            null,
            null,
            null,
            IntentKind: "open-event-shop",
            Values: new Dictionary<string, string> { ["eventOptionId"] = choiceId });
        var checkedHookPaths = new[] { "button.OnRelease" };
        choices.Add(new ResolvedEventRoomChoice(
            new ChoiceSnapshot(
                Id: choiceId,
                Label: "Open Shop",
                Kind: "event-shop-entry",
                Provisional: false,
                OwnerPlayerId: defaultPlayerId,
                ChoiceKind: "event-shop-entry",
                IntentKind: "open-event-shop",
                Perspective: ResolvePerspective(defaultPlayerId),
                PreferredAction: "open-event-shop",
                Arguments: arguments,
                Enabled: enabled,
                DisabledReason: enabled ? null : "event-shop-entry-not-enabled",
                PreferredActionRef: CreatePreferredActionRef(choiceId, "Open Shop", defaultPlayerId, "open-event-shop", SemanticActionKind.OpenEventShop, arguments, enabled, enabled ? null : "event-shop-entry-not-enabled", checkedHookPaths),
                CheckedHookPaths: checkedHookPaths,
                LegalityStatus: enabled ? ActionLegalityKind.Legal : ActionLegalityKind.Illegal),
            Button: merchantButton,
            Option: null,
            Index: -1,
            IsExecutable: enabled,
            ExecutionKind: EventRoomChoiceExecutionKind.FakeMerchantOpenShop,
            PreferredAction: "open-event-shop",
            DescriptionText: null));
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
            Sts2ActionIds.Intent("event-room", preferredAction, choiceId),
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

    private static SemanticActionKind ResolveActionKind(string preferredAction)
        => preferredAction switch
        {
            "proceed-event" => SemanticActionKind.ProceedEvent,
            "open-event-shop" => SemanticActionKind.OpenEventShop,
            _ => SemanticActionKind.SelectEventOption,
        };

    public static EventRoomPageStateSnapshot? ResolvePage(
        object eventRoom,
        ICollection<StateNoticeSnapshot>? notices = null)
    {
        var layout = Sts2LiveIntrospection.GetMemberValue(eventRoom, "Layout");
        var eventModel = Sts2LiveIntrospection.GetMemberValue(eventRoom, "_event")
            ?? Sts2LiveIntrospection.GetMemberValue(layout, "_event");
        var ancientEvent = ResolveAncientEventModel(layout, eventModel);

        var title = ResolveRichText(Sts2LiveIntrospection.GetMemberValue(eventModel, "Title"), "loc-string");
        var description = ResolveRichText(Sts2LiveIntrospection.GetMemberValue(eventModel, "Description"), "loc-string");
        var textSource = "event-model";
        var provisional = false;

        if (title is null)
        {
            title = ResolveVisibleNodeText(Sts2LiveIntrospection.GetMemberValue(layout, "_title"), "mega-label");
            if (title is not null)
            {
                textSource = "partial";
                provisional = true;
            }
        }

        if (description is null)
        {
            description = ResolveVisibleNodeText(Sts2LiveIntrospection.GetMemberValue(layout, "_description"), "mega-rich-text-label");
            if (description is not null)
            {
                textSource = "partial";
                provisional = true;
            }
        }

        var sharedLabel = ResolveVisibleNodeText(
            Sts2LiveIntrospection.GetMemberValue(layout, "_sharedEventLabel"),
            "mega-label");
        if (eventModel is null && (title is not null || description is not null || sharedLabel is not null))
        {
            textSource = "layout-label";
            provisional = true;
        }

        var ancient = ResolveAncientPage(layout, ancientEvent, notices);
        if (ancient is not null)
        {
            eventModel = ancientEvent ?? eventModel;
            title = ancient.BannerTitle ?? ancient.Title ?? title;
            description = ancient.CurrentDialogue ?? description;
            textSource = ancient.TextSource ?? "partial";
            provisional = ancient.Provisional;
        }

        if (title is null && description is null && sharedLabel is null)
        {
            AddPageNotice(
                notices,
                "event-room-text-unavailable",
                "The live event room did not expose localized event title or description text through the current hooks.");
            return null;
        }

        if (title is null || description is null || provisional)
        {
            AddPageNotice(
                notices,
                "event-room-text-partial",
                "The live event room text was only partially resolved; missing values may require additional event layout hooks.");
        }

        return new EventRoomPageStateSnapshot(
            EventId: ResolveModelId(eventModel),
            EventType: eventModel?.GetType().FullName,
            Title: title,
            Description: description,
            SharedLabel: sharedLabel,
            TextSource: textSource,
            Provisional: provisional || title is null || description is null,
            Ancient: ancient);
    }

    private static object? ResolveAncientEventModel(object? layout, object? eventModel)
    {
        var ancientEvent = Sts2LiveIntrospection.GetMemberValue(layout, "_ancientEvent");
        if (ancientEvent is not null)
        {
            return ancientEvent;
        }

        if (eventModel is not null
            && (eventModel.GetType().FullName?.Contains("AncientEventModel", StringComparison.Ordinal) ?? false))
        {
            return eventModel;
        }

        return null;
    }

    private static AncientEventPageStateSnapshot? ResolveAncientPage(
        object? layout,
        object? ancientEvent,
        ICollection<StateNoticeSnapshot>? notices)
    {
        if (layout is null && ancientEvent is null)
        {
            return null;
        }

        var layoutLooksAncient = layout?.GetType().FullName?.Contains("NAncientEventLayout", StringComparison.Ordinal) ?? false;
        if (!layoutLooksAncient && ancientEvent is null)
        {
            return null;
        }

        var nameBanner = Sts2LiveIntrospection.GetMemberValue(layout, "_ancientNameBanner");
        var title = ResolveRichText(Sts2LiveIntrospection.GetMemberValue(ancientEvent, "Title"), "loc-string");
        var bannerTitle = ResolveVisibleNodeText(
            Sts2LiveIntrospection.GetMemberValue(nameBanner, "_titleLabel") ?? FindNamedDescendant(nameBanner, "Title"),
            "mega-rich-text-label") ?? ResolveAncientBannerTitle(title);
        var epithet = ResolveVisibleNodeText(
            Sts2LiveIntrospection.GetMemberValue(nameBanner, "_epithetLabel") ?? FindNamedDescendant(nameBanner, "Epithet"),
            "mega-label") ?? ResolveRichText(Sts2LiveIntrospection.GetMemberValue(ancientEvent, "Epithet"), "loc-string");

        var currentDialogueIndex = ResolveInt(Sts2LiveIntrospection.GetMemberValue(layout, "_currentDialogueLine"), 0);
        var dialogueLines = ResolveAncientDialogueLines(layout, currentDialogueIndex);
        var currentLine = dialogueLines.FirstOrDefault(line => line.Current)
            ?? dialogueLines.FirstOrDefault(line => line.Index == currentDialogueIndex);
        var currentDialogue = currentLine?.Text
            ?? ResolveAncientDialogueModelText(ResolveAncientDialogueModelAt(layout, currentDialogueIndex));
        var currentSpeaker = currentLine?.Speaker
            ?? NormalizeRichText(Sts2LiveIntrospection.GetMemberValue(ResolveAncientDialogueModelAt(layout, currentDialogueIndex), "Speaker")?.ToString());
        var nextButtonText = ResolveAncientNextButtonText(layout, currentDialogueIndex);

        var textSource = bannerTitle?.Provisional == false || currentDialogue?.Provisional == false || epithet?.Provisional == false
            ? "ancient-event-model"
            : "ancient-layout-label";
        if (bannerTitle?.Source is "mega-rich-text-label" || currentDialogue?.Source is "mega-rich-text-label" || epithet?.Source is "mega-label")
        {
            textSource = "ancient-layout-label";
        }

        var provisional = title is null || bannerTitle is null || epithet is null || currentDialogue is null;
        if (provisional)
        {
            textSource = "partial";
            AddAncientPageNotice(
                notices,
                "event-room-ancient-text-partial",
                "The live ancient event room text was only partially resolved; missing values may require additional ancient layout hooks.");
        }

        if (title is null && bannerTitle is null && epithet is null && currentDialogue is null && dialogueLines.Count == 0)
        {
            AddAncientPageNotice(
                notices,
                "event-room-ancient-text-unavailable",
                "The live ancient event room did not expose localized ancient event text through the current hooks.");
            return null;
        }

        return new AncientEventPageStateSnapshot(
            Title: title,
            BannerTitle: bannerTitle,
            Epithet: epithet,
            CurrentDialogue: currentDialogue,
            CurrentDialogueIndex: currentDialogueIndex,
            DialogueLines: dialogueLines,
            CurrentSpeaker: currentSpeaker,
            NextButtonText: nextButtonText,
            TextSource: textSource,
            Provisional: provisional);
    }

    private static IReadOnlyList<AncientEventDialogueLineStateSnapshot> ResolveAncientDialogueLines(
        object? layout,
        int currentDialogueIndex)
    {
        var lines = new List<AncientEventDialogueLineStateSnapshot>();
        var dialogueContainer = Sts2LiveIntrospection.GetMemberValue(layout, "_dialogueContainer");
        foreach (var child in EnumerateChildren(dialogueContainer))
        {
            var lineModel = Sts2LiveIntrospection.GetMemberValue(child, "_line");
            var looksLikeDialogueLine = child.GetType().FullName?.Contains("AncientDialogueLine", StringComparison.Ordinal) ?? false;
            if (lineModel is null && !looksLikeDialogueLine)
            {
                continue;
            }

            var index = lines.Count;
            var text = ResolveVisibleNodeText(FindNamedDescendant(child, "Text"), "mega-rich-text-label")
                ?? ResolveAncientDialogueModelText(lineModel);
            var speaker = NormalizeRichText(Sts2LiveIntrospection.GetMemberValue(lineModel, "Speaker")?.ToString());
            lines.Add(new AncientEventDialogueLineStateSnapshot(
                Index: index,
                Text: text,
                Speaker: speaker,
                Current: index == currentDialogueIndex,
                Visible: ResolveVisible(child)));
        }

        if (lines.Count > 0)
        {
            return lines;
        }

        var dialogue = Sts2LiveIntrospection.GetMemberValue(layout, "_dialogue") as IEnumerable;
        if (dialogue is null)
        {
            return lines;
        }

        foreach (var lineModel in dialogue)
        {
            if (lineModel is null)
            {
                continue;
            }

            var index = lines.Count;
            lines.Add(new AncientEventDialogueLineStateSnapshot(
                Index: index,
                Text: ResolveAncientDialogueModelText(lineModel),
                Speaker: NormalizeRichText(Sts2LiveIntrospection.GetMemberValue(lineModel, "Speaker")?.ToString()),
                Current: index == currentDialogueIndex,
                Visible: index == currentDialogueIndex));
        }

        return lines;
    }

    private static object? ResolveAncientDialogueModelAt(object? layout, int index)
    {
        var dialogue = Sts2LiveIntrospection.GetMemberValue(layout, "_dialogue") as IEnumerable;
        if (dialogue is null || index < 0)
        {
            return null;
        }

        var current = 0;
        foreach (var line in dialogue)
        {
            if (current == index)
            {
                return line;
            }

            current++;
        }

        return null;
    }

    private static RichLocalizedTextSnapshot? ResolveAncientDialogueModelText(object? lineModel)
        => ResolveRichText(Sts2LiveIntrospection.GetMemberValue(lineModel, "LineText"), "loc-string");

    private static RichLocalizedTextSnapshot? ResolveAncientNextButtonText(object? layout, int currentDialogueIndex)
    {
        var label = Sts2LiveIntrospection.GetMemberValue(layout, "_fakeNextButtonLabel");
        if (label is null || !ResolveVisible(label))
        {
            return null;
        }

        var labelText = ResolveRichText(label, "mega-label");
        if (labelText is not null)
        {
            return labelText;
        }

        return ResolveRichText(
            Sts2LiveIntrospection.GetMemberValue(ResolveAncientDialogueModelAt(layout, currentDialogueIndex), "NextButtonText"),
            "loc-string");
    }

    private static RichLocalizedTextSnapshot? ResolveAncientBannerTitle(RichLocalizedTextSnapshot? title)
    {
        if (title is null)
        {
            return null;
        }

        return title with
        {
            Text = $"[ancient_banner]{title.Text.ToUpperInvariant()}[/ancient_banner]",
            Source = title.Source,
            Provisional = title.Provisional,
        };
    }

    private static string ResolvePerspective(string? playerId)
        => string.IsNullOrWhiteSpace(playerId) ? "shared" : "local";

    private static object? ResolveFakeMerchantOpenButton(object? fakeMerchant)
    {
        var memberButton = Sts2LiveIntrospection.GetMemberValue(fakeMerchant, "MerchantButton");
        if (memberButton is not null)
        {
            return memberButton;
        }

        return FindDescendant(fakeMerchant, static child =>
            string.Equals(Sts2LiveIntrospection.GetMemberValue(child, "Name")?.ToString(), "ProceedButton", StringComparison.Ordinal)
            || Sts2LiveIntrospection.IsType(child, "MegaCrit.Sts2.Core.Nodes.CommonUi.NProceedButton"));
    }

    private static object? FindDescendant(object? root, Func<object, bool> predicate)
    {
        if (root is null)
        {
            return null;
        }

        foreach (var child in EnumerateChildren(root))
        {
            if (predicate(child))
            {
                return child;
            }

            var nested = FindDescendant(child, predicate);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static object? FindNamedDescendant(object? root, string name)
        => FindDescendant(root, child =>
            string.Equals(Sts2LiveIntrospection.GetMemberValue(child, "Name")?.ToString(), name, StringComparison.Ordinal)
            || string.Equals(child.GetType().Name, name, StringComparison.Ordinal));

    private static IEnumerable<object> EnumerateChildren(object? target)
    {
        if (target is null)
        {
            yield break;
        }

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

    private static bool IsControlDisabled(object control)
    {
        if (ResolveBool(control, "Disabled"))
        {
            return true;
        }

        var isEnabled = Sts2LiveIntrospection.GetMemberValue(control, "IsEnabled");
        if (isEnabled is bool enabled && !enabled)
        {
            return true;
        }

        return ResolveBool(control, "IsDisabled");
    }

    private static bool ResolveVisible(object control)
        => Sts2LiveIntrospection.GetMemberValue(control, "Visible") switch
        {
            bool visible => visible,
            _ => true,
        };

    private static string ResolveOptionId(
        object option,
        int optionIndex,
        ICollection<StateNoticeSnapshot>? notices)
    {
        var optionId = Sts2EventRoomIds.ResolveOptionId(
            Sts2LiveIntrospection.GetMemberValue(option, "TextKey")?.ToString(),
            ResolveBool(option, "IsProceed"),
            ResolveOptionalLabel(option, fallback: null),
            optionIndex,
            out var fallbackKind);
        AddFallbackNotice(notices, fallbackKind);
        return optionId;
    }

    private static string ResolveLabel(object option, string fallback)
        => ResolveOptionalLabel(option, fallback) ?? fallback;

    private static string? ResolveOptionalLabel(object option, string? fallback)
    {
        return ResolveLocStringLabel(Sts2LiveIntrospection.GetMemberValue(option, "Title"))
            ?? ResolveLocStringLabel(Sts2LiveIntrospection.GetMemberValue(option, "Description"))
            ?? ResolveLocStringLabel(Sts2LiveIntrospection.GetMemberValue(option, "HistoryName"))
            ?? fallback;
    }

    private static string? ResolveLocStringLabel(object? value)
    {
        return Normalize(TryInvokeText(value, "GetFormattedText"))
            ?? Normalize(TryInvokeText(value, "GetRawText"))
            ?? Normalize(Sts2LiveIntrospection.GetMemberValue(value, "Text")?.ToString())
            ?? Normalize(value?.ToString());
    }

    private static RichLocalizedTextSnapshot? ResolveRichText(object? value, string source)
    {
        var formatted = NormalizeRichText(TryInvokeText(value, "GetFormattedText"));
        var raw = NormalizeRichText(TryInvokeText(value, "GetRawText"));
        var text = formatted
            ?? raw
            ?? NormalizeRichText(Sts2LiveIntrospection.GetMemberValue(value, "Text")?.ToString())
            ?? NormalizeRichText(value is string ? value.ToString() : null);
        if (text is null)
        {
            return null;
        }

        return new RichLocalizedTextSnapshot(
            Text: text,
            RawText: raw is not null && !string.Equals(raw, text, StringComparison.Ordinal) ? raw : null,
            LocTable: NormalizeRichText(Sts2LiveIntrospection.GetMemberValue(value, "LocTable")?.ToString()),
            LocKey: NormalizeRichText(Sts2LiveIntrospection.GetMemberValue(value, "LocEntryKey")?.ToString()),
            Source: source,
            Provisional: !string.Equals(source, "loc-string", StringComparison.Ordinal));
    }

    private static string? TryInvokeText(object? value, string methodName)
    {
        try
        {
            return Sts2LiveIntrospection.InvokeMethod(value, methodName)?.ToString();
        }
        catch (TargetInvocationException exception) when (IsLocalizationFailure(exception))
        {
            return null;
        }
    }

    private static bool IsLocalizationFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var type = current.GetType();
            if (string.Equals(type.Name, "LocException", StringComparison.Ordinal)
                || string.Equals(type.FullName, "MegaCrit.Sts2.Core.Localization.LocException", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static RichLocalizedTextSnapshot? ResolveVisibleNodeText(object? node, string source)
    {
        if (node is null || !ResolveVisible(node))
        {
            return null;
        }

        return ResolveRichText(node, source);
    }

    private static RichLocalizedTextSnapshot? ResolveEventOptionButtonText(object? button)
    {
        var label = Sts2LiveIntrospection.GetMemberValue(button, "_label") ?? FindNamedDescendant(button, "Text");
        return ResolveVisibleNodeText(label, "mega-rich-text-label");
    }

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return NormalizeRichText(text.Split('\n', 2)[0]);
    }

    private static RichLocalizedTextSnapshot? RemainingLines(RichLocalizedTextSnapshot? text)
    {
        if (text is null)
        {
            return null;
        }

        var parts = text.Text.Split('\n', 2);
        if (parts.Length < 2)
        {
            return null;
        }

        var remaining = NormalizeRichText(parts[1]);
        return remaining is null ? null : text with { Text = remaining };
    }

    private static string? ResolveModelId(object? model)
    {
        var id = Sts2LiveIntrospection.GetMemberValue(model, "Id")
            ?? Sts2LiveIntrospection.GetMemberValue(model, "ModelId")
            ?? Sts2LiveIntrospection.GetMemberValue(model, "EventId");
        return NormalizeRichText(id?.ToString());
    }

    private static bool ResolveBool(object target, string memberName, bool defaultValue = false)
    {
        return Sts2LiveIntrospection.GetMemberValue(target, memberName) switch
        {
            bool value => value,
            _ => defaultValue,
        };
    }

    private static int ResolveInt(object? value, int defaultValue)
    {
        return value switch
        {
            int intValue => intValue,
            uint uintValue when uintValue <= int.MaxValue => (int)uintValue,
            long longValue when longValue >= int.MinValue && longValue <= int.MaxValue => (int)longValue,
            _ when int.TryParse(value?.ToString(), out var parsed) => parsed,
            _ => defaultValue,
        };
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.ReplaceLineEndings(" ").Trim();

    private static string? NormalizeRichText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void AddNotice(
        ICollection<StateNoticeSnapshot>? notices,
        string code,
        string message)
    {
        if (notices is null || notices.Any(notice => notice.Code == code))
        {
            return;
        }

        Sts2StateNotice.AddPartialOnce(notices, code, message, "choices", nameof(Sts2EventRoomScreenInspector));
    }

    private static void AddFallbackNotice(
        ICollection<StateNoticeSnapshot>? notices,
        Sts2EventRoomIds.OptionIdFallbackKind fallbackKind)
    {
        if (fallbackKind == Sts2EventRoomIds.OptionIdFallbackKind.Label)
        {
            AddNotice(
                notices,
                "event-room-choice-id-label-fallback",
                "Event-room choice ids fell back to visible text because the live option did not expose a stable text key.");
            return;
        }

        if (fallbackKind == Sts2EventRoomIds.OptionIdFallbackKind.Order)
        {
            AddNotice(
                notices,
                "event-room-choice-id-order-fallback",
                "Event-room choice ids fell back to screen ordering because the live option did not expose a stable key or label.");
        }
    }

    private static void AddPageNotice(
        ICollection<StateNoticeSnapshot>? notices,
        string code,
        string message)
    {
        if (notices is null || notices.Any(notice => notice.Code == code))
        {
            return;
        }

        Sts2StateNotice.AddPartialOnce(notices, code, message, "eventRoom.page", nameof(Sts2EventRoomScreenInspector));
    }

    private static void AddAncientPageNotice(
        ICollection<StateNoticeSnapshot>? notices,
        string code,
        string message)
    {
        if (notices is null || notices.Any(notice => notice.Code == code))
        {
            return;
        }

        Sts2StateNotice.AddPartialOnce(notices, code, message, "eventRoom.page.ancient", nameof(Sts2EventRoomScreenInspector));
    }
}

internal sealed record ResolvedEventRoomChoice(
    ChoiceSnapshot Snapshot,
    object Button,
    object? Option,
    int Index,
    bool IsExecutable,
    EventRoomChoiceExecutionKind ExecutionKind,
    string PreferredAction = "choose",
    RichLocalizedTextSnapshot? DescriptionText = null);

internal enum EventRoomChoiceExecutionKind
{
    Option,
    FakeMerchantOpenShop,
}
