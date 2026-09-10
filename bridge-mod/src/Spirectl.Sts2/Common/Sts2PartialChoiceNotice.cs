using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2;

public static class Sts2PartialChoiceNotice
{
    public static StateNoticeSnapshot? TryCreate(
        string code,
        string message,
        int visibleChoiceCount,
        int executableChoiceCount,
        string path = "availableActions",
        string source = nameof(Sts2PartialChoiceNotice))
    {
        if (visibleChoiceCount <= 0 || executableChoiceCount >= visibleChoiceCount)
        {
            return null;
        }

        return Sts2StateNotice.Partial(
            code,
            message,
            path,
            source);
    }
}
