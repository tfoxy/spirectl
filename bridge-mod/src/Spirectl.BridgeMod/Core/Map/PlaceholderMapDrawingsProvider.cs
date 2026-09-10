namespace Spirectl.Sts2.Core.Map;


/// Non-live default. Reports map drawings as unavailable via the interface's default
/// implementation; the live provider (Sts2MapDrawingsProvider) replaces it under the live host.
public sealed class PlaceholderMapDrawingsProvider : IMapDrawingsProvider;
