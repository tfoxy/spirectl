using Grpc.Core;
using Google.Protobuf.WellKnownTypes;
using System.Text.Json;
using Spirectl.Sts2;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.ConsoleCommands;
using Spirectl.Sts2.Core.Debugging;
using Spirectl.Sts2.Core.HotReload;
using Spirectl.Sts2.Core.Lifecycle;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.Scenarios;
using Spirectl.Sts2.Core.SceneInspection;
using Spirectl.Sts2.Core.State;
using Spirectl.Proto.V0;
using FixtureLoadFailure = Spirectl.Sts2.Core.Fixtures.FixtureLoadFailure;
using FixtureLoadFailureCode = Spirectl.Sts2.Core.Fixtures.FixtureLoadFailureCode;
using FixtureLoadRequestSnapshot = Spirectl.Sts2.Core.Fixtures.FixtureLoadRequestSnapshot;
using RecordedFixtureCompatibilityNoteSnapshot = Spirectl.Sts2.Core.Fixtures.RecordedFixtureCompatibilityNoteSnapshot;
using RecordedFixtureFailure = Spirectl.Sts2.Core.Fixtures.RecordedFixtureFailure;
using RecordedFixtureFailureCode = Spirectl.Sts2.Core.Fixtures.RecordedFixtureFailureCode;
using RecordedFixtureMetadataSnapshot = Spirectl.Sts2.Core.Fixtures.RecordedFixtureMetadataSnapshot;
using RecordedFixtureNoticeSnapshot = Spirectl.Sts2.Core.Fixtures.RecordedFixtureNoticeSnapshot;
using RecordedFixtureRequestSnapshot = Spirectl.Sts2.Core.Fixtures.RecordedFixtureRequestSnapshot;
using RecordedFixtureRestoreQuality = Spirectl.Sts2.Core.Fixtures.RecordedFixtureRestoreQuality;
using RestoreFieldFidelitySnapshot = Spirectl.Sts2.Core.Restore.RestoreFieldFidelity;
using RestoreFieldReportSnapshot = Spirectl.Sts2.Core.Restore.RestoreFieldReportSnapshot;
using RestoreMismatchSnapshot = Spirectl.Sts2.Core.Restore.RestoreMismatchSnapshot;
using RestoreVerificationSnapshot = Spirectl.Sts2.Core.Restore.RestoreVerificationSnapshot;
using RestoreVerificationStatusSnapshot = Spirectl.Sts2.Core.Restore.RestoreVerificationStatus;
using RestoreLobbyCharacterSnapshot = Spirectl.Sts2.Core.Restore.LobbyCharacterSnapshot;
using MultiplayerLobbySnapshot = Spirectl.Sts2.Core.Restore.MultiplayerLobbySnapshot;
using MultiplayerPlayerSnapshot = Spirectl.Sts2.Core.Restore.MultiplayerPlayerSnapshot;
using MultiplayerRestoreLimitationSnapshot = Spirectl.Sts2.Core.Restore.MultiplayerRestoreLimitationSnapshot;
using MultiplayerRestoreModeSnapshot = Spirectl.Sts2.Core.Restore.MultiplayerRestoreModeSnapshot;
using MultiplayerRestoreResultSnapshot = Spirectl.Sts2.Core.Restore.MultiplayerRestoreResultSnapshot;
using MultiplayerRestoreSnapshot = Spirectl.Sts2.Core.Restore.MultiplayerRestoreSnapshot;

namespace Spirectl.BridgeMod.Services;

public sealed partial class GrpcBridgeService(BridgeRuntime runtime) : BridgeService.BridgeServiceBase
{
    private readonly BridgeRuntime _runtime = runtime;
    private readonly BridgeRuntimeProtocolAdapter _protocol = new(runtime);

    // Shared response for RPCs whose CLI surface and C# implementation were removed but
    // whose proto definitions remain (protobuf is additive).
    private static BridgeError RemovedRpcError(string rpc)
        => new()
        {
            Code = BridgeErrorCode.NotImplemented,
            Message = $"The {rpc} RPC was removed from spirectl and is no longer implemented by the bridge.",
        };

    public HandshakeResult HandleHandshake(HandshakeRequest request)
    {
        return _protocol.HandleHandshake(request);
    }

    public StateResult HandleGetState(StateRequest request)
    {
        return _protocol.HandleGetState(request);
    }

    public IAsyncEnumerable<StateWatchEvent> HandleWatchState(
        StateWatchRequest request,
        CancellationToken cancellationToken = default)
    {
        return _protocol.WatchState(request, cancellationToken);
    }

    public override async Task WatchState(
        StateWatchRequest request,
        IServerStreamWriter<StateWatchEvent> responseStream,
        ServerCallContext context)
    {
        await foreach (var evt in HandleWatchState(request, context.CancellationToken)
            .ConfigureAwait(false))
        {
            await responseStream.WriteAsync(evt, context.CancellationToken).ConfigureAwait(false);
        }
    }

    public IAsyncEnumerable<CombatEvent> HandleWatchCombatEvents(
        WatchCombatEventsRequest request,
        CancellationToken cancellationToken = default)
    {
        return _protocol.WatchCombatEvents(request, cancellationToken);
    }

    public override async Task WatchCombatEvents(
        WatchCombatEventsRequest request,
        IServerStreamWriter<CombatEvent> responseStream,
        ServerCallContext context)
    {
        await foreach (var evt in HandleWatchCombatEvents(request, context.CancellationToken)
            .ConfigureAwait(false))
        {
            await responseStream.WriteAsync(evt, context.CancellationToken).ConfigureAwait(false);
        }
    }

    public ActionResult HandleExecuteAction(ActionRequest request)
    {
        return _protocol.HandleExecuteAction(request);
    }

