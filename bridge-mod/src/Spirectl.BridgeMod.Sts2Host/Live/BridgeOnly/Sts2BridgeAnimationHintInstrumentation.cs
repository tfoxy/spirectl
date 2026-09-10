namespace Spirectl.Sts2.Live;

internal sealed class Sts2BridgeAnimationHintInstrumentation : ISts2AnimationHintInstrumentation
{
    public ISts2AnimationHintCounter CardFlight => Sts2AnimationHintDiagnostics.CardFlight;
    public ISts2AnimationHintCounter CardDiscard => Sts2AnimationHintDiagnostics.CardDiscard;
    public ISts2AnimationHintCounter HandTween => Sts2AnimationHintDiagnostics.HandTween;
}
