namespace Spirectl.Sts2.Core.Combat;

/// <summary>Game-truth values shared by the live state projection and the optional bridge endpoint.</summary>
public sealed record CardPreviewSnapshot(
    int Block,
    int SelfDamage,
    IReadOnlyDictionary<string, int> DamageByTarget);
