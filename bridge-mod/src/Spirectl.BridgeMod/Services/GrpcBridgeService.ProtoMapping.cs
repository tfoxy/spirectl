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

public sealed partial class GrpcBridgeService
{
    private static ActionResult InvalidAction(string field, string value, string note)
    {
        return new ActionResult
        {
            Error = new BridgeError
            {
                Code = BridgeErrorCode.InvalidAction,
                Message = field == "choice_id"
                    ? "choose requires a stable choice id."
                    : field == "card_id"
                        ? "play-card requires a stable card id."
                        : field == "character_id"
                            ? "select-character requires a stable character id."
                        : "Action request did not include a semantic action payload.",
                Details =
                {
                    new ErrorDetail
                    {
                        Field = field,
                        Value = value,
                        Note = note,
                    },
                },
            },
        };
    }

    private static BridgeError ToProtoBridgeError(HotReloadFailureSnapshot error)
    {
        return new BridgeError
        {
            Code = error.Code switch
            {
                "hot_reload_shell_unsupported" => BridgeErrorCode.NotImplemented,
                "hot_reload_shell_not_running" => BridgeErrorCode.BridgeNotAttached,
                _ => BridgeErrorCode.RuntimeFailure,
            },
            Message = error.Message,
        };
    }

    private static HotReloadShellStatus ToProtoHotReloadShellStatus(HotReloadShellStatusSnapshot status)
    {
        var proto = new HotReloadShellStatus
        {
            Supported = status.Supported,
            Protocol = status.Protocol is null ? null : new HotReloadProtocolInfo
            {
                Id = status.Protocol.Id,
                Version = status.Protocol.Version,
            },
            ShellModId = status.ShellModId,
            ShellProtocolVersion = status.ShellProtocolVersion,
            ActiveGeneration = status.ActiveGeneration,
            ExpectedLogicArtifactPath = status.ExpectedLogicArtifactPath,
            ContractVersion = status.ContractVersion,
            ReloadInProgress = status.ReloadInProgress,
            LastReloadReport = status.LastReloadReport is null ? null : ToProtoHotReloadReport(status.LastReloadReport),
            RestartRequired = status.RestartRequired,
        };
        proto.Notices.Add(status.Notices.Select(ToProtoHotReloadNotice));
        return proto;
    }

    private static HotReloadReport ToProtoHotReloadReport(HotReloadReportSnapshot report)
    {
        var proto = new HotReloadReport
        {
            Status = report.Status,
            Generation = report.Generation,
            RequestedAt = report.RequestedAt,
            SourceAssemblyPath = report.SourceAssemblyPath,
            ShadowAssemblyPath = report.ShadowAssemblyPath,
            ContractVersion = report.ContractVersion,
            LogicAssemblyName = report.LogicAssemblyName,
            EntryType = report.EntryType,
            PreviousGeneration = report.PreviousGeneration,
            PreviousRemainsActive = report.PreviousRemainsActive,
            PreviousDisposed = report.PreviousDisposed,
            PreviousUnloadRequested = report.PreviousUnloadRequested,
            PreviousCollected = report.PreviousCollected,
            DurationMs = report.DurationMs,
            Error = report.Error is null ? null : new HotReloadError
            {
                Code = report.Error.Code,
                Phase = report.Error.Phase,
                Message = report.Error.Message,
                ExceptionType = report.Error.ExceptionType,
                ExceptionMessage = report.Error.ExceptionMessage,
                RestartRequired = report.Error.RestartRequired,
            },
        };
        proto.Warnings.Add(report.Warnings.Select(warning => new HotReloadWarning
        {
            Code = warning.Code,
            Phase = warning.Phase ?? "",
            Message = warning.Message,
        }));
        return proto;
    }

    private static HotReloadNotice ToProtoHotReloadNotice(HotReloadNoticeSnapshot notice)
    {
        return new HotReloadNotice
        {
            Code = notice.Code,
            Message = notice.Message,
        };
    }

    private static string BuildConsoleLine(string command, IReadOnlyList<string> args)
    {
        return args.Count == 0 ? command : command + " " + string.Join(" ", args);
    }

    private static ConsoleCommandResponse ToProtoConsoleCommandResponse(ConsoleCommandExecutionResult result)
    {
        var response = new ConsoleCommandResponse
        {
            RequestId = result.RequestId,
            Command = result.Command,
            Line = result.Line,
            Accepted = result.Accepted,
            Success = result.Success,
            Output = result.Output,
            Source = ToProtoDataSource(result.Source),
            Provisional = result.Provisional,
        };
        response.Args.Add(result.Args);
        response.OutputLines.Add(result.OutputLines);
        response.Notices.Add(result.Notices.Select(ToProtoConsoleCommandNotice));
        return response;
    }

    private static ConsoleCommandNotice ToProtoConsoleCommandNotice(ConsoleCommandNoticeSnapshot notice)
    {
        return new ConsoleCommandNotice
        {
            Code = notice.Code,
            Message = notice.Message,
            Provisional = notice.Provisional,
        };
    }

    private static BridgeError ToProtoBridgeError(ConsoleCommandFailure error)
    {
        var protoError = new BridgeError
        {
            Code = error.Code switch
            {
                ConsoleCommandFailureCode.NotImplemented => BridgeErrorCode.NotImplemented,
                ConsoleCommandFailureCode.InvalidRequest => BridgeErrorCode.InvalidAction,
                ConsoleCommandFailureCode.BridgeNotAttached => BridgeErrorCode.BridgeNotAttached,
                ConsoleCommandFailureCode.RuntimeFailure => BridgeErrorCode.RuntimeFailure,
                _ => BridgeErrorCode.RuntimeFailure,
            },
            Message = error.Message,
        };

        protoError.Details.Add(error.Details.Select(detail => new ErrorDetail
        {
            Field = detail.Field,
            Value = detail.Value,
            Note = detail.Note,
        }));

        return protoError;
    }

    private static BridgeError ToProtoBridgeError(ActionFailure error)
    {
        var protoError = new BridgeError
        {
            Code = error.Code switch
            {
                ActionFailureCode.NotImplemented => BridgeErrorCode.NotImplemented,
                ActionFailureCode.InvalidAction => BridgeErrorCode.InvalidAction,
                ActionFailureCode.BridgeNotAttached => BridgeErrorCode.BridgeNotAttached,
                ActionFailureCode.WrongScreen
                    or ActionFailureCode.WrongPlayer
                    or ActionFailureCode.NotVisible
                    or ActionFailureCode.NotEnabled
                    or ActionFailureCode.StaleId
                    or ActionFailureCode.UnsupportedPerspective
                    or ActionFailureCode.DangerousModeRequired => BridgeErrorCode.InvalidAction,
                _ => BridgeErrorCode.RuntimeFailure,
            },
            Message = error.Message,
        };

        protoError.Details.Add(error.Details.Select(detail => new ErrorDetail
        {
            Field = detail.Field,
            Value = detail.Value,
            Note = detail.Note,
            ActionReasonCode = ToProtoErrorActionFailureReasonCode(detail.ReasonCode ?? error.Code),
            Screen = detail.Screen ?? string.Empty,
            PlayerId = detail.PlayerId ?? string.Empty,
            Perspective = detail.Perspective ?? string.Empty,
            CheckedHookPaths = { detail.CheckedHookPaths ?? [] },
            FieldDiagnostics =
            {
                (detail.FieldDiagnostics ?? []).Select(ToProtoFieldDiagnostic),
            },
            RequestedPlayerId = detail.RequestedPlayerId ?? string.Empty,
            ResolvedOwnerPlayerId = detail.ResolvedOwnerPlayerId ?? string.Empty,
            LocalPlayerId = detail.LocalPlayerId ?? string.Empty,
            HostPlayerId = detail.HostPlayerId ?? string.Empty,
            LocalRole = ToProtoMultiplayerRole(detail.LocalRole),
            Action = detail.Action ?? string.Empty,
            RemoteOrchestration = detail.RemoteOrchestration is null ? null : ToProtoRemoteOrchestration(detail.RemoteOrchestration),
        }));
        protoError.ActionFailure = new Spirectl.Proto.V0.ActionFailureDetail
        {
            ReasonCode = ToProtoErrorActionFailureReasonCode(error.Code),
            Screen = error.Screen ?? string.Empty,
            PlayerId = error.PlayerId ?? string.Empty,
            Perspective = error.Perspective ?? string.Empty,
            CheckedHookPaths = { error.CheckedHookPaths ?? [] },
            FieldDiagnostics =
            {
                (error.FieldDiagnostics ?? []).Select(ToProtoFieldDiagnostic),
            },
            RequestedPlayerId = error.RequestedPlayerId ?? string.Empty,
            ResolvedOwnerPlayerId = error.ResolvedOwnerPlayerId ?? string.Empty,
            LocalPlayerId = error.LocalPlayerId ?? string.Empty,
            HostPlayerId = error.HostPlayerId ?? string.Empty,
            LocalRole = ToProtoMultiplayerRole(error.LocalRole),
            Action = error.Action ?? string.Empty,
            RemoteOrchestration = error.RemoteOrchestration is null ? null : ToProtoRemoteOrchestration(error.RemoteOrchestration),
        };

        return protoError;
    }