    public LogsResult HandleGetLogs(LogsRequest request)
    {
        if (request.Limit <= 0)
        {
            return new LogsResult
            {
                Error = new BridgeError
                {
                    Code = BridgeErrorCode.InvalidQueryFilter,
                    Message = "logs.limit must be greater than zero.",
                    Details =
                    {
                        new ErrorDetail
                        {
                            Field = "limit",
                            Value = request.Limit.ToString(),
                            Note = "Use a positive limit when querying bridge logs.",
                        },
                    },
                }
            };
        }

        var read = _runtime.ReadLogs(new LogQuery(
            Limit: (int)request.Limit,
            AfterCursor: request.AfterCursor == 0 ? null : request.AfterCursor,
            MinimumLevel: request.MinimumLevel == LogLevel.Unspecified ? null : ToDomainLogLevel(request.MinimumLevel),
            TargetFilter: string.IsNullOrWhiteSpace(request.TargetFilter) ? null : request.TargetFilter));

        var response = new LogsResponse
        {
            Source = ToProtoDataSource(read.Source),
            Provisional = read.Provisional,
            NextCursor = read.NextCursor,
        };
        response.Entries.Add(read.Entries.Select(ToProtoLogEntry));
        return new LogsResult { Success = response };
    }

    public ConsoleCommandResult HandleExecuteConsoleCommand(ConsoleCommandRequest request)
    {
        var command = request.Command?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(command))
        {
            return new ConsoleCommandResult
            {
                Error = new BridgeError
                {
                    Code = BridgeErrorCode.InvalidAction,
                    Message = "dev console requires a command name.",
                    Details =
                    {
                        new ErrorDetail
                        {
                            Field = "command",
                            Value = string.Empty,
                            Note = "Provide the first positional token after 'dev console'.",
                        },
                    },
                },
            };
        }

        var args = request.Args.ToArray();
        var line = string.IsNullOrWhiteSpace(request.Line)
            ? BuildConsoleLine(command, args)
            : request.Line.Trim();

        var result = _runtime.ExecuteConsoleCommand(new ConsoleCommandRequestSnapshot(
            request.RequestId,
            command,
            args,
            line));

        if (result.Error is not null)
        {
            return new ConsoleCommandResult
            {
                Error = ToProtoBridgeError(result.Error),
            };
        }

