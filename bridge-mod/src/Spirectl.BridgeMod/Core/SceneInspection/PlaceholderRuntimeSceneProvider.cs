using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Core.SceneInspection;


public sealed class PlaceholderRuntimeSceneProvider : IRuntimeSceneProvider
{
    public RuntimeSceneTreeResult GetTree(RuntimeSceneQuery request)
    {
        return RuntimeSceneTreeResult.Failure(
            source: DataSourceKind.Stub,
            provisional: true,
            code: RuntimeSceneFailureCode.NotImplemented,
            message: "runtime scene inspection requires the live STS2 bridge host.",
            details:
            [
                new RuntimeSceneDetail(
                    Field: "command",
                    Value: "dev scene tree",
                    Note: "Retry through the live bridge after launching or attaching the game."),
            ]);
    }

    public RuntimeSceneNodeResult GetNode(RuntimeSceneQuery request)
    {
        return RuntimeSceneNodeResult.Failure(
            source: DataSourceKind.Stub,
            provisional: true,
            code: RuntimeSceneFailureCode.NotImplemented,
            message: "runtime scene inspection requires the live STS2 bridge host.",
            details:
            [
                new RuntimeSceneDetail(
                    Field: "command",
                    Value: "dev scene node",
                    Note: "Retry through the live bridge after launching or attaching the game."),
            ]);
    }

    public RuntimeSceneSetVisibleResult SetVisible(RuntimeSceneSetVisibleRequestSnapshot request)
    {
        return RuntimeSceneSetVisibleResult.Failure(
            source: DataSourceKind.Stub,
            provisional: true,
            code: RuntimeSceneFailureCode.NotImplemented,
            message: "runtime scene mutation requires the live STS2 bridge host.",
            details:
            [
                new RuntimeSceneDetail(
                    Field: "command",
                    Value: "dev scene set-visible",
                    Note: "Retry through the live bridge after launching or attaching the game."),
            ]);
    }

    public RuntimeSceneControlHoverResult HoverControl(RuntimeSceneControlHoverRequestSnapshot request)
    {
        return RuntimeSceneControlHoverResult.Failure(
            source: DataSourceKind.Stub,
            provisional: true,
            code: RuntimeSceneFailureCode.NotImplemented,
            message: "runtime scene control hover requires the live STS2 bridge host.",
            details:
            [
                new RuntimeSceneDetail(
                    Field: "command",
                    Value: "dev scene hover",
                    Note: "Retry through the live bridge after launching or attaching the game."),
            ]);
    }

    public RuntimeSceneControlUnhoverResult UnhoverControl(RuntimeSceneControlUnhoverRequestSnapshot request)
    {
        return RuntimeSceneControlUnhoverResult.Failure(
            source: DataSourceKind.Stub,
            provisional: true,
            code: RuntimeSceneFailureCode.NotImplemented,
            message: "runtime scene control unhover requires the live STS2 bridge host.",
            details:
            [
                new RuntimeSceneDetail(
                    Field: "command",
                    Value: "dev scene unhover",
                    Note: "Retry through the live bridge after launching or attaching the game."),
            ]);
    }

    public RuntimeTransitionStatusResult GetTransitionStatus(RuntimeTransitionStatusRequestSnapshot request)
    {
        return RuntimeTransitionStatusResult.Failure(
            source: DataSourceKind.Stub,
            provisional: true,
            code: RuntimeSceneFailureCode.NotImplemented,
            message: "runtime transition status requires the live STS2 bridge host.",
            details:
            [
                new RuntimeSceneDetail(
                    Field: "command",
                    Value: "dev wait-for-transitions",
                    Note: "Retry through the live bridge after launching or attaching the game."),
            ]);
    }
}
