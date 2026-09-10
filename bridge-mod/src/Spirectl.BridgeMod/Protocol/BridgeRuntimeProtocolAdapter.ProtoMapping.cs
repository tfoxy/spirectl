using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Mods;
using Spirectl.Sts2.Core.Models;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.State;
using Spirectl.Proto.V0;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using DomainModelCatalogStatus = Spirectl.Sts2.Core.Models.ModelCatalogStatus;
using CurrentStateSubscriptionRequest = Spirectl.Sts2.Embedding.CurrentStateSubscriptionRequest;
using CurrentStateWatchEvent = Spirectl.Sts2.Embedding.CurrentStateWatchEvent;
using CurrentStateWatchEventType = Spirectl.Sts2.Embedding.CurrentStateWatchEventType;
using CombatEventSubscriptionRequest = Spirectl.Sts2.Embedding.CombatEventSubscriptionRequest;
using CombatWatchEvent = Spirectl.Sts2.Embedding.CombatWatchEvent;
using CombatWatchEventType = Spirectl.Sts2.Embedding.CombatWatchEventType;
using EmbeddableRuntimeError = Spirectl.Sts2.Embedding.EmbeddableRuntimeError;
using SpirectlRuntimeFacade = Spirectl.Sts2.Embedding.SpirectlRuntimeFacade;

namespace Spirectl.Sts2.Core.Protocol;

