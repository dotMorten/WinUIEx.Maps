using System.Diagnostics.Tracing;

namespace WindowsMapsSample.Services;

[EventSource(Name = "WindowsMapsSample")]
internal sealed class SampleEventSource : EventSource
{
    internal static readonly SampleEventSource Log = new();
    public static class Keywords
    {
        public const EventKeywords Errors = (EventKeywords)1;
    }

    [NonEvent]
    internal void UnhandledFailure(Exception exception)
    {
        if (IsEnabled(EventLevel.Error, Keywords.Errors))
            UnhandledFailure(exception.GetType().FullName ?? exception.GetType().Name,
                exception.HResult, exception.StackTrace ?? "");
    }

    [Event(1, Level = EventLevel.Error, Keywords = Keywords.Errors)]
    public void UnhandledFailure(string exceptionType, int hresult, string stack) =>
        WriteEvent(1, exceptionType, hresult, stack);
}