    private static BridgeError ToProtoBridgeError(ScreenshotCaptureFailure error)
    {
        var protoError = new BridgeError
        {
            Code = error.Code switch
            {
                ScreenshotFailureCode.NotImplemented => BridgeErrorCode.NotImplemented,
                ScreenshotFailureCode.BridgeNotAttached => BridgeErrorCode.BridgeNotAttached,
                ScreenshotFailureCode.InvalidViewport => BridgeErrorCode.InvalidQueryFilter,
                _ => BridgeErrorCode.RuntimeFailure,
            },
            Message = error.Message,
        };

        protoError.Details.Add(error.Details.Select(detail => new ErrorDetail
        {
            Field = detail.Field,
            Value = detail.Value,
            Note = detail.Note,
        }));

        return protoError;
    }

    private static BridgeError ToProtoBridgeError(LifecycleFailure error)
    {
        var protoError = new BridgeError
        {
            Code = error.Code switch
            {
                LifecycleFailureCode.NotImplemented => BridgeErrorCode.NotImplemented,
                LifecycleFailureCode.BridgeNotAttached => BridgeErrorCode.BridgeNotAttached,
                _ => BridgeErrorCode.RuntimeFailure,
            },
            Message = error.Message,
        };

        protoError.Details.Add(error.Details.Select(detail => new ErrorDetail
        {
            Field = detail.Field,
            Value = detail.Value,
            Note = detail.Note,
        }));

        return protoError;
    }

    private static BridgeError ToProtoBridgeError(AssetExtractFailure error)
    {
        var protoError = new BridgeError
        {
            Code = error.Code switch
            {
                AssetExtractFailureCode.NotImplemented => BridgeErrorCode.NotImplemented,
                AssetExtractFailureCode.BridgeNotAttached => BridgeErrorCode.BridgeNotAttached,
                _ => BridgeErrorCode.RuntimeFailure,
            },
            Message = error.Message,
        };

        protoError.Details.Add(error.Details.Select(detail => new ErrorDetail
        {
            Field = detail.Field,
            Value = detail.Value,
            Note = detail.Note,
            Diagnostic = detail.Diagnostic is null ? null : ToProtoStruct(detail.Diagnostic),
        }));

        return protoError;
    }

    private static Struct ToProtoStruct(IReadOnlyDictionary<string, object?> values)
    {
        var result = new Struct();
        foreach (var (key, value) in values)
        {
            result.Fields[key] = ToProtoValue(value);
        }

        return result;
    }

    private static Google.Protobuf.WellKnownTypes.Value ToProtoValue(object? value)
        => value switch
        {
            null => new Google.Protobuf.WellKnownTypes.Value { NullValue = NullValue.NullValue },
            string text => new Google.Protobuf.WellKnownTypes.Value { StringValue = text },
            bool flag => new Google.Protobuf.WellKnownTypes.Value { BoolValue = flag },
            int number => new Google.Protobuf.WellKnownTypes.Value { NumberValue = number },
            long number => new Google.Protobuf.WellKnownTypes.Value { NumberValue = number },
            float number => new Google.Protobuf.WellKnownTypes.Value { NumberValue = number },
            double number => new Google.Protobuf.WellKnownTypes.Value { NumberValue = number },
            decimal number => new Google.Protobuf.WellKnownTypes.Value { NumberValue = decimal.ToDouble(number) },
            IReadOnlyDictionary<string, object?> map => new Google.Protobuf.WellKnownTypes.Value { StructValue = ToProtoStruct(map) },
            IEnumerable<object?> list => new Google.Protobuf.WellKnownTypes.Value
            {
                ListValue = new ListValue { Values = { list.Select(ToProtoValue) } },
            },
            _ => new Google.Protobuf.WellKnownTypes.Value { StringValue = value.ToString() ?? string.Empty },
        };

    private static BridgeError ToProtoBridgeError(FixtureLoadFailure error)
    {
        var protoError = new BridgeError
        {
            Code = error.Code switch
            {
                FixtureLoadFailureCode.NotImplemented => BridgeErrorCode.NotImplemented,
                FixtureLoadFailureCode.InvalidFixture => BridgeErrorCode.InvalidFixture,
                FixtureLoadFailureCode.BridgeNotAttached => BridgeErrorCode.BridgeNotAttached,
                _ => BridgeErrorCode.RuntimeFailure,
            },
            Message = error.Message,
        };

        protoError.Details.Add(error.Details.Select(detail => new ErrorDetail
        {
            Field = detail.Field,
            Value = detail.Value,
            Note = detail.Note,
        }));

        return protoError;
    }

    private static FixtureRecipeRestoreReport ToProtoFixtureRecipeReport(
        Spirectl.Sts2.Core.Fixtures.FixtureRecipeRestoreReport report)
    {
        var proto = new FixtureRecipeRestoreReport
        {
            RecipeName = report.RecipeName,
            BridgeValidation = ToProtoFixtureBridgeValidation(report.BridgeValidation),
        };
        proto.AppliedFields.Add(report.AppliedFields.Select(ToProtoFixtureRecipeFieldReport));
        proto.InferredFields.Add(report.InferredFields.Select(ToProtoFixtureRecipeFieldReport));
        proto.OmittedFields.Add(report.OmittedFields.Select(ToProtoFixtureRecipeFieldReport));
        proto.UnsupportedFields.Add(report.UnsupportedFields.Select(ToProtoFixtureRecipeFieldReport));
        proto.DegradedMultiplayerFields.Add(report.DegradedMultiplayerFields.Select(ToProtoFixtureRecipeFieldReport));
        return proto;
    }

    private static FixtureRecipeFieldReport ToProtoFixtureRecipeFieldReport(
        Spirectl.Sts2.Core.Fixtures.FixtureRecipeFieldReport report)
        => new()
        {
            FieldPath = report.FieldPath,
            ValueSummary = report.ValueSummary,
            ReasonCode = report.ReasonCode,
            Message = report.Message,
        };

    private static FixtureBridgeValidationResult ToProtoFixtureBridgeValidation(
        Spirectl.Sts2.Core.Fixtures.FixtureBridgeValidationResult validation)
    {
        var proto = new FixtureBridgeValidationResult
        {
            Status = validation.Status,
        };
        proto.Details.Add(validation.Details.Select(ToProtoFixtureRecipeFieldReport));
        return proto;
    }

    private static BridgeError ToProtoBridgeError(RecordedFixtureFailure error)
    {
        var protoError = new BridgeError
        {
            Code = error.Code switch
            {
                RecordedFixtureFailureCode.NotImplemented => BridgeErrorCode.NotImplemented,
                RecordedFixtureFailureCode.InvalidAction => BridgeErrorCode.InvalidAction,
                _ => BridgeErrorCode.RuntimeFailure,
            },
            Message = error.Message,
        };

        protoError.Details.Add(error.Details.Select(detail => new ErrorDetail
        {
            Field = detail.Field,
            Value = detail.Value,
            Note = detail.Note,
        }));

        return protoError;
    }

    private static BridgeError ToProtoBridgeError(ScenarioFailure error)
    {
        var protoError = new BridgeError
        {
            Code = error.Code switch
            {
                ScenarioFailureCode.NotImplemented => BridgeErrorCode.NotImplemented,
                ScenarioFailureCode.InvalidRequest => BridgeErrorCode.InvalidFixture,
                ScenarioFailureCode.InvalidAction => BridgeErrorCode.InvalidAction,
                _ => BridgeErrorCode.RuntimeFailure,
            },
            Message = error.Message,
        };

        protoError.Details.Add(error.Details.Select(detail => new ErrorDetail
        {
            Field = detail.Field,
            Value = detail.Value,
            Note = detail.Note,
        }));

        return protoError;
    }

    private static BridgeError ToProtoBridgeError(RuntimeSceneFailure error)
    {
        var protoError = new BridgeError
        {
            Code = error.Code switch
            {
                RuntimeSceneFailureCode.NotImplemented => BridgeErrorCode.NotImplemented,
                RuntimeSceneFailureCode.InvalidNodePath => BridgeErrorCode.InvalidQueryFilter,
                RuntimeSceneFailureCode.BridgeNotAttached => BridgeErrorCode.BridgeNotAttached,
                _ => BridgeErrorCode.RuntimeFailure,
            },
            Message = error.Message,
        };

        protoError.Details.Add(error.Details.Select(detail => new ErrorDetail
        {
            Field = detail.Field,
            Value = detail.Value,
            Note = detail.Note,
        }));

        return protoError;
    }

