using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Live;

public sealed partial class Sts2ActionHandler
{
    /// <summary>
    /// One semantic action kind the embedded runtime carries out: what it advertises about itself, and the body
    /// that runs it.
    /// </summary>
    private sealed record EmbeddedRoute(
        ActionDescriptorSnapshot Descriptor,
        Func<Sts2ActionHandler, SemanticActionRequest, ActionExecutionResult> Execute);

    /// <summary>
    /// The embedded profile's action routes, and the single place the kinds an embedder can send are named. The
    /// dispatcher below routes by this table, and <c>Sts2ActionDescriptorCatalog</c> (the capabilities'
    /// <c>SupportedActions</c>) lists this table's descriptors, so what the runtime advertises and what it does
    /// cannot drift apart. A kind without a route answers <see cref="ActionFailureCode.InvalidAction"/>, exactly
    /// as an unknown kind does; the <see cref="SemanticActionKind"/> enum itself stays complete so its wire
    /// values never move. To add a kind, add its route here and, if its body sits in a <c>*.Full.cs</c> partial,
    /// move that body back into the main partial.
    /// </summary>
    private static readonly EmbeddedRoute[] EmbeddedRoutes =
    [
        // select-map-node and claim-reward are worded exactly as the full profile's catalog words them.
        RouteFor(
            "select-map-node", SemanticActionKind.SelectMapNode,
            "Select an executable map node by stable id.",
            "sts2 act select-map-node --node map-node:3:1",
            provisional: false, ActionImplementationStatus.Implemented,
            static (handler, request) => handler.ExecuteSelectMapNode(request),
            RouteParam("mapNodeId", true, "Stable map node id from state.choices[].id.")),
        RouteFor(
            "claim-reward", SemanticActionKind.ClaimReward,
            "Claim a visible reward by stable reward id.",
            "sts2 act claim-reward --reward reward:p1:0",
            provisional: true, ActionImplementationStatus.Scaffolded,
            static (handler, request) => handler.ExecuteClaimReward(request),
            RouteParam("rewardId", true, "Stable reward id from state.rewards.rewards[].id."),
            RouteParam("playerId", false, "Owning player id when explicit ownership is needed.")),
        RouteFor(
            "disconnect-client", SemanticActionKind.DisconnectClient,
            "Evict a remote client from the host by its net id.",
            string.Empty,
            provisional: false, ActionImplementationStatus.Implemented,
            static (handler, request) => handler.ExecuteDisconnectClient(request),
            RouteParam("playerId", true, "The remote peer's net id, e.g. 1002 or p:1002.")),
        RouteFor(
            "set-client-name", SemanticActionKind.SetClientName,
            "Override the display name shown for a remote client by its net id; an empty name clears the override.",
            string.Empty,
            provisional: false, ActionImplementationStatus.Implemented,
            static (handler, request) => handler.ExecuteSetClientName(request),
            RouteParam("playerId", true, "The remote peer's net id, e.g. 1002 or p:1002."),
            RouteParam("displayName", false, "Name to show for that peer; empty clears the override.")),
        RouteFor(
            "mouse-click", SemanticActionKind.MouseClick,
            "Replay a real mouse press and release at a viewport point or at a scene node by element id; a press-only or release-only call drives a drag.",
            string.Empty,
            provisional: false, ActionImplementationStatus.Implemented,
            static (handler, request) => handler.ExecuteMouseClick(request),
            RouteParam("x", false, "Viewport x coordinate; wins over the element id when both are given.", "integer"),
            RouteParam("y", false, "Viewport y coordinate.", "integer"),
            RouteParam("elementId", false, "Stable scene-node instance id to click when no coordinates are given."),
            RouteParam("offsetX", false, "Normalized 0..1 horizontal offset within the element's rect.", "number"),
            RouteParam("offsetY", false, "Normalized 0..1 vertical offset within the element's rect.", "number"),
            RouteParam("button", false, "left, right, middle, wheelUp or wheelDown. Defaults to left."),
            RouteParam("mousePressed", false, "true presses only (hold), false releases only; omit for a full click.", "boolean")),
        RouteFor(
            "hover-element", SemanticActionKind.HoverElement,
            "Replay a real pointer move over a scene node by element id, or to a viewport point; while a button is held it is a drag.",
            string.Empty,
            provisional: false, ActionImplementationStatus.Implemented,
            static (handler, request) => handler.ExecuteHoverElement(request),
            RouteParam("elementId", false, "Stable scene-node instance id to hover."),
            RouteParam("offsetX", false, "Normalized 0..1 horizontal offset within the element's rect.", "number"),
            RouteParam("offsetY", false, "Normalized 0..1 vertical offset within the element's rect.", "number"),
            RouteParam("x", false, "Viewport x coordinate when no element id is given.", "integer"),
            RouteParam("y", false, "Viewport y coordinate when no element id is given.", "integer")),
        RouteFor(
            "key-input", SemanticActionKind.KeyInput,
            "Replay a real key event: a press and release, or only one edge of it.",
            string.Empty,
            provisional: false, ActionImplementationStatus.Implemented,
            static (handler, request) => handler.ExecuteKeyInput(request),
            RouteParam("key", true, "Browser KeyboardEvent.code, e.g. KeyE, Digit1 or Escape."),
            RouteParam("keyModifiers", false, "Comma-separated modifiers held with the key."),
            RouteParam("keyPressed", false, "true is key down, false is key up; omit for a full press and release.", "boolean")),
        RouteFor(
            "controller-input", SemanticActionKind.ControllerInput,
            "Replay a device-neutral controller input; the host names the game action it maps to.",
            string.Empty,
            provisional: false, ActionImplementationStatus.Implemented,
            static (handler, request) => handler.ExecuteControllerInput(request),
            RouteParam("controllerInput", true, "Device-neutral pad token, e.g. faceSouth, dpadUp or leftBumper."),
            RouteParam("keyPressed", false, "true is down, false is up; omit for a full press and release.", "boolean")),
        RouteFor(
            "set-scroll-offset", SemanticActionKind.SetScrollOffset,
            "Park a scrollable surface (the map, or a card grid) at a container-local Y; the game's own limits clamp it.",
            string.Empty,
            provisional: false, ActionImplementationStatus.Implemented,
            static (handler, request) => handler.ExecuteSetScrollOffset(request),
            RouteParam("elementId", true, "Numeric node instance id of the scroll container."),
            RouteParam("offsetY", true, "The wanted container-local Y, as a plain number.", "number")),
    ];

