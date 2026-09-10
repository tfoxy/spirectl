namespace Spirectl.DotnetTools.Inspection;

internal sealed class ToolCommandException : Exception
{
    public ToolCommandException(int exitCode, string code, string message)
        : base(message)
    {
        ExitCode = exitCode;
        Code = code;
    }

    public int ExitCode { get; }

    public string Code { get; }
}