    private static BridgeError? ValidateFixtureRequest(FixtureLoadRequest request)
    {
        if (!string.Equals(request.SchemaVersion, "spirectl.fixture/v0", StringComparison.Ordinal))
        {
            return InvalidFixtureRequest(
                "schema_version",
                request.SchemaVersion,
                "Only spirectl.fixture/v0 is supported for bridge-backed fixture loading.");
        }

        try
        {
            using var document = JsonDocument.Parse(request.FixtureJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return InvalidFixtureRequest(
                    "fixture_json",
                    request.FixtureJson,
                    "Fixture JSON must deserialize to an object.");
            }

            if (!document.RootElement.TryGetProperty("screen", out var screen)
                || screen.ValueKind != JsonValueKind.String)
            {
                return InvalidFixtureRequest(
                    "screen",
                    string.Empty,
                    $"Fixture JSON must include a supported executable fixture screen such as combat, {Sts2SupportedScreenIds.EventRoomScreenId}, {Sts2SupportedScreenIds.StartRunLobbyScreenId}, or {Sts2SupportedScreenIds.LoadRunLobbyScreenId}.");
            }

            var screenValue = screen.GetString() ?? string.Empty;
            if (!string.Equals(screenValue, "main-menu", StringComparison.Ordinal)
                && !Sts2SupportedScreenIds.IsMapScreenType(screenValue)
                && !Sts2SupportedScreenIds.IsRewardsScreenType(screenValue)
                && !Sts2SupportedScreenIds.IsRestSiteScreenType(screenValue)
                && !Sts2SupportedScreenIds.IsEventRoomScreenType(screenValue)
                && !Sts2SupportedScreenIds.IsCrystalSphereScreenType(screenValue)
                && !Sts2SupportedScreenIds.IsTreasureRoomFamily(screenValue)
                && !Sts2SupportedScreenIds.IsShopScreenType(screenValue)
                && !Sts2SupportedScreenIds.IsCardSelectionFamily(screenValue)
                && !Sts2SupportedScreenIds.IsGameOverScreenType(screenValue)
                && !string.Equals(screenValue, "card-overlay", StringComparison.Ordinal)
                && !string.Equals(screenValue, "passive-card-overlay", StringComparison.Ordinal)
                && !string.Equals(screenValue, "combat", StringComparison.Ordinal)
                && !IsLobbyFixtureScreen(screenValue))
            {
                return InvalidFixtureRequest(
                    "screen",
                    screenValue,
                    $"The bridge fixture loader currently supports main-menu, combat, card-overlay families, and game-derived screen ids including {Sts2SupportedScreenIds.MapScreenId}, {Sts2SupportedScreenIds.RewardsScreenId}, {Sts2SupportedScreenIds.RestSiteRoomScreenId}, {Sts2SupportedScreenIds.EventRoomScreenId}, {Sts2SupportedScreenIds.CrystalSphereScreenId}, {Sts2SupportedScreenIds.TreasureRoomScreenId}, {Sts2SupportedScreenIds.RelicSelectionScreenId}, {Sts2SupportedScreenIds.ShopScreenId}, {Sts2SupportedScreenIds.GameOverScreenId}, card-selection families, {Sts2SupportedScreenIds.StartRunLobbyScreenId}, and {Sts2SupportedScreenIds.LoadRunLobbyScreenId}.");
            }

        }
        catch (JsonException ex)
        {
            return InvalidFixtureRequest(
                "fixture_json",
                request.FixtureJson,
                $"Fixture JSON was not valid canonical JSON from the CLI: {ex.Message}");
        }

        return null;
    }


    private static bool IsLobbyFixtureScreen(string screen)
        => Sts2SupportedScreenIds.IsLobbyScreenType(screen);

    private static BridgeError InvalidFixtureRequest(string field, string value, string note)
    {
        return new BridgeError
        {
            Code = BridgeErrorCode.InvalidFixture,
            Message = "Fixture request does not match the supported bridge contract.",
            Details =
            {
                new ErrorDetail
                {
                    Field = field,
                    Value = value,
                    Note = note,
                },
            },
        };
    }

    private static PerspectiveSelection ToPerspectiveSelection(PerspectiveSelector selector)
    {
        return new PerspectiveSelection(
            selector.Scope == PerspectiveScope.Omniscient ? PlayerScope.Omniscient : PlayerScope.Local,
            string.IsNullOrWhiteSpace(selector.PlayerId) ? null : selector.PlayerId);
    }

    private static PerspectiveInfo ToProtoPerspective(PlayerPerspective perspective)
    {
        return new PerspectiveInfo
        {
            Scope = perspective.Scope == PlayerScope.Omniscient
                ? PerspectiveScope.Omniscient
                : PerspectiveScope.Local,
            PlayerId = perspective.PlayerId ?? string.Empty,
            UsesDefault = perspective.UsesDefault,
        };
    }

    private static Capability ToProtoCapability(BridgeCapabilitySnapshot capability)
    {
        return new Capability
        {
            Id = capability.Id,
            Summary = capability.Summary,
            Provisional = capability.Provisional,
            RemoteOrchestration = capability.RemoteOrchestration is null ? null : ToProtoRemoteOrchestration(capability.RemoteOrchestration),
        };
    }

    private static Spirectl.Proto.V0.ActionDescriptor ToProtoActionDescriptor(ActionDescriptorSnapshot action)
    {
        var response = new Spirectl.Proto.V0.ActionDescriptor
        {
            Id = action.Id,
            Kind = ToProtoActionKind(action.Kind),
            Summary = action.Summary,
            CliCommandHint = action.CliCommandHint,
            Provisional = action.Provisional,
            Status = ToProtoActionStatus(action.Status),
        };
        response.Parameters.Add(action.Parameters.Select(ToProtoActionParameter));
        return response;
    }

    private static ActionParameter ToProtoActionParameter(ActionParameterDescriptorSnapshot parameter)
    {
        return new ActionParameter
        {
            Name = parameter.Name,
            ValueType = parameter.ValueType,
            Required = parameter.Required,
            Summary = parameter.Summary,
        };
    }

    private static StateNotice ToProtoStateNotice(StateNoticeSnapshot notice)
    {
        return new StateNotice
        {
            Code = notice.Code,
            Message = notice.Message,
            Provisional = notice.Provisional,
            Path = notice.Path ?? string.Empty,
            Severity = notice.Severity ?? string.Empty,
            Source = notice.Source ?? string.Empty,
        };
    }

    private static StateNotice ToProtoFixtureNotice(Spirectl.Sts2.Core.Fixtures.FixtureLoadNotice notice)
    {
        return new StateNotice
        {
            Code = notice.Code,
            Message = notice.Message,
            Provisional = notice.Provisional,
        };
    }

    private static RecordedFixtureMetadata ToProtoRecordedFixtureMetadata(
        RecordedFixtureMetadataSnapshot metadata)
    {
        var proto = new RecordedFixtureMetadata
        {
            SchemaVersion = metadata.SchemaVersion,
            RecordedAt = metadata.RecordedAt,
            Screen = new ScreenInfo
            {
                Id = metadata.Screen.Type,
                Title = metadata.Screen.Title,
                ScreenInstanceId = metadata.Screen.ScreenInstanceId,
            },
            GameVersion = metadata.GameVersion,
            BridgeVersion = metadata.BridgeVersion,
            SpirectlVersion = metadata.SpirectlVersion,
            RestoreQuality = ToProtoRestoreQuality(metadata.RestoreQuality),
        };
        proto.KnownOmissions.Add(metadata.KnownOmissions.Select(ToProtoCompatibilityNote));
        proto.Notices.Add(metadata.Notices.Select(ToProtoRecordedFixtureNotice));
        return proto;
    }

    private static RestoreCompatibilityNote ToProtoCompatibilityNote(
        RecordedFixtureCompatibilityNoteSnapshot note)
        => new()
        {
            Code = note.Code,
            Message = note.Message,
            Field = note.Field,
        };

    private static StateNotice ToProtoRecordedFixtureNotice(RecordedFixtureNoticeSnapshot notice)
        => new()
        {
            Code = notice.Code,
            Message = notice.Message,
            Provisional = notice.Provisional,
        };

    private static ScenarioDocument ToProtoScenarioDocument(ScenarioDocumentSnapshot document)
    {
        var proto = new ScenarioDocument
        {
            SchemaVersion = document.SchemaVersion,
            Name = document.Name,
            Description = document.Description,
            CreatedAt = document.CreatedAt,
            Source = new ScenarioSourceMetadata
            {
                GameVersion = document.Source.GameVersion,
                BridgeVersion = document.Source.BridgeVersion,
                SpirectlVersion = document.Source.SpirectlVersion,
                Screen = ToProtoScenarioScreen(document.Source.Screen),
                Perspective = document.Source.Perspective is null ? null : ToProtoScenarioPerspective(document.Source.Perspective),
            },
            Restore = new RestoreMetadata
            {
                Mode = ToProtoRestoreMode(document.Restore.Mode),
                Quality = ToProtoRestoreQuality(document.Restore.Quality),
                ExactBundle = document.Restore.ExactBundle is null ? null : new ExactBundleMetadata
                {
                    Path = document.Restore.ExactBundle.Path,
                    FormatVersion = document.Restore.ExactBundle.FormatVersion,
                    Sha256 = document.Restore.ExactBundle.Sha256,
                    SizeBytes = document.Restore.ExactBundle.SizeBytes,
                    VersionSensitive = document.Restore.ExactBundle.VersionSensitive,
                    ContentType = document.Restore.ExactBundle.ContentType,
                },
            },
            Run = document.Run is null ? null : ToProtoScenarioRun(document.Run),
            ScreenStateJson = document.ScreenStateJson,
            Multiplayer = document.Multiplayer is null ? null : ToProtoMultiplayerRestoreMetadata(document.Multiplayer),
        };
        proto.Restore.CompatibilityNotes.Add(document.Restore.CompatibilityNotes.Select(ToProtoCompatibilityNote));
        proto.Restore.FieldReports.Add(document.Restore.FieldReports.Select(ToProtoRestoreFieldReport));
        proto.Notices.Add(document.Notices.Select(ToProtoScenarioNotice));
        return proto;
    }

