namespace Spirectl.Sts2.Core.State;

public sealed record StateBlockingModalSnapshot(
    string Id, string Kind, string TypeName, string? Title, string? Body,
    IReadOnlyList<StateModalButtonSnapshot> Buttons);
public sealed record StateModalButtonSnapshot(string Id, string Label);
