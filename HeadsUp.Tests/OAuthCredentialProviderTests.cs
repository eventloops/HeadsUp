using System.Net;
using System.Text;
using HeadsUp.Configuration;
using HeadsUp.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HeadsUp.Tests;

public class OAuthCredentialProviderTests
{
    private readonly InMemoryTokenStore _store = new();
    private readonly RecordingBrowserLauncher _browser = new();
    private readonly ConnectionGate _gate = new();
    private readonly AppStatus _status = new();
    private ISlackAppSetupPrompt _setupPrompt = new NoSlackAppSetupPrompt();
    private SlackOptions _options = new() { ClientId = "123.456", AppToken = "xapp-1" };
    private string _oauthResponse = """{"ok":true,"authed_user":{"access_token":"xoxp-new","refresh_token":"xoxe-2","expires_in":43200}}""";

    private OAuthSlackAppCredentialProvider Provider()
    {
        var handler = new StubHandler(() => _oauthResponse);
        return new OAuthSlackAppCredentialProvider(
            _store,
            new SlackOAuthClient(new HttpClient(handler), NullLogger<SlackOAuthClient>.Instance),
            new LoopbackOAuthListener(NullLogger<LoopbackOAuthListener>.Instance),
            _browser,
            _setupPrompt,
            _gate,
            new StaticMonitor<SlackOptions>(_options),
            _status,
            NullLogger<OAuthSlackAppCredentialProvider>.Instance);
    }

    private static SlackTokenSet Stored(DateTimeOffset? expiresAt, string? refreshToken = "xoxe-1") =>
        new("123.456", "xapp-1", "xoxp-stored", refreshToken, expiresAt);

    // ------------------------------------------------------------------
    // Setup prerequisites
    // ------------------------------------------------------------------

    [Fact]
    public async Task Without_a_configured_app_it_reports_not_set_up_rather_than_signing_in()
    {
        // The app-level token can't come from OAuth, so there's nothing to sign in to yet.
        _options = new SlackOptions { ClientId = "", AppToken = "" };
        _gate.Open();

        var creds = await Provider().GetCredentialsAsync(false, CancellationToken.None);

        Assert.Null(creds);
        Assert.Empty(_browser.Opened.Urls);
        Assert.Contains("not set up", _status.GetOrAdd(SlackSource.SourceId, SlackSource.Name).StatusMessage);
    }

    // ------------------------------------------------------------------
    // Stored tokens
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_valid_stored_token_is_used_without_opening_a_browser()
    {
        _store.Saved = Stored(DateTimeOffset.UtcNow.AddHours(6));

        var creds = await Provider().GetCredentialsAsync(false, CancellationToken.None);

        Assert.NotNull(creds);
        Assert.Equal("xoxp-stored", creds.UserToken);
        Assert.Equal("xapp-1", creds.AppToken);
        Assert.Empty(_browser.Opened.Urls);
    }

    [Fact]
    public async Task A_stored_token_is_returned_even_while_the_gate_is_shut()
    {
        // The gate exists to hold back *visible* sign-in, not to delay a silent reconnect.
        _store.Saved = Stored(DateTimeOffset.UtcNow.AddHours(6));

        var creds = await Provider().GetCredentialsAsync(false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(creds);
    }

    [Fact]
    public async Task A_token_with_no_expiry_is_never_refreshed()
    {
        _store.Saved = Stored(expiresAt: null);

        var creds = await Provider().GetCredentialsAsync(false, CancellationToken.None);

        Assert.Equal("xoxp-stored", creds!.UserToken);
        Assert.Equal(0, _store.SaveCount);
    }

    // ------------------------------------------------------------------
    // Rotation
    // ------------------------------------------------------------------

    [Fact]
    public async Task An_expiring_token_is_renewed_and_the_rotated_refresh_token_persisted()
    {
        _store.Saved = Stored(DateTimeOffset.UtcNow.AddMinutes(1)); // inside the refresh skew

        var creds = await Provider().GetCredentialsAsync(false, CancellationToken.None);

        Assert.Equal("xoxp-new", creds!.UserToken);
        Assert.Empty(_browser.Opened.Urls);
        // Slack invalidates the previous refresh token, so the new one must be written back.
        Assert.Equal("xoxe-2", _store.Saved!.RefreshToken);
        Assert.Equal(1, _store.SaveCount);
    }

    [Fact]
    public async Task A_forced_refresh_renews_even_a_token_that_has_not_expired()
    {
        _store.Saved = Stored(DateTimeOffset.UtcNow.AddHours(6));

        var creds = await Provider().GetCredentialsAsync(true, CancellationToken.None);

        Assert.Equal("xoxp-new", creds!.UserToken);
    }

    [Fact]
    public async Task An_unrenewable_token_is_discarded_so_the_user_can_sign_in_again()
    {
        // Refresh tokens age out after 30 days; Slack answers ok:false once they have.
        _oauthResponse = """{"ok":false,"error":"invalid_refresh_token"}""";
        _store.Saved = Stored(DateTimeOffset.UtcNow.AddMinutes(1));

        var task = Provider().GetCredentialsAsync(false, CancellationToken.None);
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(300)));