    private static ScenarioDocumentSnapshot FromProtoScenarioDocument(ScenarioDocument document)
    {
        return new ScenarioDocumentSnapshot(
            document.SchemaVersion,
            document.Name,
            document.Description,
            document.CreatedAt,
            new ScenarioSourceSnapshot(
                document.Source?.GameVersion ?? string.Empty,
                document.Source?.BridgeVersion ?? string.Empty,
                document.Source?.SpirectlVersion ?? string.Empty,
                FromProtoScenarioScreen(document.Source?.Screen),
                document.Source?.Perspective is null ? null : new ScenarioPerspectiveSnapshot(
                    document.Source.Perspective.Scope.ToString().ToLowerInvariant(),
                    document.Source.Perspective.PlayerId)),
            new ScenarioRestoreSnapshot(
                FromProtoRestoreMode(document.Restore?.Mode ?? RestoreMode.Unspecified),
                FromProtoScenarioRestoreQuality(document.Restore?.Quality ?? RestoreQuality.Unspecified),
                document.Restore?.ExactBundle is null ? null : new ScenarioExactBundleMetadataSnapshot(
                    document.Restore.ExactBundle.Path,
                    document.Restore.ExactBundle.FormatVersion,
                    document.Restore.ExactBundle.Sha256,
                    document.Restore.ExactBundle.SizeBytes,
                    document.Restore.ExactBundle.VersionSensitive,
                    document.Restore.ExactBundle.ContentType),
                document.Restore?.CompatibilityNotes.Select(note => new ScenarioCompatibilityNoteSnapshot(note.Code, note.Message, note.Field)).ToArray() ?? [],
                document.Restore?.FieldReports.Select(FromProtoRestoreFieldReport).ToArray() ?? []),
            document.Run is null ? null : FromProtoScenarioRun(document.Run),
            document.ScreenStateJson,
            [.. document.Notices.Select(notice => new ScenarioNoticeSnapshot(notice.Code, notice.Message, notice.Provisional))],
            document.Multiplayer is null ? null : FromProtoMultiplayerRestoreMetadata(document.Multiplayer));
    }

    private static ScenarioRunState ToProtoScenarioRun(ScenarioRunSnapshot run)
    {
        var proto = new ScenarioRunState
        {
            Seed = run.Seed,
            Act = run.Act,
            Floor = run.Floor,
            Ascension = run.Ascension,
        };
        proto.Players.Add(run.Players.Select(player => new ScenarioPlayerState
        {
            Id = player.Id,
            Character = player.Character,
            IsLocal = player.IsLocal,
            IsHost = player.IsHost,
            IsRemote = player.IsRemote,
        }));
        return proto;
    }

    private static ScenarioRunSnapshot FromProtoScenarioRun(ScenarioRunState run)
    {
        return new ScenarioRunSnapshot(
            run.Seed,
            run.Act,
            run.Floor,
            run.Ascension,
            [.. run.Players.Select(player => new ScenarioPlayerSnapshot(
                player.Id,
                player.Character,
                player.IsLocal,
                player.IsHost,
                player.IsRemote))]);
    }

    private static ScreenInfo ToProtoScenarioScreen(ScenarioScreenSnapshot screen)
    {
        return new ScreenInfo
        {
            Id = screen.Type,
            Title = screen.Title,
            ScreenInstanceId = screen.ScreenInstanceId,
        };
    }

    private static ScenarioScreenSnapshot FromProtoScenarioScreen(ScreenInfo? screen)
    {
        return new ScenarioScreenSnapshot(
            screen?.Id ?? string.Empty,
            screen?.Title ?? string.Empty,
            screen?.ScreenInstanceId ?? string.Empty);
    }

    private static PerspectiveInfo ToProtoScenarioPerspective(ScenarioPerspectiveSnapshot perspective)
    {
        return new PerspectiveInfo
        {
            Scope = perspective.Scope == "omniscient" ? PerspectiveScope.Omniscient : PerspectiveScope.Local,
            PlayerId = perspective.PlayerId,
            UsesDefault = false,
        };
    }

    private static RestoreCompatibilityNote ToProtoCompatibilityNote(ScenarioCompatibilityNoteSnapshot note)
    {
        return new RestoreCompatibilityNote
        {
            Code = note.Code,
            Message = note.Message,
            Field = note.Field,
        };
    }

    private static RestoreFieldReport ToProtoRestoreFieldReport(RestoreFieldReportSnapshot report)
    {
        return new RestoreFieldReport
        {
            Path = report.Path,
            Capture = ToProtoRestoreFieldFidelity(report.Capture),
            Restore = ToProtoRestoreFieldFidelity(report.Restore),
            ValidationKey = report.ValidationKey,
            ReasonCode = report.ReasonCode,
            Message = report.Message,
        };
    }

    private static RestoreFieldReportSnapshot FromProtoRestoreFieldReport(RestoreFieldReport report)
        => new(
            report.Path,
            FromProtoRestoreFieldFidelity(report.Capture),
            FromProtoRestoreFieldFidelity(report.Restore),
            report.ValidationKey,
            report.ReasonCode,
            report.Message);

    private static RestoreVerification ToProtoRestoreVerification(RestoreVerificationSnapshot verification)
    {
        var proto = new RestoreVerification
        {
            Status = ToProtoRestoreVerificationStatus(verification.Status),
            Quality = ToProtoRestoreQuality(verification.Quality),
            ExpectedSummaryJson = verification.ExpectedSummaryJson,
            ObservedSummaryJson = verification.ObservedSummaryJson,
        };
        proto.CheckedFields.Add(verification.CheckedFields);
        proto.Mismatches.Add(verification.Mismatches.Select(mismatch => new RestoreMismatch
        {
            Path = mismatch.Path,
            ExpectedJson = mismatch.ExpectedJson,
            ObservedJson = mismatch.ObservedJson,
            Severity = mismatch.Severity,
            SupportClass = mismatch.SupportClass,
            ReasonCode = mismatch.ReasonCode,
            SuggestedNextStep = mismatch.SuggestedNextStep,
        }));
        return proto;
    }

    private static MultiplayerRestoreMetadata ToProtoMultiplayerRestoreMetadata(MultiplayerRestoreSnapshot metadata)
    {
        var proto = new MultiplayerRestoreMetadata
        {
            IsMultiplayer = metadata.IsMultiplayer,
            RestoreMode = ToProtoMultiplayerRestoreMode(metadata.RestoreMode),
            LocalPlayerId = metadata.LocalPlayerId,
            HostPlayerId = metadata.HostPlayerId,
            LocalPlayerRole = metadata.LocalPlayerRole,
            Lobby = metadata.Lobby is null ? null : new Spirectl.Proto.V0.MultiplayerLobbySnapshot
            {
                LobbyId = metadata.Lobby.LobbyId,
                Phase = metadata.Lobby.Phase,
            },
            RequiresRemoteClients = metadata.RequiresRemoteClients,
            DegradedLocalOnlyAvailable = metadata.DegradedLocalOnlyAvailable,
        };
        proto.Players.Add(metadata.Players.Select(player => new MultiplayerPlayerState
        {
            Id = player.Id,
            NetId = player.NetId,
            SlotId = player.SlotId,
            DisplayName = player.DisplayName,
            SelectedCharacterId = player.SelectedCharacterId,
            IsReady = player.IsReady,
            IsLocal = player.IsLocal,
            IsHost = player.IsHost,
            IsRemote = player.IsRemote,
            Character = player.Character,
        }));
        proto.Lobby?.AvailableCharacters.Add(metadata.Lobby!.AvailableCharacters.Select(character => new LobbyCharacter
        {
            Id = character.Id,
            Name = character.Name,
            IsUnlocked = character.IsUnlocked,
        }));

        proto.Limitations.Add(metadata.Limitations.Select(note => new RestoreCompatibilityNote
        {
            Code = note.Code,
            Message = note.Message,
            Field = note.Field,
        }));
        return proto;
    }

    private static MultiplayerRestoreSnapshot FromProtoMultiplayerRestoreMetadata(MultiplayerRestoreMetadata metadata)
        => new(
            metadata.IsMultiplayer,
            FromProtoMultiplayerRestoreMode(metadata.RestoreMode),
            metadata.LocalPlayerId,
            metadata.HostPlayerId,
            metadata.LocalPlayerRole,
            [.. metadata.Players.Select(player => new MultiplayerPlayerSnapshot(
                player.Id,
                player.NetId,
                player.SlotId,
                player.DisplayName,
                player.SelectedCharacterId,
                player.IsReady,
                player.IsLocal,
                player.IsHost,
                player.IsRemote,
                player.Character))],
            metadata.Lobby is null ? null : new MultiplayerLobbySnapshot(
                metadata.Lobby.LobbyId,
                metadata.Lobby.Phase,
                [.. metadata.Lobby.AvailableCharacters.Select(character => new RestoreLobbyCharacterSnapshot(
                    character.Id,
                    character.Name,
                    character.IsUnlocked))]),
            metadata.RequiresRemoteClients,
            metadata.DegradedLocalOnlyAvailable,
            [.. metadata.Limitations.Select(note => new MultiplayerRestoreLimitationSnapshot(
                note.Code,
                note.Message,
                note.Field))]);

