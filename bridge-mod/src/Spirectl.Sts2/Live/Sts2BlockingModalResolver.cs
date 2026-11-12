using System.Collections;
using System.Reflection;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.State;
#if ENABLE_STS2_LIVE_HOST
using Godot;
using MegaCrit.Sts2.Core.Nodes;
#endif

namespace Spirectl.Sts2.Live;

// Resolve only actual, visible modal children: the persistent backstop is not a modal.
public static class Sts2BlockingModalResolver
{
    public sealed record Modal(StateBlockingModalSnapshot State, IReadOnlyList<object> Buttons);

    public static Modal? ResolveLive()
    {
#if ENABLE_STS2_LIVE_HOST
        return Resolve(NGame.Instance?.GetNodeOrNull<Node>("ModalContainer"));
#else
        return null;
#endif
    }

    public static Modal? Resolve(object? container)
    {
        foreach (var node in Children(container).Reverse())
        {
            if (Read(node, "Name")?.ToString() == "Backstop" || !Visible(node))
            {
                continue;
            }

            var descendants = Walk(node).ToArray();
            var buttons = descendants.Where(n => Visible(n) && IsButton(n) && Read(n, "IsEnabled") is not false && Read(n, "Disabled") is not true).ToArray();
            var title = Text(descendants.FirstOrDefault(n => Read(n, "Name")?.ToString() == "Header"))
                ?? Text(descendants.FirstOrDefault(n => n.GetType().Name.EndsWith("Label", StringComparison.Ordinal) && !n.GetType().Name.Contains("RichText", StringComparison.Ordinal)));
            var body = Text(descendants.FirstOrDefault(n => Read(n, "Name")?.ToString() == "Description"))
                ?? Text(descendants.FirstOrDefault(n => n.GetType().Name.Contains("RichTextLabel", StringComparison.Ordinal)));
            var name = Read(node, "Name")?.ToString() ?? node.GetType().Name;
            return new Modal(new StateBlockingModalSnapshot(
                $"modal:{name}", name.Contains("Ftue", StringComparison.OrdinalIgnoreCase) ? "ftue" : "popup",
                node.GetType().Name, title, body,
                [.. buttons.Select((b, i) => new StateModalButtonSnapshot($"modal-button:{i}", Text(b) ?? Walk(b).Select(Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? Read(b, "Name")?.ToString() ?? "Button"))]), buttons);
        }

        return null;
    }

    public static ActionExecutionResult? Precheck(SemanticActionKind kind, Modal? modal)
        => modal is null || kind is SemanticActionKind.DismissModal or SemanticActionKind.MouseClick
            or SemanticActionKind.HoverElement or SemanticActionKind.KeyInput or SemanticActionKind.ControllerInput
            or SemanticActionKind.DisconnectClient or SemanticActionKind.SetClientName
            ? null
            : ActionExecutionResult.Failure(kind, ActionFailureCode.BlockedByModal,
                $"Blocked by modal '{modal.State.Title ?? modal.State.Id}'; run `sts2 act dismiss-modal`.",
                [new ActionFailureDetail("blocker", modal.State.Id, "run `sts2 act dismiss-modal`",
                    Blocker: new ModalBlockerSnapshot(modal.State.Id, modal.State.Kind, modal.State.Title))]);

    public static ActionExecutionResult Dismiss(SemanticActionRequest request, Modal? modal)
    {
        var modalId = request.Values?.GetValueOrDefault("modalId");
        var buttonId = request.Values?.GetValueOrDefault("buttonId");
        if (modal is null || (!string.IsNullOrEmpty(modalId) && modalId != modal.State.Id))
        {
            return ActionExecutionResult.Failure(request.Kind, ActionFailureCode.StaleId, "The requested blocking modal is no longer visible.");
        }

        if (string.IsNullOrEmpty(buttonId) && modal.Buttons.Count != 1)
        {
            return ActionExecutionResult.Failure(request.Kind, ActionFailureCode.InvalidArgument,
                "Specify --button using one of the visible modal button ids.",
                [.. modal.State.Buttons.Select(b => new ActionFailureDetail("buttonId", b.Id, b.Label))]);
        }

        var index = string.IsNullOrEmpty(buttonId) ? 0 : modal.State.Buttons.ToList().FindIndex(b => b.Id == buttonId);
        if (index < 0)
        {
            return ActionExecutionResult.Failure(request.Kind, ActionFailureCode.StaleId, "The requested modal button is no longer visible.");
        }

        var target = modal.Buttons[index];
        var forceClick = target.GetType().GetMethod("ForceClick", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
        if (forceClick is not null && forceClick.ReturnType == typeof(void))
        {
            forceClick.Invoke(target, null);
        }
#if ENABLE_STS2_LIVE_HOST
        else if (target is BaseButton button)
        {
            button.EmitSignal(BaseButton.SignalName.Pressed);
        }
#endif
        else
        {
            return ActionExecutionResult.Failure(request.Kind, ActionFailureCode.MissingHook, "No public semantic press hook exists for this modal button.");
        }
        return ActionExecutionResult.Success(request.RequestId, request.Kind,
            $"Pressed '{modal.State.Buttons[index].Label}' on '{modal.State.Id}'; the game is still closing it.",
            values: new Dictionary<string, string> { ["modalId"] = modal.State.Id, ["closed"] = "false", ["waitedMs"] = "0" });
    }

    public static async Task<ActionExecutionResult> WaitForDismissalAsync(
        ActionExecutionResult pressed, string modalId, Func<Modal?> resolve, Func<Task> nextFrame,
        int timeoutMs = 3000, Func<long>? elapsedMilliseconds = null)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        elapsedMilliseconds ??= () => timer.ElapsedMilliseconds;
        var closed = resolve()?.State.Id != modalId;
        while (!closed && elapsedMilliseconds() < timeoutMs)
        {
            var frame = nextFrame();
            var remainingMs = (int)Math.Max(0, timeoutMs - elapsedMilliseconds());
            if (await Task.WhenAny(frame, Task.Delay(remainingMs)) != frame)
            {
                break;
            }

            await frame;
            closed = resolve()?.State.Id != modalId;
        }

        return pressed with
        {
            Message = closed ? $"Closed '{modalId}'." : $"Pressed the button on '{modalId}'; the game is still closing it. Poll state until blockingModal is null.",
            Values = new Dictionary<string, string>
            {
                ["modalId"] = modalId,
                ["closed"] = closed ? "true" : "false",
                ["waitedMs"] = elapsedMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        };
    }

    private static object? Read(object? node, string name) => Sts2LiveIntrospection.GetMemberValue(node, name);
    private static string? Text(object? node) => Read(node, "Text")?.ToString();
    private static bool Visible(object node)
    {
#if ENABLE_STS2_LIVE_HOST
        if (node is Control control)
        {
            return control.IsVisibleInTree() && control.Size.X > 0 && control.Size.Y > 0;
        }
#endif
        return Read(node, "Visible") is true && Read(Read(node, "Size"), "X") is float x && x > 0
            && Read(Read(node, "Size"), "Y") is float y && y > 0;
    }
    private static bool IsButton(object node)
    {
        for (var type = node.GetType(); type is not null; type = type.BaseType)
        {
            if (type.Name.Contains("Button", StringComparison.Ordinal)) return true;
        }
        return false;
    }
    private static IEnumerable<object> Children(object? node)
    {
#if ENABLE_STS2_LIVE_HOST
        if (node is Node godotNode) return godotNode.GetChildren().Cast<object>();
#endif
        return (Read(node, "Children") as IEnumerable)?.Cast<object>() ?? [];
    }
    private static IEnumerable<object> Walk(object node)
    {
        foreach (var child in Children(node))
        {
            yield return child;
            foreach (var descendant in Walk(child)) yield return descendant;
        }
    }
}
