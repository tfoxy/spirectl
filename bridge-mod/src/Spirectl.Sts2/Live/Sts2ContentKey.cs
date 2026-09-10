using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Spirectl.Sts2.Live;

// R13 producer CONTENT KEY — PURE and Godot-free (like Sts2TopBarFold / Sts2ProceedGlow / Sts2MapPointPulse) so the
// identity rule and the serial registry are unit-testable without the Godot-coupled Sts2RuntimeSceneWatcher.
//
// WHY. Card visual nodes are POOLED. `NCard` implements `IPoolable`: the game keeps ~30 instances alive and
// re-assigns `Model` as cards move between hand / draw / discard / reward / shop / deck view. So a streamed node's
// INSTANCE ID says nothing about which card the player is looking at — the same id is a Strike this tick and a
// Bash the next, and two different ids may both be Strike. A client that wants to key anything on card CONTENT (a
// cached render, a re-attach signature, a stable list key across a shuffle) has nothing to key on today.
//
// WHAT WE SHIP. One STATIC string per card-scene root, `RuntimeSceneNodeDelta.ContentKey`, of the form
//
//     nc:{entry}#{serial}
//
//   * `{entry}` is `NCard.Model.Id.Entry` — the card DEFINITION id ("Strike"), shared by every duplicate in a deck,
//     which is exactly what makes it useful for content-addressed caching.
//   * `{serial}` distinguishes the INSTANCES that share a definition: two Strikes in one hand must not collide, or
//     a client keying on the pair would fold them into one element. It is a process-monotonic counter handed out on
//     first sight of a MODEL OBJECT.
//
// WHY THE SERIAL IS KEYED ON THE MODEL OBJECT (and lives in a ConditionalWeakTable). The model is the thing with
// card identity and a real lifetime; the visual node is a recycled shell. Keying the registry on the CardModel means
// a card keeps ONE key while it is dragged, played, shuffled and redrawn through a dozen different pooled nodes —
// and loses it only when the game drops the model. A ConditionalWeakTable holds the model WEAKLY, so the registry
// can never keep a finished run's cards (or a whole CombatState) alive: entries evict themselves with their card.
// Nothing here is keyed by node instance id, which for a pooled node would go stale the moment it is recycled — the
// watcher therefore resolves the key on EVERY node add rather than caching it per tracked node.
//
// The entry is captured with the serial on first sight: `AbstractModel.Id` is assigned once (`InitId`) and never
// changes, so the composed key is stable for the model's life and later lookups are one dictionary probe with no
// string formatting.
internal static class Sts2ContentKey
{
    /// <summary>The instanced scene whose root is an <c>NCard</c> (`NCard.AssetPaths[0]`).</summary>
    internal const string CardSceneFile = "res://scenes/cards/card.tscn";

    /// <summary>Namespace prefix of a card content key, so a future non-card key space can't collide.</summary>
    internal const string CardPrefix = "nc";

    // model object -> its composed key. Weak on the KEY, so a dropped card model takes its entry with it.
    private static readonly ConditionalWeakTable<object, string> Keys = new();

    // Process-monotonic serial source. Global (not per-entry) so it needs no dictionary and no lock — the only
    // requirement is that two live models never share a serial.
    private static long _nextSerial;

    /// <summary>True when an instanced-scene root's scene file is the card scene.</summary>
    internal static bool IsCardScene(string? sceneFilePath)
        => string.Equals(sceneFilePath, CardSceneFile, StringComparison.Ordinal);

    /// <summary>
    /// The stable content key for one card MODEL, allocating a serial the first time the model is seen and
    /// returning that same key forever after. Null when the node has no model yet (a pooled shell between
    /// assignments) or the model has no entry — the field is then simply omitted from the wire.
    /// </summary>
    /// <param name="model">The `NCard.Model` object (typed as <see cref="object"/> to keep this class STS2-free).</param>
    /// <param name="entry">`model.Id.Entry`, the card definition id.</param>
    internal static string? ForModel(object? model, string? entry)
    {
        if (model is null || string.IsNullOrEmpty(entry))
        {
            return null;
        }

        return Keys.GetValue(model, _ => Compose(entry, Interlocked.Increment(ref _nextSerial)));
    }

    /// <summary>The key format, single-sourced so the client contract and the tests read the same code.</summary>
    internal static string Compose(string entry, long serial) => CardPrefix + ":" + entry + "#" + serial;
}
