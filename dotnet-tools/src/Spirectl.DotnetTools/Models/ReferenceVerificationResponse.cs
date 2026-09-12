namespace Spirectl.DotnetTools.Models;

internal sealed record ReferenceVerificationResponse(
    string Command,
    string Status,
    IReadOnlyList<string> Notes,
    IReadOnlyList<ReferenceVerificationConsumer> Consumers,
    IReadOnlyList<string> GameAssemblies,
    string AssembliesDir,
    string? ControlAssembliesDir,
    int TypeReferenceCount,
    int MemberReferenceCount,
    int BreakCount,
    IReadOnlyList<ReferenceMissingType> MissingTypes,
    IReadOnlyList<ReferenceMissingMember> MissingMembersWithMissingOwner,
    IReadOnlyList<ReferenceMissingMember> MissingMembers,
    IReadOnlyList<ReferenceChangedSignature> ChangedSignatures,
    ReferenceVerificationControl? Control);

internal sealed record ReferenceVerificationConsumer(string Assembly, string Path);

internal sealed record ReferenceVerificationControl(
    string Status,
    int UnresolvedCount,
    IReadOnlyList<ReferenceMissingType> MissingTypes,
    IReadOnlyList<ReferenceMissingMember> MissingMembersWithMissingOwner,
    IReadOnlyList<ReferenceMissingMember> MissingMembers);

internal sealed record ReferenceMissingType(
    string Assembly,
    string Type,
    IReadOnlyList<string> Consumers);

internal sealed record ReferenceMissingMember(
    string Assembly,
    string Type,
    string Member,
    string MemberKind,
    int? ParameterCount,
    IReadOnlyList<string> Consumers);

internal sealed record ReferenceChangedSignature(
    string Assembly,
    string Type,
    string Member,
    IReadOnlyList<string> Consumers,
    string ControlSignature,
    string CandidateSignature);
