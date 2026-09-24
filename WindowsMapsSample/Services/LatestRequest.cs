namespace WindowsMapsSample.Services;

// Owned by the UI thread. A canceled SDK call can still complete; compare the ticket before publishing.
internal sealed class LatestRequest : IDisposable
{
    private CancellationTokenSource? _source;

    internal CancellationToken Begin()
    {
        Cancel();
        _source = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return _source.Token;
    }

    internal bool Owns(CancellationToken token) => _source?.Token == token;
    internal bool IsCurrent(CancellationToken token) => Owns(token) && !token.IsCancellationRequested;

    internal void Cancel()
    {
        _source?.Cancel();
        _source?.Dispose();
        _source = null;
    }

    public void Dispose() => Cancel();
}