    private static readonly IReadOnlyDictionary<SemanticActionKind, EmbeddedRoute> EmbeddedRoutesByKind =
        EmbeddedRoutes.ToDictionary(route => route.Descriptor.Kind);

    /// <summary>The descriptors of exactly the kinds <see cref="ExecuteOnMainThread"/> routes.</summary>
    internal static IReadOnlyList<ActionDescriptorSnapshot> RoutedActionDescriptors { get; } =
        [.. EmbeddedRoutes.Select(route => route.Descriptor)];

    private static EmbeddedRoute RouteFor(
        string id,
        SemanticActionKind kind,
        string summary,
        string cliCommandHint,
        bool provisional,
        ActionImplementationStatus status,
        Func<Sts2ActionHandler, SemanticActionRequest, ActionExecutionResult> execute,
        params ActionParameterDescriptorSnapshot[] parameters)
        => new(new ActionDescriptorSnapshot(id, kind, summary, cliCommandHint, provisional, status, parameters), execute);

    private static ActionParameterDescriptorSnapshot RouteParam(string name, bool required, string summary, string type = "string")
        => new(name, type, required, summary);

    /// <summary>
    /// The embedded profile's dispatcher: it stands in for <c>Live/Sts2ActionHandler.Dispatch.cs</c>, which
    /// routes every kind, and it is what lets the profile leave the action bodies no embedder reaches out of the
    /// assembly. The kinds it routes are <see cref="EmbeddedRoutes"/>.
    /// </summary>
    private ActionExecutionResult ExecuteOnMainThread(SemanticActionRequest request)
    {
        try
        {
            return EmbeddedRoutesByKind.TryGetValue(request.Kind, out var route)
                ? route.Execute(this, request)
                : ActionExecutionResult.Failure(
                    kind: request.Kind,
                    code: ActionFailureCode.InvalidAction,
                    message: $"Semantic action kind '{request.Kind}' is not available in the embedded runtime profile.");
        }
        catch (Exception ex)
        {
            _logStream.Write(
                BridgeLogLevel.Error,
                "bridge.action",
                $"Action '{request.Kind}' failed with an unhandled exception: {ex}");
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.RuntimeFailure,
                message: $"Action '{request.Kind}' failed with an unhandled runtime exception.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "exception",
                        Value: ex.GetType().Name,
                        Note: ex.Message),
                ]);
        }
    }
}
