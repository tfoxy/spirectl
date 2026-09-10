using System.Collections;
using System.Runtime.CompilerServices;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live;

public static class Sts2CardOverlayInspector
{
    public static CardOverlayInspection Inspect(
        object? overlay,
        ScreenLocatorResult screen,
        string? defaultPlayerId,
        ICollection<StateNoticeSnapshot>? notices = null)
    {
        var localNotices = notices ?? new List<StateNoticeSnapshot>();
        var cards = ResolveCards(overlay, defaultPlayerId, localNotices);
        if (cards.Count == 0)
        {
            AddNotice(
                localNotices,
                "card-overlay-card-partial",
                "The visible card overlay did not expose an observable card model through the checked presentation members.",
                "cardOverlay.cards",
                "partial");
        }

        var cardOwnerIds = cards
            .Select(card => card.OwnerPlayerId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var overlayOwnerPlayerId = ResolveOwnerPlayerId(overlay);
        var ownerPlayerId = overlayOwnerPlayerId ?? (cardOwnerIds.Length == 1 ? cardOwnerIds[0] : null);
        if (string.IsNullOrWhiteSpace(overlayOwnerPlayerId) && cardOwnerIds.Length > 1)
        {
            AddNotice(
                localNotices,
                "card-overlay-ambiguous-ownership",
                "The visible card overlay exposed multiple owner hints, so no overlay owner player was inferred.",
                "cardOverlay.ownerPlayerId",
                "partial");
        }

        var close = ResolveHookAffordance(
            overlay,
            "card-overlay:close",
            "Close",
            ownerPlayerId,
            "close-overlay",
            "_closeButton",
            "CloseButton",
            "OnCloseButtonReleased",
            "OnClose",
            "CloseOverlay");
        var back = ResolveHookAffordance(
            overlay,
            "card-overlay:back",
            "Back",
            ownerPlayerId,
            "back-overlay",
            "_backButton",
            "BackButton",
            "OnBackButtonReleased",
            "OnBack",
            "GoBack");

        var followThrough = ResolveFallbackControls(overlay, ownerPlayerId, [close?.Id, back?.Id]);
        var previewText = ResolveText(overlay, "PreviewText", "Preview", "Text", "Description", "_previewText", "_descriptionLabel");
        var source = ResolveText(overlay, "SourceScreenType", "SourceScreen", "ParentScreenType", "UnderlyingScreenType");
        var breadcrumb = new OverlayBreadcrumbSnapshot(
            string.IsNullOrWhiteSpace(source) ? "unknown" : source!,
            string.IsNullOrWhiteSpace(source) ? null : source,
            Source: screen.Source,
            RawType: screen.ScreenRawType,
            ClassName: screen.ScreenClassName,
            OwnerPlayerId: ownerPlayerId,
            Perspective: ResolvePerspective(ownerPlayerId));

        if (close is null && back is null)
        {
            AddNotice(
                localNotices,
                "card-overlay-close-hook-unavailable",
                "The visible card overlay did not expose a validated close or back hook, so no close/back action was advertised.",
                "cardOverlay.close",
                "partial");
        }

        var passive = ResolveBool(overlay, "Passive", "IsPassive") == true;
        var blocking = ResolveBool(overlay, "Blocking", "IsBlocking") ?? !passive;
        var state = new CardOverlayStateSnapshot(
            cards,
            PreviewText: previewText,
            Breadcrumbs: [breadcrumb],
            Blocking: blocking,
            Passive: passive,
            OverlayPolicy: passive ? "passive" : blocking ? "blocking" : "unsupported",
            OwnerPlayerId: ownerPlayerId,
            Perspective: ResolvePerspective(ownerPlayerId),
            Close: close,
            Back: back,
            FollowThroughControls: followThrough,
            Provisional: localNotices.Count > 0);

        var choices = followThrough
            .Where(control => control.Enabled)
            .Select(control => new ChoiceSnapshot(
                control.Id,
                control.Label,
                "card-overlay-control",
                Provisional: control.Provisional,
                OwnerPlayerId: control.OwnerPlayerId,
                ChoiceKind: control.ChoiceKind,
                IntentKind: control.IntentKind,
                Perspective: control.Perspective,
                PreferredAction: control.PreferredAction))
            .ToArray();

        return new CardOverlayInspection(state, choices);
    }

    private static IReadOnlyList<CardStateSnapshot> ResolveCards(
        object? overlay,
        string? defaultPlayerId,
        ICollection<StateNoticeSnapshot> notices)
    {
        var result = new List<CardStateSnapshot>();
        foreach (var card in EnumerateCardCandidates(overlay).DistinctBy(RuntimeHelpers.GetHashCode))
        {
            var ownerPlayerId = ResolveOwnerPlayerId(card) ?? defaultPlayerId;
            var name = ResolveText(card, "Name", "Title", "DisplayName") ?? card.GetType().Name;
            var cost = ResolveInt(card, "Cost", "CurrentCost", "EnergyCost") ?? 0;
            var id = ResolveStableId(card) ?? $"card-overlay:{Slug(name)}:{result.Count}";
            result.Add(Sts2PresentationStateResolver.WithCardPresentation(new CardStateSnapshot(
                id,
                name,
                cost,
                ownerPlayerId,
                Playable: false,
                UnplayableReason: "inspection-only",
                TargetIds: [],
                Upgraded: ResolveBool(card, "Upgraded", "IsUpgraded") ?? false),
                card));
        }

        if (result.Count > 1 && result.Select(card => card.OwnerPlayerId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            AddNotice(
                notices,
                "card-overlay-ambiguous-ownership",
                "The visible card overlay exposed cards for multiple owners.",
                "cardOverlay.cards.ownerPlayerId",
                "partial");
        }

        return result;
    }

    private static IEnumerable<object> EnumerateCardCandidates(object? overlay)
    {
        foreach (var memberName in new[] { "Card", "CardModel", "Model", "PreviewCard", "HoveredCard", "_card", "_cardModel", "_previewCard" })
        {
            var value = Sts2LiveIntrospection.GetMemberValue(overlay, memberName);
            if (LooksLikeCard(value))
            {
                yield return value!;
            }
        }

        foreach (var memberName in new[] { "Cards", "CardModels", "PreviewCards", "_cards" })
        {
            if (Sts2LiveIntrospection.GetMemberValue(overlay, memberName) is not IEnumerable items)
            {
                continue;
            }

            foreach (var item in items)
            {
                if (LooksLikeCard(item))
                {
                    yield return item!;
                }
            }
        }

        foreach (var memberName in new[] { "CardNode", "CardHolder", "Holder", "_cardNode", "_cardHolder" })
        {
            var node = Sts2LiveIntrospection.GetMemberValue(overlay, memberName);
            var card = ResolveCardFromNode(node);
            if (LooksLikeCard(card))
            {
                yield return card!;
            }
        }
    }

    private static object? ResolveCardFromNode(object? node)
        => Sts2LiveIntrospection.GetMemberValue(node, "Card")
            ?? Sts2LiveIntrospection.GetMemberValue(node, "CardModel")
            ?? Sts2LiveIntrospection.GetMemberValue(node, "Model")
            ?? Sts2LiveIntrospection.GetMemberValue(node, "_card")
            ?? Sts2LiveIntrospection.GetMemberValue(node, "_cardModel");

    private static OverlayAffordanceSnapshot? ResolveHookAffordance(
        object? overlay,
        string id,
        string fallbackLabel,
        string? ownerPlayerId,
        string intentKind,
        string controlMemberName,
        string controlMemberName2,
        params string[] methodNames)
    {
        var control = Sts2LiveIntrospection.GetMemberValue(overlay, controlMemberName)
            ?? Sts2LiveIntrospection.GetMemberValue(overlay, controlMemberName2);
        var hasHook = methodNames.Any(methodName => HasParameterlessMethod(overlay, methodName));
        if (!hasHook)
        {
            return null;
        }

        if (control is not null && !IsVisible(control))
        {
            return null;
        }

        return new OverlayAffordanceSnapshot(
            id,
            ResolveText(control, "Text", "Label", "Name") ?? fallbackLabel,
            control is null || IsExecutable(control),
            OwnerPlayerId: ownerPlayerId,
            ChoiceKind: "card-overlay-flow",
            IntentKind: intentKind,
            PreferredAction: "choose",
            Perspective: ResolvePerspective(ownerPlayerId),
            Provisional: false);
    }

    private static IReadOnlyList<OverlayAffordanceSnapshot> ResolveFallbackControls(
        object? overlay,
        string? ownerPlayerId,
        IReadOnlyList<string?> reservedIds)
    {
        var controls = new List<OverlayAffordanceSnapshot>();
        foreach (var candidate in EnumerateVisibleControls(overlay))
        {
            var label = ResolveText(candidate, "Text", "Label", "Name") ?? "Overlay control";
            var id = ResolveStableId(candidate) ?? $"card-overlay:control:{Slug(label)}";
            if (reservedIds.Contains(id, StringComparer.Ordinal) || controls.Any(control => control.Id == id))
            {
                continue;
            }

            controls.Add(new OverlayAffordanceSnapshot(
                id,
                label,
                IsExecutable(candidate),
                OwnerPlayerId: ownerPlayerId,
                ChoiceKind: "card-overlay-control",
                IntentKind: "choose-overlay-control",
                PreferredAction: "choose",
                Perspective: ResolvePerspective(ownerPlayerId),
                Provisional: true));
        }

        return controls;
    }

    private static IEnumerable<object> EnumerateVisibleControls(object? overlay)
    {
        foreach (var memberName in new[] { "Controls", "Buttons", "FollowThroughControls", "_controls", "_buttons" })
        {
            if (Sts2LiveIntrospection.GetMemberValue(overlay, memberName) is not IEnumerable items)
            {
                continue;
            }

            foreach (var item in items)
            {
                if (item is not null && IsVisible(item))
                {
                    yield return item;
                }
            }
        }
    }

    private static bool LooksLikeCard(object? value)
        => value is not null
            && value is not string
            && (ResolveText(value, "Name", "Title", "DisplayName") is not null
                || Sts2LiveIntrospection.GetMemberValue(value, "Id") is not null
                || Sts2LiveIntrospection.GetMemberValue(value, "Key") is not null);

    private static string? ResolveStableId(object? value)
    {
        var id = Sts2LiveIntrospection.GetMemberValue(value, "Id")
            ?? Sts2LiveIntrospection.GetMemberValue(value, "ID")
            ?? Sts2LiveIntrospection.GetMemberValue(value, "Key")
            ?? Sts2LiveIntrospection.GetMemberValue(value, "ModelId");
        return ResolveText(id, "Entry") ?? id?.ToString();
    }

    private static string? ResolveOwnerPlayerId(object? value)
    {
        var owner = Sts2LiveIntrospection.GetMemberValue(value, "OwnerPlayerId")
            ?? Sts2LiveIntrospection.GetMemberValue(value, "PlayerId")
            ?? Sts2LiveIntrospection.GetMemberValue(value, "Owner")
            ?? Sts2LiveIntrospection.GetMemberValue(value, "Player");
        return ResolveText(owner, "Id", "PlayerId") ?? owner?.ToString();
    }

    private static string? ResolveText(object? value, params string[] memberNames)
    {
        if (value is null)
        {
            return null;
        }

        if (value is string text)
        {
            return Normalize(text);
        }

        foreach (var memberName in memberNames)
        {
            var member = Sts2LiveIntrospection.GetMemberValue(value, memberName);
            var textValue = member is string memberText
                ? memberText
                : Sts2LiveIntrospection.InvokeMethod(member, "GetFormattedText")?.ToString()
                    ?? Sts2LiveIntrospection.InvokeMethod(member, "GetRawText")?.ToString()
                    ?? member?.ToString();
            var normalized = Normalize(textValue);
            if (normalized is not null)
            {
                return normalized;
            }
        }

        return null;
    }

    private static int? ResolveInt(object? value, params string[] memberNames)
    {
        foreach (var memberName in memberNames)
        {
            var member = Sts2LiveIntrospection.GetMemberValue(value, memberName);
            if (member is int intValue)
            {
                return intValue;
            }
        }

        return null;
    }

    private static bool? ResolveBool(object? value, params string[] memberNames)
    {
        foreach (var memberName in memberNames)
        {
            if (Sts2LiveIntrospection.GetMemberValue(value, memberName) is bool boolValue)
            {
                return boolValue;
            }
        }

        return null;
    }

    private static bool IsVisible(object value)
        => ResolveBool(value, "Visible", "IsVisible") ?? true;

    private static bool IsExecutable(object value)
        => ResolveBool(value, "Disabled", "IsDisabled") switch
        {
            true => false,
            false => true,
            _ => ResolveBool(value, "Enabled", "IsEnabled") ?? true,
        };

    private static bool HasParameterlessMethod(object? value, string methodName)
        => value?.GetType().GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Any(method => string.Equals(method.Name, methodName, StringComparison.Ordinal) && method.GetParameters().Length == 0) == true;

    private static string? ResolvePerspective(string? ownerPlayerId)
        => OwnershipMetadata.ResolvePerspective(ownerPlayerId);

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Slug(string value)
        => string.Join("-", value.Trim().ToLowerInvariant().Split([' ', '_', ':', '.', '/'], StringSplitOptions.RemoveEmptyEntries));

    private static void AddNotice(
        ICollection<StateNoticeSnapshot> notices,
        string code,
        string message,
        string path,
        string severity)
    {
        if (notices.Any(notice => notice.Code == code && notice.Path == path))
        {
            return;
        }

        notices.Add(new StateNoticeSnapshot(
            code,
            message,
            Provisional: true,
            Path: path,
            Severity: severity,
            Source: nameof(Sts2CardOverlayInspector),
            Stability: "stable"));
    }
}

public sealed record CardOverlayInspection(
    CardOverlayStateSnapshot State,
    IReadOnlyList<ChoiceSnapshot> Choices);