        return new ConsoleCommandResult
        {
            Success = ToProtoConsoleCommandResponse(result),
        };
    }

    public DebugStatusResult HandleGetDebugStatus(DebugStatusRequest request)
    {
        return new DebugStatusResult
        {
            Success = ToProtoDebugStatus(_runtime.GetDebugStatus(new DebugStatusRequestSnapshot(
                string.IsNullOrWhiteSpace(request.SessionId) ? null : request.SessionId))),
        };
    }

    public GameCloseResult HandleCloseGame(GameCloseRequest request)
    {
        var result = _runtime.LifecycleControl.CloseGame(request.RequestId);
        if (result.Error is not null)
        {
            return new GameCloseResult
            {
                Error = ToProtoBridgeError(result.Error),
            };
        }

        return new GameCloseResult
        {
            Success = new GameCloseResponse
            {
                RequestId = request.RequestId,
                Accepted = result.Accepted,
            }.Also(response => response.Notices.Add(result.Notices)),
        };
    }

    public ModListResult HandleGetMods(ModListRequest request)
    {
        return _protocol.HandleGetMods(request);
    }

    public ScenarioCaptureResult HandleCaptureScenario(ScenarioCaptureRequest request)
    {
        var result = _runtime.ScenarioProvider.Capture(new ScenarioCaptureRequestSnapshot(
            request.RequestId,
            request.IncludeExact,
            string.IsNullOrWhiteSpace(request.Perspective?.PlayerId) ? null : request.Perspective.PlayerId));
        if (result.Error is not null)
        {
            return new ScenarioCaptureResult
            {
                Error = ToProtoBridgeError(result.Error),
            };
        }

        return new ScenarioCaptureResult
        {
            Success = new ScenarioCaptureResponse
            {
                RequestId = result.RequestId,
                Scenario = ToProtoScenarioDocument(result.Scenario!),
                Source = ToProtoDataSource(result.Source),
                Provisional = result.Provisional,
                ExactBundlePayload = result.ExactBundlePayload is null ? null : ToProtoExactBundlePayload(result.ExactBundlePayload),
            },
        };
    }

    public ScenarioRestoreResult HandleRestoreScenario(ScenarioRestoreRequest request)
    {
        var result = _runtime.ScenarioProvider.Restore(new ScenarioRestoreRequestSnapshot(
            request.RequestId,
            request.Scenario is null ? null : FromProtoScenarioDocument(request.Scenario),
            request.ExactBundlePayload?.Data.ToByteArray(),
            string.IsNullOrWhiteSpace(request.ExactBundlePayload?.ContentType)
                ? request.Scenario?.Restore?.ExactBundle?.ContentType
                : request.ExactBundlePayload.ContentType,
            request.AllowSparseFallback,
            request.AllowDegradedLocalMultiplayer));
        if (result.Error is not null)
        {
            return new ScenarioRestoreResult
            {
                Error = ToProtoBridgeError(result.Error),
            };
        }

        var response = new ScenarioRestoreResponse
        {
            RequestId = result.RequestId,
            Quality = ToProtoRestoreQuality(result.Quality),
            Screen = result.Screen is null ? null : ToProtoScenarioScreen(result.Screen),
            ResolvedPerspective = result.ResolvedPerspective is null ? null : ToProtoScenarioPerspective(result.ResolvedPerspective),
            ExactBundleUsed = result.ExactBundleUsed,
            SparseFallbackUsed = result.SparseFallbackUsed,
            MultiplayerRestore = result.MultiplayerRestore is null ? null : ToProtoMultiplayerRestoreResult(result.MultiplayerRestore),
        };
        response.Notices.Add(result.Notices.Select(ToProtoScenarioNotice));
        response.CompatibilityNotes.Add(result.CompatibilityNotes.Select(ToProtoCompatibilityNote));
        if (result.Verification is not null)
        {
            response.Verification = ToProtoRestoreVerification(result.Verification);
        }

        return new ScenarioRestoreResult
        {
            Success = response,
        };
    }

    // The checkpoint RPCs (dev checkpoint capture|resume|list|delete) were removed from the
    // CLI. The proto surface stays defined (protobuf is additive), but every handler now
    // collapses to a single "removed" response. See also HandleInspectPresentation* below.
    public CheckpointCaptureResult HandleCaptureCheckpoint(CheckpointCaptureRequest request)
        => new() { Error = RemovedRpcError("CaptureCheckpoint") };

    public CheckpointRestoreResult HandleRestoreCheckpoint(CheckpointRestoreRequest request)
        => new() { Error = RemovedRpcError("RestoreCheckpoint") };

    public CheckpointListResult HandleListCheckpoints(CheckpointListRequest request)
        => new() { Error = RemovedRpcError("ListCheckpoints") };

    public CheckpointDeleteResult HandleDeleteCheckpoint(CheckpointDeleteRequest request)
        => new() { Error = RemovedRpcError("DeleteCheckpoint") };

    public HotReloadStatusResult HandleGetHotReloadStatus(HotReloadStatusRequest request)
    {
        var result = _runtime.GetHotReloadStatus(new HotReloadStatusRequestSnapshot(
            request.RequestId,
            request.ProjectId,
            request.ShellModId));
        if (result.Error is not null)
        {
            return new HotReloadStatusResult { Error = ToProtoBridgeError(result.Error) };
        }

        var response = new HotReloadStatusResponse
        {
            Status = ToProtoHotReloadShellStatus(result.Status!),
        };
        response.Notices.Add(result.Notices.Select(ToProtoHotReloadNotice));
        return new HotReloadStatusResult { Success = response };
    }

    public async Task<HotReloadResult> HandleRequestHotReloadAsync(HotReloadRequest request)
    {
        var result = await _runtime.RequestHotReloadAsync(new HotReloadRequestSnapshot(
            request.RequestId,
            request.ProjectId,
            request.ShellModId,
            request.LogicArtifactPath,
            request.ExpectedContractVersion,
            request.WaitForCompletion,
            request.TimeoutMs)).ConfigureAwait(false);
        if (result.Error is not null)
        {
            return new HotReloadResult { Error = ToProtoBridgeError(result.Error) };
        }

        var response = new HotReloadResponse
        {
            RequestId = request.RequestId,
            Accepted = result.Accepted,
            Status = ToProtoHotReloadShellStatus(result.Status!),
            Report = result.Report is null ? null : ToProtoHotReloadReport(result.Report),
        };
        response.Notices.Add(result.Notices.Select(ToProtoHotReloadNotice));
        return new HotReloadResult { Success = response };
    }

    public DebugSessionStartResult HandleStartDebugSession(DebugSessionStartRequest request)
    {
        var result = _runtime.StartDebugSession(new DebugSessionStartRequestSnapshot(
            string.IsNullOrWhiteSpace(request.Name) ? null : request.Name,
            request.PauseOnStart,
            request.LeaseTimeoutMs,
            ToDomainDebugSessionRole(request.Role)));
        return new DebugSessionStartResult
        {
            Success = new DebugSessionStartResponse
            {
                Started = result.Started,
                Session = result.Session is null ? null : ToProtoDebugSessionInfo(result.Session),
                Status = ToProtoDebugStatus(result.Status),
            }.Also(response => response.Notices.Add(result.Notices.Select(ToProtoDebugNotice))),
        };
    }

    public DebugSessionStatusResult HandleGetDebugSessionStatus(DebugSessionStatusRequest request)
    {
        var result = _runtime.GetDebugSessionStatus(new DebugSessionStatusRequestSnapshot(request.Id));
        return new DebugSessionStatusResult
        {
            Success = new DebugSessionStatusResponse
            {
                Found = result.Found,
                Session = result.Session is null ? null : ToProtoDebugSessionInfo(result.Session),
                Status = ToProtoDebugStatus(result.Status),
            }.Also(response => response.Notices.Add(result.Notices.Select(ToProtoDebugNotice))),
        };
    }

    public DebugSessionEndResult HandleEndDebugSession(DebugSessionEndRequest request)
    {
        var result = _runtime.EndDebugSession(new DebugSessionEndRequestSnapshot(request.Id, request.ResumeRuntime));
        return new DebugSessionEndResult
        {
            Success = new DebugSessionEndResponse
            {
                Ended = result.Ended,
                Id = result.SessionId,
                Status = ToProtoDebugStatus(result.Status),
            }.Also(response => response.Notices.Add(result.Notices.Select(ToProtoDebugNotice))),
        };
    }

    public DebugPauseResult HandlePauseDebug(DebugPauseRequest request)
    {
        var result = _runtime.PauseDebug(new DebugSessionBoundRequestSnapshot(
            string.IsNullOrWhiteSpace(request.SessionId) ? null : request.SessionId));
        return new DebugPauseResult
        {
            Success = new DebugPauseResponse
            {
                Applied = result.Applied,
                Status = ToProtoDebugStatus(result.Status),
            }.Also(response => response.Notices.Add(result.Notices.Select(ToProtoDebugNotice))),
        };
    }

    public DebugResumeResult HandleResumeDebug(DebugResumeRequest request)
    {
        var result = _runtime.ResumeDebug(new DebugSessionBoundRequestSnapshot(
            string.IsNullOrWhiteSpace(request.SessionId) ? null : request.SessionId));
        return new DebugResumeResult
        {
            Success = new DebugResumeResponse
            {
                Applied = result.Applied,
                Status = ToProtoDebugStatus(result.Status),
            }.Also(response => response.Notices.Add(result.Notices.Select(ToProtoDebugNotice))),
        };
    }

    public DebugStepResult HandleStepDebug(DebugStepRequest request)
    {
        var result = _runtime.StepDebug(new DebugStepRequestSnapshot(
            string.IsNullOrWhiteSpace(request.SessionId) ? null : request.SessionId,
            ToDomainDebugStepKind(request.Kind),
            request.Count == 0 ? 1u : request.Count));
        return new DebugStepResult
        {
            Success = new DebugStepResponse
            {
                Applied = result.Applied,
                Status = ToProtoDebugStatus(result.Status),
            }.Also(response => response.Notices.Add(result.Notices.Select(ToProtoDebugNotice))),
        };
    }

    public DebugWaitResult HandleWaitDebug(DebugWaitRequest request)
    {
        var result = _runtime.WaitDebug(new DebugWaitRequestSnapshot(
            request.SessionId,
            request.TimeoutMs == 0 ? 5000u : request.TimeoutMs));
        return new DebugWaitResult
        {
            Success = new DebugWaitResponse
            {
                Completed = result.Completed,
                TimedOut = result.TimedOut,
                Status = ToProtoDebugStatus(result.Status),
            }.Also(response => response.Notices.Add(result.Notices.Select(ToProtoDebugNotice))),
        };
    }

    public DebugBreakpointListResult HandleListBreakpoints(DebugBreakpointListRequest request)
    {
        var result = _runtime.ListBreakpoints(new DebugBreakpointListRequestSnapshot(
            string.IsNullOrWhiteSpace(request.SessionId) ? null : request.SessionId));
        return new DebugBreakpointListResult
        {
            Success = new DebugBreakpointListResponse
            {
                Status = ToProtoDebugStatus(result.Status),
            }.Also(response =>
            {
                response.Breakpoints.Add(result.Breakpoints.Select(ToProtoDebugBreakpoint));
                response.Notices.Add(result.Notices.Select(ToProtoDebugNotice));
            }),
        };
    }

    public DebugBreakpointAddResult HandleAddBreakpoint(DebugBreakpointAddRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.QueryPath))
        {
            return new DebugBreakpointAddResult
            {
                Error = new BridgeError
                {
                    Code = BridgeErrorCode.InvalidQueryFilter,
                    Message = "breakpoint add requires a query path.",
                    Details =
                    {
                        new ErrorDetail
                        {
                            Field = "query_path",
                            Value = request.QueryPath,
                            Note = "Provide a state query path such as screen.id or combat.turn.",
                        },
                    },
                },
            };
        }

        var predicate = ToDomainDebugPredicate(request.Predicate);
        var pathError = DebugStateQueryEvaluator.ValidatePath(request.QueryPath);
        if (pathError is not null)
        {
            return new DebugBreakpointAddResult
            {
                Error = new BridgeError
                {
                    Code = BridgeErrorCode.InvalidQueryFilter,
                    Message = pathError,
                    Details =
                    {
                        new ErrorDetail
                        {
                            Field = "query_path",
                            Value = request.QueryPath,
                            Note = "Use the same query path syntax supported by dev assert and dev wait-for.",
                        },
                    },
                },
            };
        }

        var predicateError = DebugStateQueryEvaluator.ValidatePredicate(predicate);
        if (predicateError is not null)
        {
            return new DebugBreakpointAddResult
            {
                Error = new BridgeError
                {
                    Code = BridgeErrorCode.InvalidQueryFilter,
                    Message = predicateError,
                    Details =
                    {
                        new ErrorDetail
                        {
                            Field = "predicate.expected_json",
                            Value = request.Predicate?.ExpectedJson ?? string.Empty,
                            Note = "Provide a valid regex pattern or switch to a non-regex predicate.",
                        },
                    },
                },
            };
        }

        var result = _runtime.AddBreakpoint(new DebugBreakpointAddRequestSnapshot(
            string.IsNullOrWhiteSpace(request.SessionId) ? null : request.SessionId,
            request.QueryPath,
            ToDomainDebugBreakpointKind(request.Kind),
            predicate,
            string.IsNullOrWhiteSpace(request.Name) ? null : request.Name,
            request.MinHitCount == 0 ? 1u : request.MinHitCount,
            request.AutoRemoveOnHit));
        return new DebugBreakpointAddResult
        {
            Success = new DebugBreakpointAddResponse
            {
                Added = result.Added,
                Breakpoint = result.Breakpoint is null ? null : ToProtoDebugBreakpoint(result.Breakpoint),
                Status = ToProtoDebugStatus(result.Status),
            }.Also(response => response.Notices.Add(result.Notices.Select(ToProtoDebugNotice))),
        };
    }

    public DebugBreakpointRemoveResult HandleRemoveBreakpoint(DebugBreakpointRemoveRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
        {
            return new DebugBreakpointRemoveResult
            {
                Error = new BridgeError
                {
                    Code = BridgeErrorCode.InvalidQueryFilter,
                    Message = "breakpoint remove requires a breakpoint id.",
                    Details =
                    {
                        new ErrorDetail
                        {
                            Field = "id",
                            Value = request.Id,
                            Note = "Use dev breakpoint list to discover the registered breakpoint id.",
                        },
                    },
                },
            };
        }

        var result = _runtime.RemoveBreakpoint(new DebugBreakpointRemoveRequestSnapshot(
            string.IsNullOrWhiteSpace(request.SessionId) ? null : request.SessionId,
            request.Id));
        return new DebugBreakpointRemoveResult
        {
            Success = new DebugBreakpointRemoveResponse
            {
                Removed = result.Removed,
                Id = result.BreakpointId,
                Status = ToProtoDebugStatus(result.Status),
            }.Also(response => response.Notices.Add(result.Notices.Select(ToProtoDebugNotice))),
        };
    }

    public DebugEventStreamResult HandleGetDebugEvents(DebugEventStreamRequest request)
    {
        var result = _runtime.GetDebugEvents(new DebugEventStreamRequestSnapshot(
            string.IsNullOrWhiteSpace(request.SessionId) ? null : request.SessionId,
            request.FromSequence,
            request.Limit));
        return new DebugEventStreamResult
        {
            Success = ToProtoDebugEventStreamResponse(result),
        };
    }

    public Spirectl.Proto.V0.RuntimeSceneTreeResult HandleGetRuntimeSceneTree(RuntimeSceneTreeRequest request)
    {
        var result = _runtime.GetRuntimeSceneTree(new RuntimeSceneQuery(
            request.NodePath,
            request.IncludeProperties,
            request.IncludeComputedTransform));
        if (result.Error is not null)
        {
            return new Spirectl.Proto.V0.RuntimeSceneTreeResult
            {
                Error = ToProtoBridgeError(result.Error),
            };
        }

        var response = new RuntimeSceneTreeResponse
        {
            Source = ToProtoDataSource(result.Source),
            Provisional = result.Provisional,
            Screen = new ScreenInfo
            {
                Id = result.ScreenType,
                Title = result.ScreenTitle,
                ScreenInstanceId = result.ScreenInstanceId,
            },
            RootNodePath = result.RootNodePath,
        };
        response.Nodes.Add(result.Nodes.Select(ToProtoRuntimeSceneNode));
        response.Notes.Add(result.Notes);

        return new Spirectl.Proto.V0.RuntimeSceneTreeResult
        {
            Success = response,
        };
    }

    public Spirectl.Proto.V0.RuntimeSceneNodeResult HandleGetRuntimeSceneNode(RuntimeSceneNodeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.NodePath))
        {
            return new Spirectl.Proto.V0.RuntimeSceneNodeResult
            {
                Error = new BridgeError
                {
                    Code = BridgeErrorCode.InvalidQueryFilter,
                    Message = "runtime scene node requests require an exact node path.",
                    Details =
                    {
                        new ErrorDetail
                        {
                            Field = "node_path",
                            Value = request.NodePath,
                            Note = "Provide a normalized Godot node path rooted at /root.",
                        },
                    },
                },
            };
        }

        var result = _runtime.GetRuntimeSceneNode(new RuntimeSceneQuery(
            request.NodePath,
            request.IncludeProperties,
            request.IncludeComputedTransform));
        if (result.Error is not null)
        {
            return new Spirectl.Proto.V0.RuntimeSceneNodeResult
            {
                Error = ToProtoBridgeError(result.Error),
            };
        }

        var response = new RuntimeSceneNodeResponse
        {
            Source = ToProtoDataSource(result.Source),
            Provisional = result.Provisional,
            Screen = new ScreenInfo
            {
                Id = result.ScreenType,
                Title = result.ScreenTitle,
                ScreenInstanceId = result.ScreenInstanceId,
            },
            Node = ToProtoRuntimeSceneNode(result.Node!),
        };
        response.Children.Add(result.Children.Select(ToProtoRuntimeSceneNode));
        response.Notes.Add(result.Notes);

        return new Spirectl.Proto.V0.RuntimeSceneNodeResult
        {
            Success = response,
        };
    }

    public Spirectl.Proto.V0.RuntimeSceneSetVisibleResult HandleSetRuntimeSceneNodeVisible(RuntimeSceneSetVisibleRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.NodePath))
        {
            return new Spirectl.Proto.V0.RuntimeSceneSetVisibleResult
            {
                Error = new BridgeError
                {
                    Code = BridgeErrorCode.InvalidQueryFilter,
                    Message = "runtime scene node visibility requests require an exact node path.",
                    Details =
                    {
                        new ErrorDetail
                        {
                            Field = "node_path",
                            Value = request.NodePath,
                            Note = "Provide a normalized Godot node path rooted at /root.",
                        },
                    },
                },
            };
        }

        var result = _runtime.SetRuntimeSceneNodeVisible(new RuntimeSceneSetVisibleRequestSnapshot(
            request.NodePath,
            request.Visible,
            request.IncludeComputedTransform));
        if (result.Error is not null)
        {
            return new Spirectl.Proto.V0.RuntimeSceneSetVisibleResult
            {
                Error = ToProtoBridgeError(result.Error),
            };
        }

        var response = new RuntimeSceneSetVisibleResponse
        {
            Source = ToProtoDataSource(result.Source),
            Provisional = result.Provisional,
            Screen = new ScreenInfo
            {
                Id = result.ScreenType,
                Title = result.ScreenTitle,
                ScreenInstanceId = result.ScreenInstanceId,
            },
            Node = ToProtoRuntimeSceneNode(result.Node!),
            PreviousVisible = result.PreviousVisible,
            RequestedVisible = result.RequestedVisible,
            Changed = result.Changed,
        };
        response.Notes.Add(result.Notes);

        return new Spirectl.Proto.V0.RuntimeSceneSetVisibleResult
        {
            Success = response,
        };
    }

    public Spirectl.Proto.V0.RuntimeSceneControlHoverResult HandleHoverRuntimeSceneControl(RuntimeSceneControlHoverRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.NodePath)
            && string.IsNullOrWhiteSpace(request.PresentationElementId))
        {
            return new Spirectl.Proto.V0.RuntimeSceneControlHoverResult
            {
                Error = new BridgeError
                {
                    Code = BridgeErrorCode.InvalidQueryFilter,
                    Message = "runtime scene hover requests require a node path or presentation element id.",
                    Details =
                    {
                        new ErrorDetail
                        {
                            Field = "target",
                            Value = string.Empty,
                            Note = "Provide a normalized Godot node path rooted at /root or a current presentation element id.",
                        },
                    },
                },
            };
        }

        var result = _runtime.HoverRuntimeSceneControl(new RuntimeSceneControlHoverRequestSnapshot(
            request.NodePath,
            string.IsNullOrWhiteSpace(request.PresentationElementId) ? null : request.PresentationElementId,
            request.IncludeHoverTip,
            request.SettleMs,
            request.EnsureVisible,
            request.AllowOffscreen));
        if (result.Error is not null)
        {
            return new Spirectl.Proto.V0.RuntimeSceneControlHoverResult
            {
                Error = ToProtoBridgeError(result.Error),
            };
        }

        var response = new RuntimeSceneControlHoverResponse
        {
            Source = ToProtoDataSource(result.Source),
            Provisional = result.Provisional,
            Screen = new ScreenInfo
            {
                Id = result.ScreenType,
                Title = result.ScreenTitle,
                ScreenInstanceId = result.ScreenInstanceId,
            },
            Node = ToProtoRuntimeSceneNode(result.Node!),
            ResolvedNodePath = result.ResolvedNodePath,
            PresentationElementId = result.PresentationElementId ?? string.Empty,
            Hovered = result.Hovered,
            HoverPosition = ToProtoRuntimeSceneVector2(result.HoverPosition),
            HoverTip = ToProtoRuntimeSceneHoverTip(result.HoverTip),
            Visibility = ToProtoRuntimeSceneHoverVisibility(result.Visibility),
        };
        response.Notes.Add(result.Notes);

        return new Spirectl.Proto.V0.RuntimeSceneControlHoverResult
        {
            Success = response,
        };
    }

    public Spirectl.Proto.V0.RuntimeSceneControlUnhoverResult HandleUnhoverRuntimeSceneControl(RuntimeSceneControlUnhoverRequest request)
    {
        var result = _runtime.UnhoverRuntimeSceneControl(new RuntimeSceneControlUnhoverRequestSnapshot(
            string.IsNullOrWhiteSpace(request.NodePath) ? null : request.NodePath,
            string.IsNullOrWhiteSpace(request.PresentationElementId) ? null : request.PresentationElementId));
        if (result.Error is not null)
        {
            return new Spirectl.Proto.V0.RuntimeSceneControlUnhoverResult
            {
                Error = ToProtoBridgeError(result.Error),
            };
        }

        var response = new RuntimeSceneControlUnhoverResponse
        {
            Source = ToProtoDataSource(result.Source),
            Provisional = result.Provisional,
            Screen = new ScreenInfo
            {
                Id = result.ScreenType,
                Title = result.ScreenTitle,
                ScreenInstanceId = result.ScreenInstanceId,
            },
            ResolvedNodePath = result.ResolvedNodePath,
            PresentationElementId = result.PresentationElementId ?? string.Empty,
            Hovered = result.Hovered,
            PointerPosition = ToProtoRuntimeSceneVector2(result.PointerPosition),
        };
        if (result.Node is not null)
        {
            response.Node = ToProtoRuntimeSceneNode(result.Node);
        }

        response.Notes.Add(result.Notes);

        return new Spirectl.Proto.V0.RuntimeSceneControlUnhoverResult
        {
            Success = response,
        };
    }

    public Spirectl.Proto.V0.RuntimeTransitionStatusResult HandleGetRuntimeTransitionStatus(RuntimeTransitionStatusRequest request)
    {
        var result = _runtime.GetRuntimeTransitionStatus(new RuntimeTransitionStatusRequestSnapshot(request.RequestId));
        if (result.Error is not null)
        {
            return new Spirectl.Proto.V0.RuntimeTransitionStatusResult
            {
                Error = ToProtoBridgeError(result.Error),
            };
        }

        var response = new RuntimeTransitionStatusResponse
        {
            Source = ToProtoDataSource(result.Source),
            Provisional = result.Provisional,
            Screen = new ScreenInfo
            {
                Id = result.ScreenType,
                Title = result.ScreenTitle,
                ScreenInstanceId = result.ScreenInstanceId,
            },
            Quiescent = result.Quiescent,
            BlockingCount = (uint)Math.Max(0, result.BlockingCount),
            IgnoredInfiniteCount = (uint)Math.Max(0, result.IgnoredInfiniteCount),
        };
        response.Blockers.Add(result.Blockers.Select(ToProtoRuntimeTransitionBlocker));
        response.Notes.Add(result.Notes);

        return new Spirectl.Proto.V0.RuntimeTransitionStatusResult
        {
            Success = response,
        };
    }

    // The presentation resource-scene / localization inspector RPCs were removed along with
    // the `presentation catalog` CLI commands. The proto surface stays defined; the handlers
    // collapse to a single "removed" response, matching the checkpoint handlers above.
    public Spirectl.Proto.V0.PresentationResourceSceneResult HandleInspectPresentationResourceScenes(
        PresentationResourceSceneRequest request)
        => new() { Error = RemovedRpcError("InspectPresentationResourceScenes") };

    public Spirectl.Proto.V0.PresentationLocalizationResult HandleInspectPresentationLocalization(
        PresentationLocalizationRequest request)
        => new() { Error = RemovedRpcError("InspectPresentationLocalization") };

    public FixtureLoadResult HandleLoadFixture(FixtureLoadRequest request)
    {
        if (ValidateFixtureRequest(request) is { } validationError)
        {
            return new FixtureLoadResult
            {
                Error = validationError,
            };
        }

        var result = _runtime.LoadFixture(new FixtureLoadRequestSnapshot(
            request.RequestId,
            request.SchemaVersion,
            request.FixtureName,
            request.SourcePath,
            request.FixtureJson));

        if (result.Error is not null)
        {
            return new FixtureLoadResult
            {
                Error = ToProtoBridgeError(result.Error),
            };
        }

        var response = new FixtureLoadResponse
        {
            RequestId = result.RequestId,
            FixtureName = result.FixtureName,
            SourcePath = result.SourcePath,
            Source = ToProtoDataSource(result.Source),
            Provisional = result.Provisional,
            Screen = new ScreenInfo
            {
                Id = result.ScreenType,
                Title = result.ScreenTitle,
                ScreenInstanceId = result.ScreenInstanceId,
            },
            ResolvedPerspective = ToProtoPerspective(result.ResolvedPerspective),
        };
        response.Notices.Add(result.Notices.Select(ToProtoFixtureNotice));
        if (result.RecipeReport is not null)
        {
            response.RecipeReport = ToProtoFixtureRecipeReport(result.RecipeReport);
        }

        return new FixtureLoadResult
        {
            Success = response,
        };
    }

    public RecordedFixtureResult HandleRecordFixture(RecordedFixtureRequest request)
    {
        var result = _runtime.RecordFixture(new RecordedFixtureRequestSnapshot(
            request.RequestId,
            request.Perspective?.Scope.ToString() ?? string.Empty,
            request.Perspective?.PlayerId ?? string.Empty));
        if (result.Error is not null)
        {
            return new RecordedFixtureResult
            {
                Error = ToProtoBridgeError(result.Error),
            };
        }

        var success = result.Success!;
        var response = new RecordedFixtureResponse
        {
            RequestId = success.RequestId,
            FixtureYaml = success.FixtureYaml,
            FixtureJson = success.FixtureJson,
            Metadata = ToProtoRecordedFixtureMetadata(success.Metadata),
            Source = ToProtoDataSource(success.Source),
            Provisional = success.Provisional,
        };
        return new RecordedFixtureResult
        {
            Success = response,
        };
    }

    public ScreenshotResult HandleGetScreenshot(ScreenshotRequest request)
    {
        var capture = _runtime.CaptureScreenshot(new ScreenshotCaptureRequest(
            request.ViewportWidth > 0 ? (int)request.ViewportWidth : null,
            request.ViewportHeight > 0 ? (int)request.ViewportHeight : null));
        if (capture.Error is not null)
        {
            return new ScreenshotResult
            {
                Error = ToProtoBridgeError(capture.Error),
            };
        }

        return new ScreenshotResult
        {
            Success = new ScreenshotResponse
            {
                Source = ToProtoDataSource(capture.Source),
                Provisional = capture.Provisional,
                Format = capture.Format,
                Width = (uint)capture.Width,
                Height = (uint)capture.Height,
                Contents = Google.Protobuf.ByteString.CopyFrom(capture.Contents),
                ScreenType = capture.ScreenType,
                ScreenInstanceId = capture.ScreenInstanceId,
                RequestedViewportWidth = (uint)(capture.RequestedViewportWidth ?? 0),
                RequestedViewportHeight = (uint)(capture.RequestedViewportHeight ?? 0),
                AppliedViewportWidth = (uint)capture.AppliedViewportWidth,
                AppliedViewportHeight = (uint)capture.AppliedViewportHeight,
                RestoredViewport = capture.RestoredViewport,
                RestoredViewportWidth = (uint)(capture.RestoredViewportWidth ?? 0),
                RestoredViewportHeight = (uint)(capture.RestoredViewportHeight ?? 0),
            }
        };
    }

    public AssetExtractResult HandleExtractAsset(AssetExtractRequest request)
    {
        return _protocol.HandleExtractAsset(request);
    }

    public AssetExplainResult HandleExplainAsset(AssetExplainRequest request)
    {
        return _protocol.HandleExplainAsset(request);
    }

    public AssetCatalogResult HandleGetAssetCatalog(AssetCatalogRequest request)
    {
        return _protocol.HandleGetAssetCatalog(request);
    }

    public ModelCatalogResult HandleGetModels(ModelCatalogRequest request)
    {
        return _protocol.HandleGetModels(request);
    }

    public CombatPreviewResult HandleGetCombatPreview(CombatPreviewRequest request)
    {
        return _protocol.HandleGetCombatPreview(request);
    }

    public MapDrawingsResult HandleGetMapDrawings(MapDrawingsRequest request)
    {
        return _protocol.HandleGetMapDrawings(request);
    }

    public ReferenceResult HandleGetReference(ReferenceRequest request)
    {
        return _protocol.HandleGetReference(request);
    }

    public override Task<HandshakeResult> Handshake(HandshakeRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleHandshake(request));
    }

    public override Task<StateResult> GetState(StateRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleGetState(request));
    }

    public override Task<ActionResult> ExecuteAction(ActionRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleExecuteAction(request));
    }

    public override Task<LogsResult> GetLogs(LogsRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleGetLogs(request));
    }

    public override Task<ConsoleCommandResult> ExecuteConsoleCommand(
        ConsoleCommandRequest request,
        ServerCallContext context)
    {
        return Task.FromResult(HandleExecuteConsoleCommand(request));
    }

    public override Task<DebugStatusResult> GetDebugStatus(DebugStatusRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleGetDebugStatus(request));
    }

    public override Task<DebugSessionStartResult> StartDebugSession(DebugSessionStartRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleStartDebugSession(request));
    }

    public override Task<DebugSessionStatusResult> GetDebugSessionStatus(DebugSessionStatusRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleGetDebugSessionStatus(request));
    }

    public override Task<DebugSessionEndResult> EndDebugSession(DebugSessionEndRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleEndDebugSession(request));
    }

    public override Task<DebugPauseResult> PauseDebug(DebugPauseRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandlePauseDebug(request));
    }

    public override Task<DebugResumeResult> ResumeDebug(DebugResumeRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleResumeDebug(request));
    }

    public override Task<DebugStepResult> StepDebug(DebugStepRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleStepDebug(request));
    }

    public override Task<DebugWaitResult> WaitDebug(DebugWaitRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleWaitDebug(request));
    }

    public override Task<DebugBreakpointListResult> ListBreakpoints(DebugBreakpointListRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleListBreakpoints(request));
    }

    public override Task<DebugBreakpointAddResult> AddBreakpoint(DebugBreakpointAddRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleAddBreakpoint(request));
    }

    public override Task<DebugBreakpointRemoveResult> RemoveBreakpoint(DebugBreakpointRemoveRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleRemoveBreakpoint(request));
    }

    public override Task<DebugEventStreamResult> GetDebugEvents(DebugEventStreamRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleGetDebugEvents(request));
    }

    public override Task<Spirectl.Proto.V0.RuntimeSceneTreeResult> GetRuntimeSceneTree(RuntimeSceneTreeRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleGetRuntimeSceneTree(request));
    }

    public override Task<Spirectl.Proto.V0.RuntimeSceneNodeResult> GetRuntimeSceneNode(RuntimeSceneNodeRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleGetRuntimeSceneNode(request));
    }

    public override Task<Spirectl.Proto.V0.RuntimeSceneSetVisibleResult> SetRuntimeSceneNodeVisible(RuntimeSceneSetVisibleRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleSetRuntimeSceneNodeVisible(request));
    }

    public override Task<Spirectl.Proto.V0.RuntimeSceneControlHoverResult> HoverRuntimeSceneControl(RuntimeSceneControlHoverRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleHoverRuntimeSceneControl(request));
    }

    public override Task<Spirectl.Proto.V0.RuntimeSceneControlUnhoverResult> UnhoverRuntimeSceneControl(RuntimeSceneControlUnhoverRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleUnhoverRuntimeSceneControl(request));
    }

    public override Task<Spirectl.Proto.V0.RuntimeTransitionStatusResult> GetRuntimeTransitionStatus(RuntimeTransitionStatusRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleGetRuntimeTransitionStatus(request));
    }

    public override Task<Spirectl.Proto.V0.PresentationResourceSceneResult> InspectPresentationResourceScenes(
        PresentationResourceSceneRequest request,
        ServerCallContext context)
    {
        return Task.FromResult(HandleInspectPresentationResourceScenes(request));
    }

    public override Task<Spirectl.Proto.V0.PresentationLocalizationResult> InspectPresentationLocalization(
        PresentationLocalizationRequest request,
        ServerCallContext context)
    {
        return Task.FromResult(HandleInspectPresentationLocalization(request));
    }

    public override Task<FixtureLoadResult> LoadFixture(FixtureLoadRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleLoadFixture(request));
    }

    public override Task<RecordedFixtureResult> RecordFixture(RecordedFixtureRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleRecordFixture(request));
    }

    public override Task<ScreenshotResult> GetScreenshot(ScreenshotRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleGetScreenshot(request));
    }

    public override Task<AssetCatalogResult> GetAssetCatalog(AssetCatalogRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleGetAssetCatalog(request));
    }

    public override Task<ModelCatalogResult> GetModels(ModelCatalogRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleGetModels(request));
    }

    public override Task<CombatPreviewResult> GetCombatPreview(CombatPreviewRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleGetCombatPreview(request));
    }

    public override Task<ReferenceResult> GetReference(ReferenceRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleGetReference(request));
    }

    public override Task<AssetExtractResult> ExtractAsset(AssetExtractRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleExtractAsset(request));
    }

    public override Task<AssetExplainResult> ExplainAsset(AssetExplainRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleExplainAsset(request));
    }

    public override Task<GameCloseResult> CloseGame(GameCloseRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleCloseGame(request));
    }

    public override Task<ModListResult> GetMods(ModListRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleGetMods(request));
    }

    public override Task<ScenarioCaptureResult> CaptureScenario(ScenarioCaptureRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleCaptureScenario(request));
    }

    public override Task<ScenarioRestoreResult> RestoreScenario(ScenarioRestoreRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleRestoreScenario(request));
    }

    public override Task<CheckpointCaptureResult> CaptureCheckpoint(CheckpointCaptureRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleCaptureCheckpoint(request));
    }

    public override Task<CheckpointRestoreResult> RestoreCheckpoint(CheckpointRestoreRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleRestoreCheckpoint(request));
    }

    public override Task<CheckpointListResult> ListCheckpoints(CheckpointListRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleListCheckpoints(request));
    }

    public override Task<CheckpointDeleteResult> DeleteCheckpoint(CheckpointDeleteRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleDeleteCheckpoint(request));
    }

    public override Task<HotReloadStatusResult> GetHotReloadStatus(HotReloadStatusRequest request, ServerCallContext context)
    {
        return Task.FromResult(HandleGetHotReloadStatus(request));
    }

    public override Task<HotReloadResult> RequestHotReload(HotReloadRequest request, ServerCallContext context)
    {
        return HandleRequestHotReloadAsync(request);
    }

}

internal static class FluentProtoExtensions
{
    public static T Also<T>(this T value, Action<T> apply)
    {
        apply(value);
        return value;
    }
}
