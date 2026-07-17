namespace HeadsUp.Services;

/// <summary>
/// Holds the Slack connection back until the app says it's time. On first run that lets the
/// welcome screen appear before any "Connecting…" UI — the gate opens when the user clicks
/// Connect. It's re-armable: if a sign-in is cancelled it can be closed again, so the app
/// goes idle until the user asks to connect once more (e.g. tray "Connect to Slack").
/// </summary>
public sealed class ConnectionGate
{
    private readonly object _lock = new();
    private TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsOpen
    {
        get { lock (_lock) return _opened.Task.IsCompleted; }
    }

    /// <summary>Completes once the gate is (or becomes) open; cancels with <paramref name="ct"/>.</summary>
    public async Task WaitAsync(CancellationToken ct)
    {
        while (true)
        {
            Task current;
            lock (_lock)
            {
                if (_opened.Task.IsCompleted)
                    return;
                current = _opened.Task;
            }
            await current.WaitAsync(ct);
            // Loop: re-check under lock in case it was re-armed between the await and here.
        }
    }

    /// <summary>Lets the connection proceed. Idempotent.</summary>
    public void Open()
    {
        lock (_lock)
            _opened.TrySetResult();
    }

    /// <summary>Closes the gate again so the next connection attempt waits for a fresh <see cref="Open"/>.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            if (_opened.Task.IsCompleted)
                _opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