        // The stale token is cleared, and the call then blocks on the shut gate awaiting a
        // fresh sign-in rather than returning a token that cannot work.
        Assert.NotSame(task, completed);
        Assert.Null(_store.Saved);
        Assert.True(_store.Cleared);
    }

    [Fact]
    public async Task A_token_that_cannot_be_refreshed_at_all_is_not_handed_out()
    {
        _store.Saved = Stored(DateTimeOffset.UtcNow.AddMinutes(1), refreshToken: null);

        var task = Provider().GetCredentialsAsync(false, CancellationToken.None);
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(300)));

        Assert.NotSame(task, completed); // waiting on the gate for a new sign-in
    }

    // ------------------------------------------------------------------
    // Gating
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_first_run_sign_in_waits_for_the_gate_before_opening_a_browser()
    {
        var task = Provider().GetCredentialsAsync(false, CancellationToken.None);
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(300)));

        Assert.NotSame(task, completed);
        Assert.Empty(_browser.Opened.Urls); // the welcome screen gets to appear first
    }

    [Fact]
    public async Task Opening_the_gate_starts_the_browser_sign_in()
    {
        using var cts = new CancellationTokenSource();
        var task = Provider().GetCredentialsAsync(false, cts.Token);

        _gate.Open();
        await _browser.Opened.FirstOpened;

        var url = Assert.Single(_browser.Opened.Urls);
        Assert.StartsWith("https://slack.com/oauth/v2/authorize?", url);
        Assert.Contains("client_id=123.456", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains(Uri.EscapeDataString(LoopbackOAuthListener.RedirectUri), url);

        cts.Cancel(); // stop the loopback listener waiting for a redirect that won't come
        try { await task; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task An_unconfigured_app_asks_the_user_and_uses_what_they_enter()
    {
        _options = new SlackOptions(); // nothing in config, so the setup prompt is the source
        _setupPrompt = new StubSetupPrompt(new SlackAppSetup("999.888", "xapp-entered"));
        using var cts = new CancellationTokenSource();

        var task = Provider().GetCredentialsAsync(false, cts.Token);
        _gate.Open();
        await _browser.Opened.FirstOpened;

        Assert.Contains("client_id=999.888", _browser.Opened.Urls.Single());

        cts.Cancel();
        try { await task; } catch (OperationCanceledException) { }
    }

    // ------------------------------------------------------------------
    // Test doubles
    // ------------------------------------------------------------------

    private sealed class StubSetupPrompt(SlackAppSetup? result) : ISlackAppSetupPrompt
    {
        public Task<SlackAppSetup?> RequestAsync(CancellationToken ct) => Task.FromResult(result);
    }

    private sealed class InMemoryTokenStore : ISlackTokenStore
    {
        public SlackTokenSet? Saved { get; set; }
        public int SaveCount { get; private set; }
        public bool Cleared { get; private set; }

        public Task<SlackTokenSet?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Saved);

        public Task SaveAsync(SlackTokenSet tokens, CancellationToken ct = default)
        {
            Saved = tokens;
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct = default)
        {
            Saved = null;
            Cleared = true;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingBrowserLauncher : IBrowserLauncher
    {
        public OpenedUrls Opened { get; } = new();
        public void Open(string url) => Opened.Add(url);
    }

    /// <summary>Records opened URLs and lets a test await the first one.</summary>
    private sealed class OpenedUrls
    {
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Urls { get; } = [];

        public Task FirstOpened => _first.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Add(string url)
        {
            Urls.Add(url);
            _first.TrySetResult();
        }
    }

    private sealed class StubHandler(Func<string> json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json(), Encoding.UTF8, "application/json"),
            });
    }
}
