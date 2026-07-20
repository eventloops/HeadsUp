namespace HeadsUp.Services;

/// <summary>
/// Runs an interactive browser sign-in: gated behind the user asking to connect, serialised so
/// two callers can't both open a browser, and — critically — closing the gate again when a
/// sign-in doesn't complete.
///
/// That last part is why this exists as shared code. Both credential providers grew the same
/// gate/lock/double-check dance independently, and both omitted the reset that
/// <see cref="WebView2SlackCredentialProvider"/> had all along. Without it, a user who closes
/// the browser without approving gets a fresh browser window every reconnect interval, forever:
/// the gate stays open, so the next round goes straight back into sign-in.
/// </summary>
public sealed class BrowserSignInCoordinator
{
    private readonly ConnectionGate _gate;

    // Separate from any caller's own locking: this one only serialises the interactive part,
    // so a cheap credential read is never stuck behind a browser window.
    private readonly SemaphoreSlim _signIn = new(1, 1);

    public BrowserSignInCoordinator(ConnectionGate gate) => _gate = gate;

    /// <summary>
    /// Waits for the connect gate, then runs <paramref name="signIn"/> under a lock.
    ///
    /// <paramref name="alreadySignedIn"/> is re-checked after acquiring the lock, because a
    /// caller that queued behind someone else's sign-in should use its result rather than
    /// prompting again.
    /// </summary>
    public async Task<T?> SignInAsync<T>(
        Func<CancellationToken, Task<T?>> alreadySignedIn,
        Func<CancellationToken, Task<T?>> signIn,
        CancellationToken ct)
        where T : class
    {
        await _gate.WaitAsync(ct);
        await _signIn.WaitAsync(ct);
        try
        {
            if (await alreadySignedIn(ct) is { } existing)
                return existing;

            var result = await signIn(ct);

            // Abandoned or refused: hold until the user asks to connect again, rather than
            // reopening a browser on the next reconnect.
            if (result is null)
                _gate.Reset();

            return result;
        }
        finally
        {
            _signIn.Release();
        }
    }
}
