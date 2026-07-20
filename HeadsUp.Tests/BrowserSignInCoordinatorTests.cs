using HeadsUp.Services;
using Xunit;

namespace HeadsUp.Tests;

/// <summary>
/// The gate reset is the reason this type exists. Both credential providers grew the same
/// sign-in dance independently and both omitted it, so a user who closed the browser without
/// approving got a fresh window every reconnect interval — forever.
/// </summary>
public class BrowserSignInCoordinatorTests
{
    private readonly ConnectionGate _gate = new();

    private BrowserSignInCoordinator Coordinator() => new(_gate);

    [Fact]
    public async Task An_abandoned_sign_in_closes_the_gate_again()
    {
        _gate.Open();

        var result = await Coordinator().SignInAsync<string>(
            alreadySignedIn: _ => Task.FromResult<string?>(null),
            signIn: _ => Task.FromResult<string?>(null),   // user closed the browser
            CancellationToken.None);

        Assert.Null(result);
        // Without this the next reconnect walks straight back into opening a browser.
        Assert.False(_gate.IsOpen);
    }

    [Fact]
    public async Task A_successful_sign_in_leaves_the_gate_open()
    {
        _gate.Open();

        var result = await Coordinator().SignInAsync(
            alreadySignedIn: _ => Task.FromResult<string?>(null),
            signIn: _ => Task.FromResult<string?>("session"),
            CancellationToken.None);

        Assert.Equal("session", result);
        Assert.True(_gate.IsOpen);
    }

    [Fact]
    public async Task Sign_in_waits_for_the_gate()
    {
        var attempts = 0;
        var task = Coordinator().SignInAsync<string>(
            alreadySignedIn: _ => Task.FromResult<string?>(null),
            signIn: _ => { attempts++; return Task.FromResult<string?>("session"); },
            CancellationToken.None);

        var raced = await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(200)));

        Assert.NotSame(task, raced);   // still waiting
        Assert.Equal(0, attempts);

        _gate.Open();
        Assert.Equal("session", await task);
    }

    [Fact]
    public async Task A_caller_queued_behind_a_sign_in_uses_its_result_rather_than_prompting_again()
    {
        // Otherwise two connectors starting together open two browser windows.
        _gate.Open();
        var coordinator = Coordinator();
        var signInCount = 0;
        string? completed = null;

        async Task<string?> SignIn(CancellationToken ct)
        {
            signInCount++;
            await Task.Delay(50, ct);
            completed = "session";
            return completed;
        }

        Task<string?> AlreadySignedIn(CancellationToken ct) => Task.FromResult(completed);

        var first = coordinator.SignInAsync(AlreadySignedIn, SignIn, CancellationToken.None);
        var second = coordinator.SignInAsync(AlreadySignedIn, SignIn, CancellationToken.None);

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, signInCount);
        Assert.All(results, r => Assert.Equal("session", r));
    }

    [Fact]
    public async Task A_reset_gate_can_be_opened_again_for_a_retry()
    {
        // The user clicking Connect a second time must work.
        _gate.Open();
        var coordinator = Coordinator();

        await coordinator.SignInAsync<string>(
            _ => Task.FromResult<string?>(null), _ => Task.FromResult<string?>(null), CancellationToken.None);
        Assert.False(_gate.IsOpen);

        _gate.Open();
        var result = await coordinator.SignInAsync(
            _ => Task.FromResult<string?>(null), _ => Task.FromResult<string?>("session"), CancellationToken.None);

        Assert.Equal("session", result);
    }
}
