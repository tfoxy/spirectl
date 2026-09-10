using System.Globalization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live;

/// <summary>
/// A sentinel "marker" applied IN PLACE to a live damage/block <see cref="DynamicVar"/> so the game's
/// own <c>CardModel.GetDescriptionForPile</c> renders a recoverable placeholder where a target-varying
/// number would go. It captures the var's original values, overwrites them with a distinctive sentinel,
/// and restores them; <see cref="Sts2CardDescriptionTemplateFactory"/> renders while the sentinel is
/// applied and <see cref="CardDescriptionTemplate"/> rewrites the sentinel digits into a
/// <c>&lt;damage&gt;</c>/<c>&lt;block&gt;</c> token.
/// </summary>
///
/// <remarks>
/// P1/P2/P3 DESIGN NOTES:
///   • <c>DynamicVar.ToHighlightedString(bool)</c> — what the canonical <c>:diff()</c> formatter emits —
///     is NOT <c>virtual</c> in the game assembly, so a DynamicVar SUBCLASS cannot override the token.
///     Instead we drive the sentinel through the var's PreviewValue/EnchantedValue, which
///     <c>ToHighlightedString</c> reads: it emits <c>(int)PreviewValue</c>, uncolored because we set
///     PreviewValue == EnchantedValue (== the sentinel), so no <c>[green]/[red]</c> wraps the digits.
///   • We MUTATE the existing var rather than SWAP the dictionary entry for a plain-typed marker,
///     because per-card <c>AddExtraArgsToDescription</c> overrides may reach typed accessors
///     (<c>DynamicVars.CalculatedDamage</c>/<c>.Damage</c>/<c>.Block</c>) that would
///     <c>InvalidCastException</c> on a substituted type. Keeping the concrete type is cast-safe.
///   • The P2 corpus confirms every <c>{Damage}</c>/<c>{CalculatedDamage}</c>/<c>{Block}</c> token is
///     wrapped in <c>:diff()</c> only (176 + 16 + 79, zero <c>:plural</c>/<c>:energyIcons</c> on the
///     damage/block number), so PreviewValue is the single render path that matters.
///   • Setting <c>BaseValue</c> triggers <c>ResetToBase()</c> (Enchanted = Preview = Base), which also
///     makes the plain <c>{Damage}</c>/IConvertible paths emit the sentinel; for a CalculatedVar those
///     paths recompute via <c>Calculate()</c> instead — but no corpus card renders a calc damage/block
///     without <c>:diff()</c>, and a stray one is caught by sentinel-not-found → per-target fallback.
///
/// Runs on the game main thread synchronously; Apply/Restore are always paired via try/finally so the
/// live card is never left mutated.
/// </remarks>
internal sealed class MarkerDynamicVar
{
    private readonly DynamicVar _target;
    private readonly decimal _originalBase;
    private readonly decimal _originalEnchanted;
    private readonly decimal _originalPreview;

    public int Sentinel { get; }

    public string SlotName { get; }

    public string Kind { get; }

    public int Baseline { get; }

    public bool Inverse { get; }

    public MarkerDynamicVar(DynamicVar target, int sentinel, string slotName, string kind, int baseline, bool inverse)
    {
        _target = target;
        _originalBase = target.BaseValue;
        _originalEnchanted = target.EnchantedValue;
        _originalPreview = target.PreviewValue;
        Sentinel = sentinel;
        SlotName = slotName;
        Kind = kind;
        Baseline = baseline;
        Inverse = inverse;
    }

    public TemplateSlotSeed ToSeed() => new(Sentinel, SlotName, Kind, Baseline, Inverse);

    // Setting BaseValue calls ResetToBase() -> EnchantedValue = PreviewValue = Sentinel, so every render
    // path (:diff() via PreviewValue, plain via BaseValue, IConvertible via BaseValue) emits the digits.
    public void Apply() => _target.BaseValue = Sentinel;

    public void Restore()
    {
        _target.BaseValue = _originalBase; // resets Enchanted/Preview to base first...
        _target.EnchantedValue = _originalEnchanted; // ...then pin them back to the captured values.
        _target.PreviewValue = _originalPreview;
    }

    public static string SentinelDigits(int sentinel) => sentinel.ToString(CultureInfo.InvariantCulture);
}
