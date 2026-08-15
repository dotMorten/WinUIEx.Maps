using System.Collections.Concurrent;
using System.Diagnostics.Tracing;

namespace WinUIEx.Maps.Tests.UITestHelpers;

internal sealed class RenderingEventListener : EventListener
{
    private readonly HashSet<string>? _eventNames;
    private readonly ConcurrentQueue<CapturedRenderingEvent>? _events;

    internal RenderingEventListener(params string[] eventNames)
    {
        _eventNames = eventNames.ToHashSet(StringComparer.Ordinal);
        _events = new ConcurrentQueue<CapturedRenderingEvent>();
    }

    internal CapturedRenderingEvent[] Events(string name) =>
        [.. _events!.Where(captured => captured.Name == name)];

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "WinUIEx-Maps-Rendering")
        {
            EnableEvents(
                eventSource,
                EventLevel.Verbose,
                EventKeywords.All);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventName is string name &&
            _eventNames?.Contains(name) == true &&
            _events is not null)
        {
            _events.Enqueue(new CapturedRenderingEvent(
                name,
                eventData.Payload?.ToArray() ?? []));
        }
    }
}

internal readonly record struct CapturedRenderingEvent(
    string Name,
    object?[] Payload);
