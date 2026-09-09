namespace PCMode.Audio.Host;

public sealed class AudioHostLifetime : IDisposable
{
    private readonly CancellationTokenSource _stopping = new();

    public CancellationToken Stopping => _stopping.Token;

    public bool IsStopping => _stopping.IsCancellationRequested;

    public void RequestStop()
    {
        if (!_stopping.IsCancellationRequested)
        {
            _stopping.Cancel();
        }
    }

    public void Dispose()
    {
        _stopping.Dispose();
    }
}
