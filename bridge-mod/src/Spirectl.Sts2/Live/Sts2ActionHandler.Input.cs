using Godot;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live;

// Element-addressed pointer hover + keyboard input replay, for browser-driven play (single controller). These
// inject REAL Godot input (the game does its own hit-testing at the resolved point), so anything the live UI
// reacts to works without per-screen semantic mapping. Element clicks reuse ExecuteMouseClick (it calls
// TryResolveElementPoint when no coordinate is supplied). All handlers run on the main-thread dispatcher.
public sealed partial class Sts2ActionHandler
{
    // The mouse button currently HELD by a browser drag (press without release), or null. All input handlers
    // run on the single main-thread dispatcher, so this needs no lock. While set, the continuous hover/move
    // stream is injected as drag-MOTION (the button mask rides the motion events) instead of a free hover —
    // that's what lets a card be dragged from hand to target. A release or a full click clears it.
    private MouseButton? _heldMouseButton;
    // Last injected motion point, so motion events carry a correct `Relative` delta (some Godot drag handlers
    // read it). Shared across hover + click injection.
    private Vector2? _lastMotionPoint;

    // The button-mask for whatever is held right now (none when not dragging).
    private MouseButtonMask HeldMouseMask()
        => _heldMouseButton is { } button ? ToMouseButtonMask(button) : (MouseButtonMask)0;

    // Inject a mouse-motion at a window point with an explicit held-button mask (mask != 0 => Godot sees a drag).
    private void InjectMotion(Vector2 point, MouseButtonMask mask)
    {
        var relative = _lastMotionPoint is { } last ? point - last : Vector2.Zero;
        Input.ParseInputEvent(new InputEventMouseMotion
        {
            Position = point,
            GlobalPosition = point,
            Relative = relative,
            ButtonMask = mask,
        });
        _lastMotionPoint = point;
    }

    private ActionExecutionResult ExecuteHoverElement(SemanticActionRequest request)
    {
        Vector2 position;
        Node? node;
        if (!string.IsNullOrWhiteSpace(request.ElementId))
        {
            if (!TryResolveElementPoint(request, out position, out node, out var failure))
            {
                return failure!;
            }
        }
        else if (request.MouseX is not null && request.MouseY is not null && request.MouseX >= 0 && request.MouseY >= 0)
        {
            position = new Vector2(request.MouseX.Value, request.MouseY.Value);
            node = null;
        }
        else
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "hover-element requires an element id or non-negative coordinates.");
        }

        // Convert canvas/GUI coords → window pixels so hover lands correctly when the window != base size.
        var inject = Sts2InputCoordinates.CanvasToWindow(position);
        Input.WarpMouse(inject);
        // If a button is held (a drag is in progress), the motion carries its mask so the game tracks a drag,
        // not a free hover — this turns the continuous hover stream into the drag itself.
        InjectMotion(inject, HeldMouseMask());
        // Drive the Control's own hover state where it exposes the same hook the dev scene-hover uses.
        if (node is Control control)
        {
            Sts2LiveIntrospection.TryInvokeParameterlessMethod(control, "OnFocus");
        }

        return ActionExecutionResult.Success(
            actionInstanceId: $"action:hover-element:{request.RequestId}",
            kind: request.Kind,
            message: $"Injected hover at canvas ({position.X:0},{position.Y:0}) → window ({inject.X:0},{inject.Y:0}).",
            provisional: true);
    }

    private ActionExecutionResult ExecuteKeyInput(SemanticActionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Key) || !Sts2BrowserKeyMap.TryMap(request.Key, out var keycode))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: $"key-input has no Godot key mapping for '{request.Key}'.");
        }

        var (ctrl, shift, alt, meta) = ParseModifiers(request.KeyModifiers);

        void Send(bool pressed)
        {
            Input.ParseInputEvent(new InputEventKey
            {
                Keycode = keycode,
                PhysicalKeycode = keycode,
                Pressed = pressed,
                Echo = false,
                CtrlPressed = ctrl,
                ShiftPressed = shift,
                AltPressed = alt,
                MetaPressed = meta,
            });
        }

        // Honor an explicit press/release; otherwise inject a full press+release (a "tap").
        if (request.KeyPressed is bool pressed)
        {
            Send(pressed);
        }
        else
        {
            Send(true);
            Send(false);
        }

        var message = $"Injected key '{request.Key}' (pressed={request.KeyPressed?.ToString().ToLowerInvariant() ?? "tap"}).";
        _logStream.Write(BridgeLogLevel.Info, "bridge.action", message);
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:key-input:{request.RequestId}",
            kind: request.Kind,
            message: message,
            provisional: true);
    }

    // Resolve a stable node instance id (GetInstanceId, the same id Sts2RuntimeSceneWatcher streams to the
    // mirror) to a viewport point: a Control's global rect (offset within it, centered by default), or a
    // CanvasItem's global canvas origin. `node` is the resolved node (for hover OnFocus); null on failure.
    private bool TryResolveElementPoint(
        SemanticActionRequest request,
        out Vector2 point,
        out Node? node,
        out ActionExecutionResult? failure)
    {
        point = Vector2.Zero;
        node = null;
        failure = null;

        var elementId = request.ElementId;
        if (string.IsNullOrWhiteSpace(elementId) || !ulong.TryParse(elementId, out var instanceId))
        {
            failure = ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "element input requires a numeric element id.");
            return false;
        }

        if (!GodotObject.IsInstanceIdValid(instanceId) || GodotObject.InstanceFromId(instanceId) is not Node resolved)
        {
            failure = ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.StaleId,
                message: $"element '{elementId}' is no longer a live node.");
            return false;
        }

        node = resolved;
        Rect2 rect;
        if (resolved is Control control)
        {
            if (!control.IsVisibleInTree())
            {
                failure = ActionExecutionResult.Failure(
                    kind: request.Kind,
                    code: ActionFailureCode.NotVisible,
                    message: $"element '{elementId}' is not visible in the current scene tree.");
                return false;
            }

            rect = control.GetGlobalRect();
        }
        else if (resolved is CanvasItem canvasItem)
        {
            // Node2D / other CanvasItems have no rect — inject at the global canvas origin (offset ignored).
            rect = new Rect2(canvasItem.GetGlobalTransformWithCanvas().Origin, Vector2.Zero);
        }
        else
        {
            failure = ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: $"element '{elementId}' is not a positionable (CanvasItem) node.");
            return false;
        }

        if (rect.Size.X <= 0 || rect.Size.Y <= 0)
        {
            point = rect.Position;
            return true;
        }

        var ox = request.OffsetX is { } x and >= 0 and <= 1 ? x : 0.5;
        var oy = request.OffsetY is { } y and >= 0 and <= 1 ? y : 0.5;
        point = rect.Position + new Vector2((float)(rect.Size.X * ox), (float)(rect.Size.Y * oy));
        return true;
    }

    private static (bool Ctrl, bool Shift, bool Alt, bool Meta) ParseModifiers(string? modifiers)
    {
        if (string.IsNullOrWhiteSpace(modifiers))
        {
            return (false, false, false, false);
        }

        var ctrl = false;
        var shift = false;
        var alt = false;
        var meta = false;
        foreach (var token in modifiers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    ctrl = true;
                    break;
                case "shift":
                    shift = true;
                    break;
                case "alt":
                case "option":
                    alt = true;
                    break;
                case "meta":
                case "cmd":
                case "command":
                case "super":
                    meta = true;
                    break;
            }
        }

        return (ctrl, shift, alt, meta);
    }
}