    private static MultiplayerRestoreResult ToProtoMultiplayerRestoreResult(MultiplayerRestoreResultSnapshot result)
    {
        var proto = new MultiplayerRestoreResult
        {
            Mode = ToProtoMultiplayerRestoreMode(result.Mode),
            RemotePlayerMode = result.RemotePlayerMode,
            LocalPlayerId = result.LocalPlayerId,
            HostPlayerId = result.HostPlayerId,
            RequiresRemoteClients = result.RequiresRemoteClients,
        };
        proto.RestoredPlayerIds.Add(result.RestoredPlayerIds);
        proto.OmittedRemotePlayerIds.Add(result.OmittedRemotePlayerIds);
        return proto;
    }

    private static MultiplayerRestoreMode ToProtoMultiplayerRestoreMode(MultiplayerRestoreModeSnapshot mode)
    {
        return mode switch
        {
            MultiplayerRestoreModeSnapshot.LobbyOnly => MultiplayerRestoreMode.LobbyOnly,
            MultiplayerRestoreModeSnapshot.HostLocalActiveRun => MultiplayerRestoreMode.HostLocalActiveRun,
            MultiplayerRestoreModeSnapshot.RemotePlayerPlaceholder => MultiplayerRestoreMode.RemotePlayerPlaceholder,
            MultiplayerRestoreModeSnapshot.FullActiveMultiplayer => MultiplayerRestoreMode.FullActiveMultiplayer,
            MultiplayerRestoreModeSnapshot.UnsupportedRemoteClientRequired => MultiplayerRestoreMode.UnsupportedRemoteClientRequired,
            MultiplayerRestoreModeSnapshot.DegradedLocalOnly => MultiplayerRestoreMode.DegradedLocalOnly,
            MultiplayerRestoreModeSnapshot.ActiveMultiplayerUnsupported => MultiplayerRestoreMode.ActiveMultiplayerUnsupported,
            _ => MultiplayerRestoreMode.Unspecified,
        };
    }

    private static MultiplayerRestoreModeSnapshot FromProtoMultiplayerRestoreMode(MultiplayerRestoreMode mode)
    {
        return mode switch
        {
            MultiplayerRestoreMode.LobbyOnly => MultiplayerRestoreModeSnapshot.LobbyOnly,
            MultiplayerRestoreMode.HostLocalActiveRun => MultiplayerRestoreModeSnapshot.HostLocalActiveRun,
            MultiplayerRestoreMode.RemotePlayerPlaceholder => MultiplayerRestoreModeSnapshot.RemotePlayerPlaceholder,
            MultiplayerRestoreMode.FullActiveMultiplayer => MultiplayerRestoreModeSnapshot.FullActiveMultiplayer,
            MultiplayerRestoreMode.UnsupportedRemoteClientRequired => MultiplayerRestoreModeSnapshot.UnsupportedRemoteClientRequired,
            MultiplayerRestoreMode.DegradedLocalOnly => MultiplayerRestoreModeSnapshot.DegradedLocalOnly,
            MultiplayerRestoreMode.ActiveMultiplayerUnsupported => MultiplayerRestoreModeSnapshot.ActiveMultiplayerUnsupported,
            _ => MultiplayerRestoreModeSnapshot.Unspecified,
        };
    }

    private static RestoreFieldFidelity ToProtoRestoreFieldFidelity(RestoreFieldFidelitySnapshot fidelity)
    {
        return fidelity switch
        {
            RestoreFieldFidelitySnapshot.Exact => RestoreFieldFidelity.Exact,
            RestoreFieldFidelitySnapshot.Partial => RestoreFieldFidelity.Partial,
            RestoreFieldFidelitySnapshot.Inferred => RestoreFieldFidelity.Inferred,
            RestoreFieldFidelitySnapshot.Omitted => RestoreFieldFidelity.Omitted,
            RestoreFieldFidelitySnapshot.Unsupported => RestoreFieldFidelity.Unsupported,
            RestoreFieldFidelitySnapshot.DegradedLocalMultiplayer => RestoreFieldFidelity.DegradedLocalMultiplayer,
            _ => RestoreFieldFidelity.Unspecified,
        };
    }

    private static RestoreFieldFidelitySnapshot FromProtoRestoreFieldFidelity(RestoreFieldFidelity fidelity)
    {
        return fidelity switch
        {
            RestoreFieldFidelity.Exact => RestoreFieldFidelitySnapshot.Exact,
            RestoreFieldFidelity.Partial => RestoreFieldFidelitySnapshot.Partial,
            RestoreFieldFidelity.Inferred => RestoreFieldFidelitySnapshot.Inferred,
            RestoreFieldFidelity.Omitted => RestoreFieldFidelitySnapshot.Omitted,
            RestoreFieldFidelity.Unsupported => RestoreFieldFidelitySnapshot.Unsupported,
            RestoreFieldFidelity.DegradedLocalMultiplayer => RestoreFieldFidelitySnapshot.DegradedLocalMultiplayer,
            _ => RestoreFieldFidelitySnapshot.Unspecified,
        };
    }

    private static RestoreVerificationStatus ToProtoRestoreVerificationStatus(RestoreVerificationStatusSnapshot status)
    {
        return status switch
        {
            RestoreVerificationStatusSnapshot.Passed => RestoreVerificationStatus.Passed,
            RestoreVerificationStatusSnapshot.Partial => RestoreVerificationStatus.Partial,
            RestoreVerificationStatusSnapshot.Degraded => RestoreVerificationStatus.Degraded,
            RestoreVerificationStatusSnapshot.Failed => RestoreVerificationStatus.Failed,
            _ => RestoreVerificationStatus.Unspecified,
        };
    }

    private static RestoreQuality ToProtoRestoreQuality(string quality)
    {
        return quality switch
        {
            "exact" => RestoreQuality.Exact,
            "partial" => RestoreQuality.Partial,
            "unsupported" => RestoreQuality.Unsupported,
            "degraded" => RestoreQuality.Degraded,
            _ => RestoreQuality.Unspecified,
        };
    }

    private static StateNotice ToProtoScenarioNotice(ScenarioNoticeSnapshot notice)
    {
        return new StateNotice
        {
            Code = notice.Code,
            Message = notice.Message,
            Provisional = notice.Provisional,
        };
    }

    private static ExactBundlePayload ToProtoExactBundlePayload(ScenarioExactBundlePayloadSnapshot payload)
    {
        return new ExactBundlePayload
        {
            Path = payload.Path,
            FormatVersion = payload.FormatVersion,
            ContentType = payload.ContentType,
            Data = Google.Protobuf.ByteString.CopyFrom(payload.Data),
        };
    }

    private static RestoreMode ToProtoRestoreMode(ScenarioRestoreMode mode)
    {
        return mode switch
        {
            ScenarioRestoreMode.Sparse => RestoreMode.Sparse,
            ScenarioRestoreMode.Exact => RestoreMode.Exact,
            ScenarioRestoreMode.Hybrid => RestoreMode.Hybrid,
            _ => RestoreMode.Unspecified,
        };
    }

    private static ScenarioRestoreMode FromProtoRestoreMode(RestoreMode mode)
    {
        return mode switch
        {
            RestoreMode.Sparse => ScenarioRestoreMode.Sparse,
            RestoreMode.Exact => ScenarioRestoreMode.Exact,
            RestoreMode.Hybrid => ScenarioRestoreMode.Hybrid,
            _ => ScenarioRestoreMode.Unspecified,
        };
    }

    private static RestoreQuality ToProtoRestoreQuality(ScenarioRestoreQuality quality)
    {
        return quality switch
        {
            ScenarioRestoreQuality.Exact => RestoreQuality.Exact,
            ScenarioRestoreQuality.Partial => RestoreQuality.Partial,
            ScenarioRestoreQuality.Unsupported => RestoreQuality.Unsupported,
            ScenarioRestoreQuality.Degraded => RestoreQuality.Degraded,
            _ => RestoreQuality.Unspecified,
        };
    }

    private static ScenarioRestoreQuality FromProtoScenarioRestoreQuality(RestoreQuality quality)
    {
        return quality switch
        {
            RestoreQuality.Exact => ScenarioRestoreQuality.Exact,
            RestoreQuality.Partial => ScenarioRestoreQuality.Partial,
            RestoreQuality.Unsupported => ScenarioRestoreQuality.Unsupported,
            RestoreQuality.Degraded => ScenarioRestoreQuality.Degraded,
            _ => ScenarioRestoreQuality.Unspecified,
        };
    }

    private static RestoreQuality ToProtoRestoreQuality(RecordedFixtureRestoreQuality quality)
    {
        return quality switch
        {
            RecordedFixtureRestoreQuality.Exact => RestoreQuality.Exact,
            RecordedFixtureRestoreQuality.Partial => RestoreQuality.Partial,
            RecordedFixtureRestoreQuality.Unsupported => RestoreQuality.Unsupported,
            RecordedFixtureRestoreQuality.Degraded => RestoreQuality.Degraded,
            _ => RestoreQuality.Unspecified,
        };
    }

