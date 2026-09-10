namespace MegaCrit.Sts2.UI;

public class MegaLabel
{
    private readonly string? _text;

    public MegaLabel(string? text = null)
    {
        _text = text;
    }

    public virtual string? Text => _text;
}

public sealed class MegaRichTextLabel
{
    private readonly string? _formattedText;
    private readonly string? _rawText;

    public MegaRichTextLabel(string? formattedText, string? rawText, bool richTextEnabled)
    {
        _formattedText = formattedText;
        _rawText = rawText;
        RichTextEnabled = richTextEnabled;
    }

    public bool RichTextEnabled { get; }

    public string? GetFormattedText() => _formattedText;

    public string? GetRawText() => _rawText;
}

public sealed class ThrowingMegaLabel : MegaLabel
{
    public override string? Text => throw new InvalidOperationException("text unavailable");
}