public sealed partial class BridgeRuntimeProtocolAdapter
{
    private static ActionResult InvalidAction(string field, string value, string note)
    {
        var failure = new Spirectl.Proto.V0.ActionFailureDetail
        {
            ReasonCode = ErrorActionFailureReasonCode.InvalidAction,
            FieldDiagnostics =
            {
                new FieldDiagnostic
                {
                    Field = field,
                    Value = value,
                    Note = note,
                },
            },
        };
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
                        ActionReasonCode = ErrorActionFailureReasonCode.InvalidAction,
                        FieldDiagnostics =
                        {
                            new FieldDiagnostic
                            {
                                Field = field,
                                Value = value,
                                Note = note,
                            },
                        },
                    },
                },
                ActionFailure = failure,
            },
        };
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

    private static BridgeError ToProtoBridgeError(ModListFailureSnapshot error)
        => new()
        {
            Code = error.Code == "mods_unavailable"
                ? BridgeErrorCode.NotImplemented
                : BridgeErrorCode.RuntimeFailure,
            Message = error.Message,
            Details =
            {
                new ErrorDetail
                {
                    Field = "mods",
                    Value = error.Code,
                    Note = error.Message,
                },
            },
        };

    private static BridgeError ToProtoBridgeError(AssetExtractFailure error, IReadOnlyList<string>? notes = null)
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
        protoError.Details.Add((notes ?? [])
            .Where(note => !string.IsNullOrWhiteSpace(note))
            .Select(note => new ErrorDetail
            {
                Field = "note",
                Value = string.Empty,
                Note = note,
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

    private static PerspectiveSelection ToPerspectiveSelection(PerspectiveSelector selector)
    {
        return new PerspectiveSelection(
            selector.Scope == PerspectiveScope.Omniscient ? PlayerScope.Omniscient : PlayerScope.Local,
            string.IsNullOrWhiteSpace(selector.PlayerId) ? null : selector.PlayerId);
    }

    private static CurrentStateSubscriptionRequest ToCurrentStateSubscriptionRequest(StateWatchRequest request)
    {
        var state = request.State ?? new StateRequest();
        return new CurrentStateSubscriptionRequest(
            StateTimeout: request.StateTimeoutMs > 0 ? TimeSpan.FromMilliseconds(request.StateTimeoutMs) : null,
            EmitInitial: true,
            MinCaptureInterval: TimeSpan.FromMilliseconds(request.MinCaptureIntervalMs),
            BufferCapacity: request.BufferCapacity <= 0 ? 16 : request.BufferCapacity,
            Perspective: state.Perspective is null ? null : ToPerspectiveSelection(state.Perspective));
    }

    private StateWatchEvent ToProtoStateWatchEvent(CurrentStateWatchEvent evt)
    {
        var response = new StateWatchEvent
        {
            Type = evt.Type switch
            {
                CurrentStateWatchEventType.Initial => StateWatchEventType.Initial,
                CurrentStateWatchEventType.Changed => StateWatchEventType.Changed,
                CurrentStateWatchEventType.Error => StateWatchEventType.Error,
                _ => StateWatchEventType.Unspecified,
            },
            Sequence = evt.Sequence,
            ObservedAtUtc = evt.ObservedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            Fingerprint = evt.SemanticFingerprint ?? string.Empty,
            SemanticRevision = evt.SemanticRevision ?? 0,
        };

        if (evt.State is not null)
        {
            response.State = ToProtoStateResponse(evt.State);
        }
        else if (evt.Error is not null)
        {
            response.Error = ToProtoBridgeError(evt.Error);
        }

        return response;
    }

    private static CombatEventSubscriptionRequest ToCombatEventSubscriptionRequest(WatchCombatEventsRequest request)
        => new(
            SinceSequence: request.SinceSequence,
            MaxEvents: request.MaxEvents,
            BufferCapacity: request.BufferCapacity <= 0 ? 256 : request.BufferCapacity);

    private CombatEvent ToProtoCombatEvent(CombatWatchEvent evt)
    {
        var response = new CombatEvent
        {
            Type = evt.Type switch
            {
                CombatWatchEventType.Damage => CombatEventType.Damage,
                CombatWatchEventType.Error => CombatEventType.Error,
                CombatWatchEventType.CardUpgrade => CombatEventType.CardUpgrade,
                CombatWatchEventType.Vfx => CombatEventType.Vfx,
                _ => CombatEventType.Unspecified,
            },
            Sequence = evt.Sequence,
            ObservedAtUtc = evt.ObservedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
        };

        if (evt.Damage is { } damage)
        {
            response.Damage = new CombatDamageEvent
            {
                TargetCreatureId = damage.TargetCreatureId,
                Amount = damage.Amount,
                DealerCreatureId = damage.DealerCreatureId ?? string.Empty,
                SourceCardModelId = damage.SourceCardModelId ?? string.Empty,
            };
        }
        else if (evt.CardUpgrade is { } upgrade)
        {
            response.CardUpgrade = new CombatCardUpgradeEvent
            {
                CardId = upgrade.CardId,
                CardModelId = upgrade.CardModelId,
                SourceRelicModelId = upgrade.SourceRelicModelId ?? string.Empty,
            };
        }
        else if (evt.Vfx is { } vfx)
        {
            response.Vfx = new CombatVfxEvent
            {
                ScenePath = vfx.ScenePath,
                AnchorCreatureId = vfx.AnchorCreatureId ?? string.Empty,
                Amount = vfx.Amount,
            };
        }
        else if (evt.Error is not null)
        {
            response.Error = ToProtoBridgeError(evt.Error);
        }

        return response;
    }

    private static BridgeError ToProtoBridgeError(EmbeddableRuntimeError error)
        => new()
        {
            Code = BridgeErrorCode.RuntimeFailure,
            Message = error.Message,
            Details =
            {
                new ErrorDetail
                {
                    Field = error.Field ?? error.Code,
                    Value = error.Value ?? string.Empty,
                    Note = error.SuggestedNextStep ?? string.Empty,
                },
            },
        };

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
            Stability = notice.Stability ?? string.Empty,
            Perspective = notice.Perspective ?? string.Empty,
        };
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
            LocalPlayerId = perspective.PlayerId ?? string.Empty,
            RemoteOrchestration = ToProtoRemoteOrchestration(DefaultRemoteOrchestration()),
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
    private static ActionDescriptorV0 ToProtoActionDescriptorV0(ActionDescriptorSnapshot action)
    {
        var response = new ActionDescriptorV0
        {
            Id = action.Id,
            Kind = ToProtoActionKind(action.Kind),
            Summary = action.Summary,
            CliCommandHint = action.CliCommandHint,
            Provisional = action.Provisional,
            Status = ToProtoActionStatus(action.Status),
            OwnerPlayerId = action.OwnerPlayerId ?? string.Empty,
            PerspectiveBehavior = ToProtoActionPerspectiveBehavior(action.PerspectiveBehavior),
            LegalityStatus = ToProtoActionLegalityStatus(action.LegalityStatus),
            RemoteOrchestration = action.RemoteOrchestration is null ? null : ToProtoRemoteOrchestration(action.RemoteOrchestration),
            OwnerRole = ToProtoMultiplayerRole(action.OwnerRole),
        };
        if (action.KindDescriptor is not null)
        {
            response.KindDescriptor = ToProtoActionKindDescriptor(action.KindDescriptor);
        }

        response.CheckedHookPaths.Add(action.CheckedHookPaths ?? []);
        response.ArgumentSchema.Add((action.ArgumentSchema ?? []).Select(ToProtoActionArgumentSchema));
        response.FailureReasonCodes.Add((action.FailureReasonCodes ?? []).Select(ToProtoActionFailureReasonCode));
        return response;
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
                RemoteClientOrchestrationStateSnapshot.HostLocalSeat => RemoteClientOrchestrationState.HostLocalSeat,
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
    private static RemoteClientOrchestrationCapabilitySnapshot DefaultRemoteOrchestration()
    {
        return new RemoteClientOrchestrationCapabilitySnapshot(
            "local-only-degraded",
            RemoteClientOrchestrationStateSnapshot.LocalOnlyDegraded,
            "This local bridge can execute local-player actions only; independent remote clients require explicitly configured client bridges.",
            Provisional: false);
    }
    private static ActionKindDescriptor ToProtoActionKindDescriptor(ActionKindDescriptorSnapshot descriptor)
    {
        var response = new ActionKindDescriptor
        {
            Kind = ToProtoActionKind(descriptor.Kind),
            IntentKind = descriptor.IntentKind ?? string.Empty,
            CommandName = descriptor.CommandName ?? string.Empty,
            Summary = descriptor.Summary ?? string.Empty,
            Fallback = descriptor.Fallback,
            Dangerous = descriptor.Dangerous,
        };
        response.Modes.Add(descriptor.Modes ?? []);
        response.ScreenTypes.Add(descriptor.ScreenTypes ?? []);
        return response;
    }
    private static ActionArgumentSchema ToProtoActionArgumentSchema(ActionArgumentSchemaSnapshot schema)
    {
        var response = new ActionArgumentSchema
        {
            Name = schema.Name,
            ValueType = ToProtoActionArgumentValueType(schema.ValueType),
            Required = schema.Required,
            Summary = schema.Summary,
            StableId = schema.StableId,
        };
        response.AllowedValues.Add(schema.AllowedValues ?? []);
        return response;
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
    private static ActionFailureReasonCode ToProtoActionFailureReasonCode(ActionFailureCode code)
    {
        return code switch
        {
            ActionFailureCode.NotImplemented => ActionFailureReasonCode.NotImplemented,
            ActionFailureCode.InvalidAction => ActionFailureReasonCode.InvalidAction,
            ActionFailureCode.BridgeNotAttached => ActionFailureReasonCode.BridgeNotAttached,
            ActionFailureCode.RuntimeFailure => ActionFailureReasonCode.RuntimeFailure,
            ActionFailureCode.WrongScreen => ActionFailureReasonCode.WrongScreen,
            ActionFailureCode.WrongPlayer => ActionFailureReasonCode.WrongPlayer,
            ActionFailureCode.NotVisible => ActionFailureReasonCode.NotVisible,
            ActionFailureCode.NotEnabled => ActionFailureReasonCode.NotEnabled,
            ActionFailureCode.MissingHook => ActionFailureReasonCode.MissingHook,
            ActionFailureCode.AmbiguousHook => ActionFailureReasonCode.AmbiguousHook,
            ActionFailureCode.StaleId => ActionFailureReasonCode.StaleId,
            ActionFailureCode.UnsupportedPerspective => ActionFailureReasonCode.UnsupportedPerspective,
            ActionFailureCode.DangerousModeRequired => ActionFailureReasonCode.DangerousModeRequired,
            _ => ActionFailureReasonCode.Unspecified,
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
    private static ActionArgumentValueType ToProtoActionArgumentValueType(ActionArgumentValueKind kind)
    {
        return kind switch
        {
            ActionArgumentValueKind.String => ActionArgumentValueType.String,
            ActionArgumentValueKind.StableId => ActionArgumentValueType.StableId,
            ActionArgumentValueKind.PlayerId => ActionArgumentValueType.PlayerId,
            ActionArgumentValueKind.CardId => ActionArgumentValueType.CardId,
            ActionArgumentValueKind.PotionId => ActionArgumentValueType.PotionId,
            ActionArgumentValueKind.TargetId => ActionArgumentValueType.TargetId,
            ActionArgumentValueKind.ChoiceId => ActionArgumentValueType.ChoiceId,
            ActionArgumentValueKind.MapNodeId => ActionArgumentValueType.MapNodeId,
            ActionArgumentValueKind.CharacterId => ActionArgumentValueType.CharacterId,
            ActionArgumentValueKind.RewardId => ActionArgumentValueType.StableId,
            ActionArgumentValueKind.BundleId => ActionArgumentValueType.StableId,
            ActionArgumentValueKind.ShopItemId => ActionArgumentValueType.StableId,
            ActionArgumentValueKind.RelicId => ActionArgumentValueType.StableId,
            ActionArgumentValueKind.RestOptionId => ActionArgumentValueType.StableId,
            ActionArgumentValueKind.EventOptionId => ActionArgumentValueType.StableId,
            ActionArgumentValueKind.ControlId => ActionArgumentValueType.StableId,
            ActionArgumentValueKind.Int32 => ActionArgumentValueType.Int32,
            ActionArgumentValueKind.Enum => ActionArgumentValueType.Enum,
            ActionArgumentValueKind.Bool => ActionArgumentValueType.Bool,
            _ => ActionArgumentValueType.Unspecified,
        };
    }
    private static ActionPerspectiveBehavior ToProtoActionPerspectiveBehavior(ActionPerspectiveBehaviorKind behavior)
    {
        return behavior switch
        {
            ActionPerspectiveBehaviorKind.LocalOnly => ActionPerspectiveBehavior.LocalOnly,
            ActionPerspectiveBehaviorKind.OwnerOnly => ActionPerspectiveBehavior.OwnerOnly,
            ActionPerspectiveBehaviorKind.OmniscientAllowed => ActionPerspectiveBehavior.OmniscientAllowed,
            ActionPerspectiveBehaviorKind.Shared => ActionPerspectiveBehavior.Shared,
            ActionPerspectiveBehaviorKind.DangerousViewport => ActionPerspectiveBehavior.DangerousViewport,
            _ => ActionPerspectiveBehavior.Unspecified,
        };
    }
    private static ActionLegalityStatus ToProtoActionLegalityStatus(ActionLegalityKind status)
    {
        return status switch
        {
            ActionLegalityKind.Legal => ActionLegalityStatus.Legal,
            ActionLegalityKind.Illegal => ActionLegalityStatus.Illegal,
            ActionLegalityKind.Provisional => ActionLegalityStatus.Provisional,
            ActionLegalityKind.Unknown => ActionLegalityStatus.Unknown,
            _ => ActionLegalityStatus.Unspecified,
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
            SemanticActionKind.ToggleMap => Spirectl.Proto.V0.ActionKind.ToggleMap,
            SemanticActionKind.ToggleDeck => Spirectl.Proto.V0.ActionKind.ToggleDeck,
            SemanticActionKind.ToggleSettings => Spirectl.Proto.V0.ActionKind.ToggleSettings,
            SemanticActionKind.SortDeckView => Spirectl.Proto.V0.ActionKind.SortDeckView,
            SemanticActionKind.ToggleDeckViewUpgrades => Spirectl.Proto.V0.ActionKind.ToggleDeckViewUpgrades,
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
            SemanticActionKind.DrawMapStroke => Spirectl.Proto.V0.ActionKind.DrawMapStroke,
            SemanticActionKind.ClearMapDrawings => Spirectl.Proto.V0.ActionKind.ClearMapDrawings,
            SemanticActionKind.Heal => Spirectl.Proto.V0.ActionKind.Heal,
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