    private static DebugStatusResponse ToProtoDebugStatus(DebugStatusSnapshot status)
    {
        return new DebugStatusResponse
        {
            Supported = status.Supported,
            ExecutionState = ToProtoDebugExecutionState(status.ExecutionState),
            PauseReason = ToProtoDebugPauseReason(status.PauseReason),
            PauseReasonDetail = status.PauseReasonDetail ?? string.Empty,
            CanPause = status.CanPause,
            CanResume = status.CanResume,
            BreakpointManagementSupported = status.BreakpointManagementSupported,
            BreakpointEvaluationSupported = status.BreakpointEvaluationSupported,
            SessionOwnership = ToProtoDebugSessionOwnership(status.SessionOwnership),
            ActiveSession = status.ActiveSession is null ? null : ToProtoDebugSessionInfo(status.ActiveSession),
            CallerRole = status.CallerRole is null ? DebugSessionRole.Unspecified : ToProtoDebugSessionRole(status.CallerRole.Value),
        }.Also(response =>
        {
            response.SupportedStepKinds.Add(status.SupportedStepKinds.Select(ToProtoDebugStepKind));
            response.Breakpoints.Add(status.Breakpoints.Select(ToProtoDebugBreakpoint));
            response.ObserverSessions.Add((status.ObserverSessions ?? []).Select(ToProtoDebugSessionInfo));
            if (status.LastBreakpointHit is not null)
            {
                response.LastBreakpointHit = ToProtoDebugBreakpointHit(status.LastBreakpointHit);
            }

            response.Notices.Add(status.Notices.Select(ToProtoDebugNotice));
        });
    }

    private static DebugNotice ToProtoDebugNotice(DebugNoticeSnapshot notice)
    {
        return new DebugNotice
        {
            Code = notice.Code,
            Message = notice.Message,
        };
    }

    private static DebugSessionInfo ToProtoDebugSessionInfo(DebugSessionInfoSnapshot session)
    {
        return new DebugSessionInfo
        {
            Id = session.Id,
            Name = session.Name ?? string.Empty,
            LeaseTimeoutMs = session.LeaseTimeoutMs,
            LeaseExpiresAtUnixMs = session.LeaseExpiresAtUnixMs,
            Role = ToProtoDebugSessionRole(session.Role),
        };
    }

    private static DebugEventStreamResponse ToProtoDebugEventStreamResponse(DebugEventStreamResultSnapshot result)
    {
        return new DebugEventStreamResponse
        {
            FromSequence = result.FromSequence,
            NextSequence = result.NextSequence,
            OldestRetainedSequence = result.Retention.OldestRetainedSequence,
            NewestSequence = result.Retention.NewestSequence,
            RetentionLimit = result.Retention.RetentionLimit,
            Expired = result.Expired,
            Overflow = result.Overflow,
        }.Also(response =>
        {
            response.Events.Add(result.Events.Select(ToProtoDebugEvent));
            response.Notices.Add(result.Notices.Select(ToProtoDebugNotice));
        });
    }

    private static DebugEvent ToProtoDebugEvent(DebugEventSnapshot evt)
    {
        return new DebugEvent
        {
            Sequence = evt.Sequence,
            UnixTimeMs = evt.UnixTimeMs,
            Kind = ToProtoDebugEventKind(evt.Kind),
            SessionId = evt.SessionId ?? string.Empty,
            SessionRole = evt.SessionRole is null ? DebugSessionRole.Unspecified : ToProtoDebugSessionRole(evt.SessionRole.Value),
        }.Also(response =>
        {
            response.Notices.Add(evt.Notices.Select(ToProtoDebugNotice));
            if (evt.StatusChanged is not null)
            {
                response.StatusChanged = new DebugEventStatusChanged
                {
                    PreviousExecutionState = ToProtoDebugExecutionState(evt.StatusChanged.PreviousExecutionState),
                    ExecutionState = ToProtoDebugExecutionState(evt.StatusChanged.ExecutionState),
                    PauseReason = ToProtoDebugPauseReason(evt.StatusChanged.PauseReason),
                    PauseReasonDetail = evt.StatusChanged.PauseReasonDetail ?? string.Empty,
                };
            }
            else if (evt.BreakpointHit is not null)
            {
                response.BreakpointHit = new DebugEventBreakpointHit { Hit = ToProtoDebugBreakpointHit(evt.BreakpointHit) };
            }
            else if (evt.Kind == DebugEventKindSnapshot.Paused)
            {
                response.Paused = new DebugEventPause { Detail = evt.PauseDetail ?? string.Empty };
            }
            else if (evt.Kind == DebugEventKindSnapshot.Resumed)
            {
                response.Resumed = new DebugEventResume { Detail = evt.ResumeDetail ?? string.Empty };
            }
            else if (evt.Step is not null)
            {
                response.Stepped = new DebugEventStep
                {
                    Kind = ToProtoDebugStepKind(evt.Step.Kind),
                    Count = evt.Step.Count,
                    Detail = evt.Step.Detail ?? string.Empty,
                };
            }
            else if (evt.LeaseChanged is not null)
            {
                response.LeaseChanged = ToProtoDebugEventLeaseChanged(evt.LeaseChanged);
            }
            else if (evt.SessionChanged is not null)
            {
                var changed = new DebugEventObserverChanged { Observer = ToProtoDebugSessionInfo(evt.SessionChanged.Session) };
                if (evt.Kind == DebugEventKindSnapshot.ObserverAttached)
                {
                    response.ObserverAttached = changed;
                }
                else if (evt.Kind == DebugEventKindSnapshot.ObserverDetached)
                {
                    response.ObserverDetached = changed;
                }
                else if (evt.Kind == DebugEventKindSnapshot.Disconnected)
                {
                    response.Disconnected = new DebugEventDisconnect
                    {
                        Session = ToProtoDebugSessionInfo(evt.SessionChanged.Session),
                        Reason = evt.SessionChanged.Reason ?? string.Empty,
                    };
                }
                else if (evt.Kind == DebugEventKindSnapshot.ClientDropped)
                {
                    response.ClientDropped = new DebugEventClientDropped
                    {
                        Session = ToProtoDebugSessionInfo(evt.SessionChanged.Session),
                        Reason = evt.SessionChanged.Reason ?? string.Empty,
                        DroppedEventCount = evt.SessionChanged.DroppedEventCount,
                    };
                }
            }
            else if (evt.StateChanged is not null)
            {
                response.StateChanged = new DebugEventStateChanged
                {
                    ScreenType = evt.StateChanged.ScreenType ?? string.Empty,
                    ScreenInstanceId = evt.StateChanged.ScreenInstanceId ?? string.Empty,
                    SelectedStateJson = evt.StateChanged.SelectedStateJson ?? string.Empty,
                };
                response.StateChanged.ChangedPaths.Add(evt.StateChanged.ChangedPaths);
            }
        });
    }

    private static DebugEventLeaseChanged ToProtoDebugEventLeaseChanged(DebugEventLeaseChangedSnapshot lease)
    {
        return new DebugEventLeaseChanged
        {
            PreviousController = lease.PreviousController is null ? null : ToProtoDebugSessionInfo(lease.PreviousController),
            Controller = lease.Controller is null ? null : ToProtoDebugSessionInfo(lease.Controller),
            Conflict = lease.Conflict is null ? null : new DebugLeaseConflict
            {
                ActiveController = ToProtoDebugSessionInfo(lease.Conflict.ActiveController),
                RequestedName = lease.Conflict.RequestedName ?? string.Empty,
                RequestedLeaseTimeoutMs = lease.Conflict.RequestedLeaseTimeoutMs,
            }.Also(conflict => conflict.Notices.Add(lease.Conflict.Notices.Select(ToProtoDebugNotice))),
        };
    }

    private static DebugBreakpoint ToProtoDebugBreakpoint(DebugBreakpointSnapshot breakpoint)
    {
        return new DebugBreakpoint
        {
            Id = breakpoint.Id,
            Name = breakpoint.Name ?? string.Empty,
            QueryPath = breakpoint.QueryPath,
            Predicate = breakpoint.Predicate is null ? null : ToProtoDebugPredicate(breakpoint.Predicate),
            Enabled = breakpoint.Enabled,
            Provisional = breakpoint.Provisional,
            Kind = ToProtoDebugBreakpointKind(breakpoint.Kind),
            MinHitCount = breakpoint.MinHitCount,
            HitCount = breakpoint.HitCount,
            AutoRemoveOnHit = breakpoint.AutoRemoveOnHit,
            LastObservedJson = breakpoint.LastObservedJson ?? string.Empty,
        };
    }

    private static DebugBreakpointHit ToProtoDebugBreakpointHit(DebugBreakpointHitSnapshot hit)
    {
        return new DebugBreakpointHit
        {
            BreakpointId = hit.BreakpointId,
            BreakpointName = hit.BreakpointName ?? string.Empty,
            QueryPath = hit.QueryPath,
            Predicate = hit.Predicate is null ? null : ToProtoDebugPredicate(hit.Predicate),
            ActualJson = hit.ActualJson ?? string.Empty,
            ScreenType = hit.ScreenType ?? string.Empty,
            ScreenInstanceId = hit.ScreenInstanceId ?? string.Empty,
            Kind = ToProtoDebugBreakpointKind(hit.Kind),
            HitCount = hit.HitCount,
        };
    }

