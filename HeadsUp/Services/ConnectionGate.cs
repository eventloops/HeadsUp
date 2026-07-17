namespace HeadsUp.Services;

/// <summary>
/// Holds the Slack connection back until the app says it's time. On first run that lets the
/// welcome screen appear before any "Connecting…" UI — the gate opens when the user clicks
/// Connect. On later runs it's opened immediately, so the app connects right away. Open once,
/// it stays open.
/// </summary>
public sealed class ConnectionGate
{
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsOpen => _opened.Task.IsCompleted;

    /// <summary>Completes once the gate is opened; cancels with <paramref name="ct"/>.</summary>
    public Task WaitAsync(CancellationToken ct) => _opened.Task.WaitAsync(ct);

    /// <summary>Lets the connection proceed. Idempotent.</summary>
    public void Open() => _opened.TrySetResult();
}
