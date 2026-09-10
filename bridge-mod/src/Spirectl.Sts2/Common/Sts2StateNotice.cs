using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2;

public static class Sts2StateNotice
{
    public static StateNoticeSnapshot Partial(
        string code,
        string message,
        string path,
        string source)
        => new(
            code,
            message,
            true,
            Path: path,
            Severity: "partial",
            Source: source);

    public static void AddPartialOnce(
        ICollection<StateNoticeSnapshot>? notices,
        string code,
        string message,
        string path,
        string source)
    {
        if (notices is null || notices.Any(notice => notice.Code == code))
        {
            return;
        }

        notices.Add(Partial(code, message, path, source));
    }
}
