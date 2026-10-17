namespace WolfLeash.Dashboard;

// Blazor retains a disconnected circuit briefly. Stop polling immediately, rather than waiting for disposal.
public sealed class DashboardConnection : IDisposable
{
    private readonly object _gate = new();
    private TaskCompletionSource _ready = NewSignal();
    private CancellationTokenSource? _connection;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void SetConnected(bool connected)
    {
        CancellationTokenSource? previous = null;
        lock (_gate)
        {
            if (connected && _connection is null)
            {
                _connection = new();
                _ready.TrySetResult();
            }
            else if (!connected && _connection is not null)
            {
                previous = _connection;
                _connection = null;
                _ready = NewSignal();
            }
        }
        previous?.Cancel();
        previous?.Dispose();
    }

    public async Task<CancellationToken> WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task ready;
            lock (_gate)
            {
                if (_connection is not null) return _connection.Token;
                ready = _ready.Task;
            }
            await ready.WaitAsync(cancellationToken);
        }
    }
    public void Dispose() => SetConnected(false);
}
