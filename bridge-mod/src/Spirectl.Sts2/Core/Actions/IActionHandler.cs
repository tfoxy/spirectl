namespace Spirectl.Sts2.Core.Actions;

public interface IActionHandler
{
    ActionExecutionResult Execute(SemanticActionRequest request);
}
