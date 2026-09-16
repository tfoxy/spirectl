using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Core.SceneInspection;


public interface IRuntimeSceneProvider
{
    RuntimeSceneTreeResult GetTree(RuntimeSceneQuery request);

    RuntimeSceneNodeResult GetNode(RuntimeSceneQuery request);

    RuntimeSceneSetVisibleResult SetVisible(RuntimeSceneSetVisibleRequestSnapshot request);

    RuntimeSceneControlHoverResult HoverControl(RuntimeSceneControlHoverRequestSnapshot request);

    RuntimeSceneControlUnhoverResult UnhoverControl(RuntimeSceneControlUnhoverRequestSnapshot request);

    RuntimeTransitionStatusResult GetTransitionStatus(RuntimeTransitionStatusRequestSnapshot request);
}

public sealed record RuntimeSceneQuery(
    string NodePath,
    bool IncludeProperties = false,
    bool IncludeComputedTransform = false,
    // Lean capture: include only the cheap per-node fields a thin-client mirror renders (visibility,
    // computed rect, modulate, z-index, transform, primary texture, and shallow text: string/color/
    // font-size/alignment). Skips the expensive diagnostics (layout/theme constants, material/shader
    // inspection, per-line text metrics, recipe, font weight/style, the full texture-ref sweep) so the
    // capture is cheap enough to run on the game main thread at high frequency. Default false keeps the
    // full diagnostics for CLI inspection / hover / fixtures.
    bool LeanProperties = false);

public sealed record RuntimeSceneSetVisibleRequestSnapshot(
    string NodePath,
    bool Visible,
    bool IncludeComputedTransform = false);

public sealed record RuntimeSceneControlHoverRequestSnapshot(
    string NodePath,
    string? PresentationElementId = null,
    bool IncludeHoverTip = true,
    uint SettleMs = 0,
    // Scroll the target's ancestor scroll containers until it is inside every clip that governs it,
    // before resolving the hover position. Opt-in, because it moves the game's own UI.
    bool EnsureVisible = false,
    // Hover a target that no click can reach anyway, instead of refusing it.
    bool AllowOffscreen = false);

// Both target fields are optional; empty NodePath/PresentationElementId means a global clear
// (move the pointer to a neutral point) without invoking a specific control's unfocus hook.
public sealed record RuntimeSceneControlUnhoverRequestSnapshot(
    string? NodePath = null,
    string? PresentationElementId = null);

public sealed record RuntimeTransitionStatusRequestSnapshot(string RequestId);

public sealed record RuntimeSceneTreeResult(
    DataSourceKind Source,
    bool Provisional,
    string ScreenType,
    string ScreenTitle,
    string ScreenInstanceId,
    string RootNodePath,
    IReadOnlyList<RuntimeSceneNodeSnapshot> Nodes,
    IReadOnlyList<string> Notes,
    RuntimeSceneFailure? Error)
{
    public static RuntimeSceneTreeResult Success(
        DataSourceKind source,
        bool provisional,
        string screenType,
        string screenTitle,
        string screenInstanceId,
        string rootNodePath,
        IReadOnlyList<RuntimeSceneNodeSnapshot> nodes,
        IReadOnlyList<string> notes)
        => new(
            Source: source,
            Provisional: provisional,
            ScreenType: screenType,
            ScreenTitle: screenTitle,
            ScreenInstanceId: screenInstanceId,
            RootNodePath: rootNodePath,
            Nodes: nodes,
            Notes: notes,
            Error: null);

    public static RuntimeSceneTreeResult Failure(
        DataSourceKind source,
        bool provisional,
        RuntimeSceneFailureCode code,
        string message,
        IReadOnlyList<RuntimeSceneDetail> details)
        => new(
            Source: source,
            Provisional: provisional,
            ScreenType: string.Empty,
            ScreenTitle: string.Empty,
            ScreenInstanceId: string.Empty,
            RootNodePath: string.Empty,
            Nodes: [],
            Notes: [],
            Error: new RuntimeSceneFailure(code, message, details));
}