    private static DebugPredicate ToProtoDebugPredicate(DebugPredicateSnapshot predicate)
    {
        return new DebugPredicate
        {
            Operator = predicate.Operator switch
            {
                DebugPredicateOperatorSnapshot.Contains => DebugPredicateOperator.Contains,
                DebugPredicateOperatorSnapshot.Regex => DebugPredicateOperator.Regex,
                DebugPredicateOperatorSnapshot.GreaterThan => DebugPredicateOperator.Gt,
                DebugPredicateOperatorSnapshot.GreaterThanOrEqual => DebugPredicateOperator.Gte,
                DebugPredicateOperatorSnapshot.LessThan => DebugPredicateOperator.Lt,
                DebugPredicateOperatorSnapshot.LessThanOrEqual => DebugPredicateOperator.Lte,
                DebugPredicateOperatorSnapshot.Exists => DebugPredicateOperator.Exists,
                DebugPredicateOperatorSnapshot.NotExists => DebugPredicateOperator.NotExists,
                _ => DebugPredicateOperator.Equals,
            },
            ExpectedJson = predicate.ExpectedJson ?? string.Empty,
        };
    }

    private static DebugPredicateSnapshot ToDomainDebugPredicate(DebugPredicate? predicate)
    {
        return new DebugPredicateSnapshot(
            predicate?.Operator switch
            {
                DebugPredicateOperator.Contains => DebugPredicateOperatorSnapshot.Contains,
                DebugPredicateOperator.Regex => DebugPredicateOperatorSnapshot.Regex,
                DebugPredicateOperator.Gt => DebugPredicateOperatorSnapshot.GreaterThan,
                DebugPredicateOperator.Gte => DebugPredicateOperatorSnapshot.GreaterThanOrEqual,
                DebugPredicateOperator.Lt => DebugPredicateOperatorSnapshot.LessThan,
                DebugPredicateOperator.Lte => DebugPredicateOperatorSnapshot.LessThanOrEqual,
                DebugPredicateOperator.Exists => DebugPredicateOperatorSnapshot.Exists,
                DebugPredicateOperator.NotExists => DebugPredicateOperatorSnapshot.NotExists,
                _ => DebugPredicateOperatorSnapshot.Equals,
            },
            string.IsNullOrWhiteSpace(predicate?.ExpectedJson) ? null : predicate.ExpectedJson);
    }

    private static DebugBreakpointKind ToProtoDebugBreakpointKind(DebugBreakpointKindSnapshot kind)
    {
        return kind switch
        {
            DebugBreakpointKindSnapshot.Change => DebugBreakpointKind.Change,
            _ => DebugBreakpointKind.Match,
        };
    }

    private static DebugBreakpointKindSnapshot ToDomainDebugBreakpointKind(DebugBreakpointKind kind)
    {
        return kind switch
        {
            DebugBreakpointKind.Change => DebugBreakpointKindSnapshot.Change,
            _ => DebugBreakpointKindSnapshot.Match,
        };
    }

    private static DebugSessionOwnership ToProtoDebugSessionOwnership(DebugSessionOwnershipSnapshot ownership)
    {
        return ownership switch
        {
            DebugSessionOwnershipSnapshot.OwnedByCaller => DebugSessionOwnership.OwnedByCaller,
            DebugSessionOwnershipSnapshot.LeasedElsewhere => DebugSessionOwnership.LeasedElsewhere,
            _ => DebugSessionOwnership.Unowned,
        };
    }

    private static DebugSessionRole ToProtoDebugSessionRole(DebugSessionRoleSnapshot role)
    {
        return role switch
        {
            DebugSessionRoleSnapshot.Observer => DebugSessionRole.Observer,
            _ => DebugSessionRole.Controller,
        };
    }

    private static DebugSessionRoleSnapshot ToDomainDebugSessionRole(DebugSessionRole role)
    {
        return role switch
        {
            DebugSessionRole.Observer => DebugSessionRoleSnapshot.Observer,
            _ => DebugSessionRoleSnapshot.Controller,
        };
    }

    private static DebugEventKind ToProtoDebugEventKind(DebugEventKindSnapshot kind)
    {
        return kind switch
        {
            DebugEventKindSnapshot.BreakpointHit => DebugEventKind.BreakpointHit,
            DebugEventKindSnapshot.Paused => DebugEventKind.Paused,
            DebugEventKindSnapshot.Resumed => DebugEventKind.Resumed,
            DebugEventKindSnapshot.Stepped => DebugEventKind.Stepped,
            DebugEventKindSnapshot.LeaseChanged => DebugEventKind.LeaseChanged,
            DebugEventKindSnapshot.ObserverAttached => DebugEventKind.ObserverAttached,
            DebugEventKindSnapshot.ObserverDetached => DebugEventKind.ObserverDetached,
            DebugEventKindSnapshot.Disconnected => DebugEventKind.Disconnected,
            DebugEventKindSnapshot.ClientDropped => DebugEventKind.ClientDropped,
            DebugEventKindSnapshot.StateChanged => DebugEventKind.StateChanged,
            _ => DebugEventKind.StatusChanged,
        };
    }

    private static DebugExecutionState ToProtoDebugExecutionState(DebugExecutionStateSnapshot state)
    {
        return state switch
        {
            DebugExecutionStateSnapshot.Running => DebugExecutionState.Running,
            DebugExecutionStateSnapshot.Paused => DebugExecutionState.Paused,
            _ => DebugExecutionState.Unsupported,
        };
    }

    private static DebugPauseReason ToProtoDebugPauseReason(DebugPauseReasonSnapshot reason)
    {
        return reason switch
        {
            DebugPauseReasonSnapshot.None => DebugPauseReason.None,
            DebugPauseReasonSnapshot.Manual => DebugPauseReason.Manual,
            DebugPauseReasonSnapshot.Breakpoint => DebugPauseReason.Breakpoint,
            DebugPauseReasonSnapshot.StepComplete => DebugPauseReason.StepComplete,
            DebugPauseReasonSnapshot.BridgeError => DebugPauseReason.BridgeError,
            _ => DebugPauseReason.Unsupported,
        };
    }

    private static DebugStepKind ToProtoDebugStepKind(DebugStepKindSnapshot kind)
    {
        return kind switch
        {
            DebugStepKindSnapshot.Action => DebugStepKind.Action,
            _ => DebugStepKind.Frame,
        };
    }

    private static DebugStepKindSnapshot ToDomainDebugStepKind(DebugStepKind kind)
    {
        return kind switch
        {
            DebugStepKind.Action => DebugStepKindSnapshot.Action,
            _ => DebugStepKindSnapshot.Frame,
        };
    }

    private static LogEntry ToProtoLogEntry(LogRecord record)
    {
        return new LogEntry
        {
            Cursor = record.Cursor,
            Level = record.Level switch
            {
                BridgeLogLevel.Trace => LogLevel.Trace,
                BridgeLogLevel.Debug => LogLevel.Debug,
                BridgeLogLevel.Info => LogLevel.Info,
                BridgeLogLevel.Warn => LogLevel.Warn,
                BridgeLogLevel.Error => LogLevel.Error,
                _ => LogLevel.Unspecified,
            },
            Target = record.Target,
            Message = record.Message,
        };
    }

    private static BridgeLogLevel ToDomainLogLevel(LogLevel level)
    {
        return level switch
        {
            LogLevel.Trace => BridgeLogLevel.Trace,
            LogLevel.Debug => BridgeLogLevel.Debug,
            LogLevel.Info => BridgeLogLevel.Info,
            LogLevel.Warn => BridgeLogLevel.Warn,
            LogLevel.Error => BridgeLogLevel.Error,
            _ => BridgeLogLevel.Info,
        };
    }

    private static TransportKind ToProtoTransportKind(string transportKind)
    {
        return transportKind.ToLowerInvariant() switch
        {
            "mock" => TransportKind.Mock,
            "ipc" => TransportKind.Ipc,
            "tcp" => TransportKind.Tcp,
            "embedded" => TransportKind.Embedded,
            _ => TransportKind.Unspecified,
        };
    }

    private static AttachmentState ToProtoAttachmentState(RuntimeAttachmentState attachmentState)
    {
        return attachmentState switch
        {
            RuntimeAttachmentState.Stubbed => AttachmentState.Stubbed,
            RuntimeAttachmentState.Detached => AttachmentState.Detached,
            RuntimeAttachmentState.Attached => AttachmentState.Attached,
            _ => AttachmentState.Unspecified,
        };
    }

    private static DataSource ToProtoDataSource(DataSourceKind source)
    {
        return source switch
        {
            DataSourceKind.Stub => DataSource.Stub,
            DataSourceKind.Live => DataSource.Live,
            _ => DataSource.Unspecified,
        };
    }

    private static ActionStatus ToProtoActionStatus(ActionImplementationStatus status)
    {
        return status switch
        {
            ActionImplementationStatus.Implemented => ActionStatus.Implemented,
            ActionImplementationStatus.Scaffolded => ActionStatus.Scaffolded,
            _ => ActionStatus.Unspecified,
        };
    }

