using System;
using System.Linq;
using System.Reflection;
using Spirectl.Sts2;
using Spirectl.Sts2.Embedding;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// EmbeddableCapabilityIds vs what the facade actually publishes.
//
// A capability id is a contract in both directions: an embedder guards on it BEFORE calling the API behind it,
// and a guard that spells the id even slightly differently reads as "no such capability" and refuses every call
// — silently, for ever. An embedder shipped exactly that, carrying a fallback for `spine-geoclip-bake` on the
// belief it was unpublished, and nothing type-checked either side.
//
// The constants only close that gap while they MATCH. So this reads the published list from a real facade rather
// than from a second hand-written list: a capability added to the facade without a constant, a constant left
// behind by a removed capability, and a renamed id all fail here.
public sealed class EmbeddableCapabilityIdsTests
{
    // Every published id has a constant, and every constant is published. Both directions, because each catches
    // a different mistake: the first catches a new capability nobody named, the second catches a constant that
    // now names nothing.
    [Fact]
    public void EveryPublishedCapabilityHasAConstantAndViceVersa()
    {
        var runtime = EmbeddedRuntimeTestFactory.Create();

        var published = runtime.GetCapabilities().Capabilities.Select(capability => capability.Id).ToArray();

        Assert.Equal(published.Length, published.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(published.Except(EmbeddableCapabilityIds.All, StringComparer.Ordinal));
        Assert.Empty(EmbeddableCapabilityIds.All.Except(published, StringComparer.Ordinal));
    }

    // The scaffold publishes several capabilities as UNSUPPORTED (placeholder providers behind them), which is
    // deliberate: an advertised-but-unsupported capability is a diagnosable state, an absent one is not. The
    // constants must therefore cover the unsupported entries too, not just the ones that happen to work here.
    [Fact]
    public void ConstantsCoverUnsupportedCapabilitiesToo()
    {
        var runtime = EmbeddedRuntimeTestFactory.Create();

        var unsupported = runtime.GetCapabilities().Capabilities
            .Where(capability => !capability.Supported)
            .Select(capability => capability.Id)
            .ToArray();

        Assert.NotEmpty(unsupported);
        Assert.Empty(unsupported.Except(EmbeddableCapabilityIds.All, StringComparer.Ordinal));
        Assert.Contains(EmbeddableCapabilityIds.SpineGeoClipBake, unsupported);
    }

    // `All` is the enumeration consumers iterate, so it has to be the whole class rather than a list someone
    // forgot to extend when they added a constant beside it.
    [Fact]
    public void AllListsEveryConstantOnce()
    {
        var declared = typeof(EmbeddableCapabilityIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        Assert.NotEmpty(declared);
        Assert.Equal(
            declared.OrderBy(id => id, StringComparer.Ordinal),
            EmbeddableCapabilityIds.All.OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(
            EmbeddableCapabilityIds.All.Count,
            EmbeddableCapabilityIds.All.Distinct(StringComparer.Ordinal).Count());
    }

    // Ids are lowercase kebab-case on the wire. Pinned because a capitalised or underscored id would still
    // compile, still publish, and still refuse every guard an embedder wrote from the documented spelling.
    [Fact]
    public void IdsAreLowercaseKebabCase()
    {
        foreach (var id in EmbeddableCapabilityIds.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(id));
            Assert.Equal(id.ToLowerInvariant(), id);
            Assert.All(
                id,
                character => Assert.True(
                    char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-',
                    $"'{character}' in '{id}' is not lowercase kebab-case"));
        }
    }
}