public sealed record RuntimeSceneNodeResult(
    DataSourceKind Source,
    bool Provisional,
    string ScreenType,
    string ScreenTitle,
    string ScreenInstanceId,
    RuntimeSceneNodeSnapshot? Node,
    IReadOnlyList<RuntimeSceneNodeSnapshot> Children,
    IReadOnlyList<string> Notes,
    RuntimeSceneFailure? Error)
{
    public static RuntimeSceneNodeResult Success(
        DataSourceKind source,
        bool provisional,
        string screenType,
        string screenTitle,
        string screenInstanceId,
        RuntimeSceneNodeSnapshot node,
        IReadOnlyList<RuntimeSceneNodeSnapshot> children,
        IReadOnlyList<string> notes)
        => new(
            Source: source,
            Provisional: provisional,
            ScreenType: screenType,
            ScreenTitle: screenTitle,
            ScreenInstanceId: screenInstanceId,
            Node: node,
            Children: children,
            Notes: notes,
            Error: null);

    public static RuntimeSceneNodeResult Failure(
        DataSourceKind source,
        bool provisional,
        RuntimeSceneFailureCode code,
        string message,
        IReadOnlyList<RuntimeSceneDetail> details)
        => new(
            Source: source,
            Provisional: provisional,
            ScreenType: string.Empty,
            ScreenTitle: string.Empty,
            ScreenInstanceId: string.Empty,
            Node: null,
            Children: [],
            Notes: [],
            Error: new RuntimeSceneFailure(code, message, details));
}

public sealed record RuntimeSceneSetVisibleResult(
    DataSourceKind Source,
    bool Provisional,
    string ScreenType,
    string ScreenTitle,
    string ScreenInstanceId,
    RuntimeSceneNodeSnapshot? Node,
    bool PreviousVisible,
    bool RequestedVisible,
    bool Changed,
    IReadOnlyList<string> Notes,
    RuntimeSceneFailure? Error)
{
    public static RuntimeSceneSetVisibleResult Success(
        DataSourceKind source,
        bool provisional,
        string screenType,
        string screenTitle,
        string screenInstanceId,
        RuntimeSceneNodeSnapshot node,
        bool previousVisible,
        bool requestedVisible,
        bool changed,
        IReadOnlyList<string> notes)
        => new(
            Source: source,
            Provisional: provisional,
            ScreenType: screenType,
            ScreenTitle: screenTitle,
            ScreenInstanceId: screenInstanceId,
            Node: node,
            PreviousVisible: previousVisible,
            RequestedVisible: requestedVisible,
            Changed: changed,
            Notes: notes,
            Error: null);

    public static RuntimeSceneSetVisibleResult Failure(
        DataSourceKind source,
        bool provisional,
        RuntimeSceneFailureCode code,
        string message,
        IReadOnlyList<RuntimeSceneDetail> details)
        => new(
            Source: source,
            Provisional: provisional,
            ScreenType: string.Empty,
            ScreenTitle: string.Empty,
            ScreenInstanceId: string.Empty,
            Node: null,
            PreviousVisible: false,
            RequestedVisible: false,
            Changed: false,
            Notes: [],
            Error: new RuntimeSceneFailure(code, message, details));
}

public sealed record RuntimeSceneControlHoverResult(
    DataSourceKind Source,
    bool Provisional,
    string ScreenType,
    string ScreenTitle,
    string ScreenInstanceId,
    RuntimeSceneNodeSnapshot? Node,
    string ResolvedNodePath,
    string? PresentationElementId,
    bool Hovered,
    RuntimeSceneVector2Snapshot? HoverPosition,
    RuntimeSceneHoverTipSnapshot? HoverTip,
    IReadOnlyList<string> Notes,
    RuntimeSceneFailure? Error,
    RuntimeSceneHoverVisibilitySnapshot? Visibility = null)
{
    public static RuntimeSceneControlHoverResult Success(
        DataSourceKind source,
        bool provisional,
        string screenType,
        string screenTitle,
        string screenInstanceId,
        RuntimeSceneNodeSnapshot node,
        string resolvedNodePath,
        string? presentationElementId,
        bool hovered,
        RuntimeSceneVector2Snapshot hoverPosition,
        RuntimeSceneHoverTipSnapshot? hoverTip,
        IReadOnlyList<string> notes,
        RuntimeSceneHoverVisibilitySnapshot? visibility = null)
        => new(
            Source: source,
            Provisional: provisional,
            ScreenType: screenType,
            ScreenTitle: screenTitle,
            ScreenInstanceId: screenInstanceId,
            Node: node,
            ResolvedNodePath: resolvedNodePath,
            PresentationElementId: presentationElementId,
            Hovered: hovered,
            HoverPosition: hoverPosition,
            HoverTip: hoverTip,
            Notes: notes,
            Error: null,
            Visibility: visibility);

    public static RuntimeSceneControlHoverResult Failure(
        DataSourceKind source,
        bool provisional,
        RuntimeSceneFailureCode code,
        string message,
        IReadOnlyList<RuntimeSceneDetail> details)
        => new(
            Source: source,
            Provisional: provisional,
            ScreenType: string.Empty,
            ScreenTitle: string.Empty,
            ScreenInstanceId: string.Empty,
            Node: null,
            ResolvedNodePath: string.Empty,
            PresentationElementId: null,
            Hovered: false,
            HoverPosition: null,
            HoverTip: null,
            Notes: [],
            Error: new RuntimeSceneFailure(code, message, details));
}

