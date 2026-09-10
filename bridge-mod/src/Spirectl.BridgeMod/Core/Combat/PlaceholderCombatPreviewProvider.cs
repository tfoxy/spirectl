namespace Spirectl.Sts2.Core.Combat;


/// Non-live default. Reports combat preview as unavailable via the interface's default
/// implementation; the live oracle (Sts2CombatPreviewProvider) replaces it under the live host.
public sealed class PlaceholderCombatPreviewProvider : ICombatPreviewProvider;