    private static ErrorActionFailureReasonCode ToProtoErrorActionFailureReasonCode(ActionFailureCode code)
    {
        return code switch
        {
            ActionFailureCode.NotImplemented => ErrorActionFailureReasonCode.NotImplemented,
            ActionFailureCode.InvalidAction => ErrorActionFailureReasonCode.InvalidAction,
            ActionFailureCode.BridgeNotAttached => ErrorActionFailureReasonCode.BridgeNotAttached,
            ActionFailureCode.RuntimeFailure => ErrorActionFailureReasonCode.RuntimeFailure,
            ActionFailureCode.WrongScreen => ErrorActionFailureReasonCode.WrongScreen,
            ActionFailureCode.WrongPlayer => ErrorActionFailureReasonCode.WrongPlayer,
            ActionFailureCode.NotVisible => ErrorActionFailureReasonCode.NotVisible,
            ActionFailureCode.NotEnabled => ErrorActionFailureReasonCode.NotEnabled,
            ActionFailureCode.MissingHook => ErrorActionFailureReasonCode.MissingHook,
            ActionFailureCode.AmbiguousHook => ErrorActionFailureReasonCode.AmbiguousHook,
            ActionFailureCode.StaleId => ErrorActionFailureReasonCode.StaleId,
            ActionFailureCode.UnsupportedPerspective => ErrorActionFailureReasonCode.UnsupportedPerspective,
            ActionFailureCode.DangerousModeRequired => ErrorActionFailureReasonCode.DangerousModeRequired,
            _ => ErrorActionFailureReasonCode.Unspecified,
        };
    }

    private static FieldDiagnostic ToProtoFieldDiagnostic(ActionFailureFieldDiagnostic diagnostic)
    {
        return new FieldDiagnostic
        {
            Field = diagnostic.Field,
            Value = diagnostic.Value,
            Note = diagnostic.Note,
        };
    }

    private static RemoteClientOrchestrationCapability ToProtoRemoteOrchestration(RemoteClientOrchestrationCapabilitySnapshot capability)
    {
        return new RemoteClientOrchestrationCapability
        {
            Id = capability.Id,
            State = capability.State switch
            {
                RemoteClientOrchestrationStateSnapshot.Unavailable => RemoteClientOrchestrationState.Unavailable,
                RemoteClientOrchestrationStateSnapshot.LocalOnlyDegraded => RemoteClientOrchestrationState.LocalOnlyDegraded,
                RemoteClientOrchestrationStateSnapshot.HostMediated => RemoteClientOrchestrationState.HostMediated,
                RemoteClientOrchestrationStateSnapshot.ConfiguredClient => RemoteClientOrchestrationState.ConfiguredClient,
                RemoteClientOrchestrationStateSnapshot.Unsupported => RemoteClientOrchestrationState.Unsupported,
                _ => RemoteClientOrchestrationState.Unspecified,
            },
            Summary = capability.Summary,
            Provisional = capability.Provisional,
        };
    }

    private static MultiplayerRole ToProtoMultiplayerRole(MultiplayerRoleSnapshot role)
    {
        return role switch
        {
            MultiplayerRoleSnapshot.Local => MultiplayerRole.Local,
            MultiplayerRoleSnapshot.Host => MultiplayerRole.Host,
            MultiplayerRoleSnapshot.Remote => MultiplayerRole.Remote,
            MultiplayerRoleSnapshot.HostLocalSeat => MultiplayerRole.HostLocalSeat,
            _ => MultiplayerRole.Unspecified,
        };
    }

    private static Spirectl.Proto.V0.ActionKind ToProtoActionKind(SemanticActionKind kind)
    {
        return kind switch
        {
            SemanticActionKind.PlayCard => Spirectl.Proto.V0.ActionKind.PlayCard,
            SemanticActionKind.Choose => Spirectl.Proto.V0.ActionKind.Choose,
            SemanticActionKind.ConfirmSelection => Spirectl.Proto.V0.ActionKind.ConfirmSelection,
            SemanticActionKind.CancelSelection => Spirectl.Proto.V0.ActionKind.CancelSelection,
            SemanticActionKind.SelectMapNode => Spirectl.Proto.V0.ActionKind.SelectMapNode,
            SemanticActionKind.EndTurn => Spirectl.Proto.V0.ActionKind.EndTurn,
            SemanticActionKind.CancelEndTurn => Spirectl.Proto.V0.ActionKind.CancelEndTurn,
            SemanticActionKind.UsePotion => Spirectl.Proto.V0.ActionKind.UsePotion,
            SemanticActionKind.Ready => Spirectl.Proto.V0.ActionKind.Ready,
            SemanticActionKind.Unready => Spirectl.Proto.V0.ActionKind.Unready,
            SemanticActionKind.SelectCharacter => Spirectl.Proto.V0.ActionKind.SelectCharacter,
            SemanticActionKind.MouseClick => Spirectl.Proto.V0.ActionKind.MouseClick,
            SemanticActionKind.ClaimReward => Spirectl.Proto.V0.ActionKind.ClaimReward,
            SemanticActionKind.SkipRewards => Spirectl.Proto.V0.ActionKind.SkipRewards,
            SemanticActionKind.SelectCard => Spirectl.Proto.V0.ActionKind.SelectCard,
            SemanticActionKind.SkipCardSelection => Spirectl.Proto.V0.ActionKind.SkipCardSelection,
            SemanticActionKind.SelectBundle => Spirectl.Proto.V0.ActionKind.SelectBundle,
            SemanticActionKind.BuyCard => Spirectl.Proto.V0.ActionKind.BuyCard,
            SemanticActionKind.BuyRelic => Spirectl.Proto.V0.ActionKind.BuyRelic,
            SemanticActionKind.BuyPotion => Spirectl.Proto.V0.ActionKind.BuyPotion,
            SemanticActionKind.RemoveCard => Spirectl.Proto.V0.ActionKind.RemoveCard,
            SemanticActionKind.LeaveShop => Spirectl.Proto.V0.ActionKind.LeaveShop,
            SemanticActionKind.CloseShopInventory => Spirectl.Proto.V0.ActionKind.CloseShopInventory,
            SemanticActionKind.Rest => Spirectl.Proto.V0.ActionKind.Rest,
            SemanticActionKind.Smith => Spirectl.Proto.V0.ActionKind.Smith,
            SemanticActionKind.UseRestSiteOption => Spirectl.Proto.V0.ActionKind.UseRestSiteOption,
            SemanticActionKind.ProceedRestSite => Spirectl.Proto.V0.ActionKind.ProceedRestSite,
            SemanticActionKind.OpenChest => Spirectl.Proto.V0.ActionKind.OpenChest,
            SemanticActionKind.TakeRelic => Spirectl.Proto.V0.ActionKind.TakeRelic,
            SemanticActionKind.ProceedTreasureRoom => Spirectl.Proto.V0.ActionKind.ProceedTreasureRoom,
            SemanticActionKind.BackFromMap => Spirectl.Proto.V0.ActionKind.BackFromMap,
            SemanticActionKind.SelectEventOption => Spirectl.Proto.V0.ActionKind.SelectEventOption,
            SemanticActionKind.OpenEventShop => Spirectl.Proto.V0.ActionKind.OpenEventShop,
            SemanticActionKind.UseCrystalSphereControl => Spirectl.Proto.V0.ActionKind.UseCrystalSphereControl,
            SemanticActionKind.ProceedEvent => Spirectl.Proto.V0.ActionKind.ProceedEvent,
            SemanticActionKind.JoinLobbyPlayer => Spirectl.Proto.V0.ActionKind.JoinLobbyPlayer,
            SemanticActionKind.LeaveLobbyPlayer => Spirectl.Proto.V0.ActionKind.LeaveLobbyPlayer,
            SemanticActionKind.OpenPotionPopup => Spirectl.Proto.V0.ActionKind.OpenPotionPopup,
            SemanticActionKind.StartPotionTargeting => Spirectl.Proto.V0.ActionKind.StartPotionTargeting,
            SemanticActionKind.SelectTarget => Spirectl.Proto.V0.ActionKind.SelectTarget,
            SemanticActionKind.DiscardPotion => Spirectl.Proto.V0.ActionKind.DiscardPotion,
            SemanticActionKind.ViewDrawPile => Spirectl.Proto.V0.ActionKind.ViewDrawPile,
            SemanticActionKind.ViewDiscardPile => Spirectl.Proto.V0.ActionKind.ViewDiscardPile,
            SemanticActionKind.ViewExhaustPile => Spirectl.Proto.V0.ActionKind.ViewExhaustPile,
            SemanticActionKind.InspectRelic => Spirectl.Proto.V0.ActionKind.InspectRelic,
            SemanticActionKind.CloseInspectRelic => Spirectl.Proto.V0.ActionKind.CloseInspectRelic,
            SemanticActionKind.SelectHandCard => Spirectl.Proto.V0.ActionKind.SelectHandCard,
            SemanticActionKind.DeselectHandCard => Spirectl.Proto.V0.ActionKind.DeselectHandCard,
            SemanticActionKind.ConfirmHandSelection => Spirectl.Proto.V0.ActionKind.ConfirmHandSelection,
            _ => Spirectl.Proto.V0.ActionKind.Unspecified,
        };
    }

    private static RawMouseButtonKind ToDomainMouseButton(RawMouseButton button)
    {
        return button switch
        {
            RawMouseButton.Right => RawMouseButtonKind.Right,
            RawMouseButton.Middle => RawMouseButtonKind.Middle,
            _ => RawMouseButtonKind.Left,
        };
    }
}