public sealed record RuntimeSceneControlUnhoverResult(
    DataSourceKind Source,
    bool Provisional,
    string ScreenType,
    string ScreenTitle,
    string ScreenInstanceId,
    RuntimeSceneNodeSnapshot? Node,
    string ResolvedNodePath,
    string? PresentationElementId,
    bool Hovered,
    RuntimeSceneVector2Snapshot? PointerPosition,
    IReadOnlyList<string> Notes,
    RuntimeSceneFailure? Error)
{
    public static RuntimeSceneControlUnhoverResult Success(
        DataSourceKind source,
        bool provisional,
        string screenType,
        string screenTitle,
        string screenInstanceId,
        RuntimeSceneNodeSnapshot? node,
        string resolvedNodePath,
        string? presentationElementId,
        bool hovered,
        RuntimeSceneVector2Snapshot pointerPosition,
        IReadOnlyList<string> notes)
        => new(
            Source: source,
            Provisional: provisional,
            ScreenType: screenType,
            ScreenTitle: screenTitle,
            ScreenInstanceId: screenInstanceId,
            Node: node,
            ResolvedNodePath: resolvedNodePath,
            PresentationElementId: presentationElementId,
            Hovered: hovered,
            PointerPosition: pointerPosition,
            Notes: notes,
            Error: null);

    public static RuntimeSceneControlUnhoverResult Failure(
        DataSourceKind source,
        bool provisional,
        RuntimeSceneFailureCode code,
        string message,
        IReadOnlyList<RuntimeSceneDetail> details)
        => new(
            Source: source,
            Provisional: provisional,
            ScreenType: string.Empty,
            ScreenTitle: string.Empty,
            ScreenInstanceId: string.Empty,
            Node: null,
            ResolvedNodePath: string.Empty,
            PresentationElementId: null,
            Hovered: false,
            PointerPosition: null,
            Notes: [],
            Error: new RuntimeSceneFailure(code, message, details));
}

public sealed record RuntimeTransitionStatusResult(
    DataSourceKind Source,
    bool Provisional,
    string ScreenType,
    string ScreenTitle,
    string ScreenInstanceId,
    bool Quiescent,
    int BlockingCount,
    int IgnoredInfiniteCount,
    IReadOnlyList<RuntimeTransitionBlockerSnapshot> Blockers,
    IReadOnlyList<string> Notes,
    RuntimeSceneFailure? Error)
{
    public static RuntimeTransitionStatusResult Success(
        DataSourceKind source,
        bool provisional,
        string screenType,
        string screenTitle,
        string screenInstanceId,
        bool quiescent,
        int blockingCount,
        int ignoredInfiniteCount,
        IReadOnlyList<RuntimeTransitionBlockerSnapshot> blockers,
        IReadOnlyList<string> notes)
        => new(
            Source: source,
            Provisional: provisional,
            ScreenType: screenType,
            ScreenTitle: screenTitle,
            ScreenInstanceId: screenInstanceId,
            Quiescent: quiescent,
            BlockingCount: blockingCount,
            IgnoredInfiniteCount: ignoredInfiniteCount,
            Blockers: blockers,
            Notes: notes,
            Error: null);

    public static RuntimeTransitionStatusResult Failure(
        DataSourceKind source,
        bool provisional,
        RuntimeSceneFailureCode code,
        string message,
        IReadOnlyList<RuntimeSceneDetail> details)
        => new(
            Source: source,
            Provisional: provisional,
            ScreenType: string.Empty,
            ScreenTitle: string.Empty,
            ScreenInstanceId: string.Empty,
            Quiescent: false,
            BlockingCount: 0,
            IgnoredInfiniteCount: 0,
            Blockers: [],
            Notes: [],
            Error: new RuntimeSceneFailure(code, message, details));
}

public enum RuntimeSceneFailureCode
{
    NotImplemented,
    InvalidNodePath,
    BridgeNotAttached,
    RuntimeFailure,
}

public sealed record RuntimeSceneFailure(
    RuntimeSceneFailureCode Code,
    string Message,
    IReadOnlyList<RuntimeSceneDetail> Details);

public sealed record RuntimeSceneDetail(
    string Field,
    string Value,
    string Note);
