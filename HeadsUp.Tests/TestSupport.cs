using HeadsUp.Models;
using HeadsUp.Services;
using Microsoft.Extensions.Options;

namespace HeadsUp.Tests;

/// <summary>An <see cref="IOptionsMonitor{T}"/> that always returns one fixed value.</summary>
internal sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>An <see cref="IAlertSource"/> tests can raise at will, with a configurable identity.</summary>
internal sealed class FakeAlertSource : IAlertSource
{
    public FakeAlertSource(string sourceId = "test", string sourceName = "Test")
    {
        SourceId = sourceId;
        SourceName = sourceName;
    }

    public string SourceId { get; }
    public string SourceName { get; }
    public event EventHandler<Alert>? AlertRaised;
    public void Raise(Alert alert) => AlertRaised?.Invoke(this, alert);
}

/// <summary>Records every alert shown; optionally faults to exercise error paths.</summary>
internal sealed class RecordingNotificationService : INotificationService
{
    private readonly TaskCompletionSource<Alert> _first =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<Alert> Shown { get; } = [];
    public Exception? ThrowOnShow { get; set; }

    /// <summary>Completes when the first alert arrives (5s timeout).</summary>
    public Task<Alert> FirstAlert => _first.Task.WaitAsync(TimeSpan.FromSeconds(5));

    public Task ShowAlertAsync(Alert alert, CancellationToken ct = default)
    {
        if (ThrowOnShow is not null)
            return Task.FromException(ThrowOnShow);

        Shown.Add(alert);
        _first.TrySetResult(alert);
        return Task.CompletedTask;
    }
}

/// <summary>Hands out one fixed credential set (or none) and counts the requests.</summary>
internal sealed class StaticCredentialProvider(SlackCredentials? creds) : ISlackCredentialProvider
{
    private int _calls;

    public int Calls => _calls;
    public string SourceDescription => "test credentials";

    public Task<SlackCredentials?> GetCredentialsAsync(bool forceRefresh, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(creds);
    }
}

/// <summary>Waits (with timeout) for an <see cref="IAlertSource"/> to raise its next alert.</summary>
internal sealed class AlertWaiter
{
    private readonly TaskCompletionSource<Alert> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AlertWaiter(IAlertSource source) => source.AlertRaised += (_, a) => _tcs.TrySetResult(a);

    public Task<Alert> Alert => _tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

    public async Task<bool> RaisedWithin(TimeSpan window)
    {
        try
        {
            await _tcs.Task.WaitAsync(window);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
