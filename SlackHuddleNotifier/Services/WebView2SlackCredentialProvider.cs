using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SlackHuddleNotifier.Configuration;

namespace SlackHuddleNotifier.Services;

/// <summary>
/// A live credential provider backed by an embedded WebView2 that stays signed in to
/// Slack. It reads the <c>xoxc</c> token and <c>d</c>/<c>d-s</c> cookies straight from
/// that session, so credentials are always current and survive rotation - no manual
/// paste. The window is kept off-screen and only surfaced when a sign-in is needed.
/// Windows-only; registered by the WPF head.
/// </summary>
public sealed class WebView2SlackCredentialProvider : ISlackCredentialProvider
{
    private const string SlackUrl = "https://app.slack.com/";
    private static readonly TimeSpan LoggedInPollTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SignInPollTimeout = TimeSpan.FromMinutes(5);

    // Finds the token anywhere in localStorage; prefers the canonical localConfig_v2 team
    // token and falls back to a scan. Returns a JSON blob (also lists keys, for diagnostics).
    private const string ExtractTokenScript = """
        (function () {
          try {
            var rx = /xoxc-[A-Za-z0-9-]{20,}/;
            var out = { token: null, via: null, keys: [] };
            var lc = localStorage.getItem('localConfig_v2');
            if (lc) {
              try {
                var teams = (JSON.parse(lc).teams) || {};
                for (var k in teams) {
                  var t = teams[k] && teams[k].token;
                  if (t && rx.test(t)) { out.token = t; out.via = 'localConfig_v2:' + k; break; }
                }
              } catch (e) {}
            }
            for (var i = 0; i < localStorage.length; i++) {
              var key = localStorage.key(i);
              out.keys.push(key);
              if (!out.token) {
                var m = (localStorage.getItem(key) || '').match(rx);
                if (m) { out.token = m[0]; out.via = 'scan:' + key; }
              }
            }
            return JSON.stringify(out);
          } catch (e) { return JSON.stringify({ error: String(e) }); }
        })()
        """;

    private readonly IOptionsMonitor<SlackClientOptions> _options;
    private readonly ILogger<WebView2SlackCredentialProvider> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Window? _window;
    private WebView2? _web;
    private SlackCredentials? _cached;
    private bool _navigated;

    public WebView2SlackCredentialProvider(
        IOptionsMonitor<SlackClientOptions> options,
        ILogger<WebView2SlackCredentialProvider> logger)
    {
        _options = options;
        _logger = logger;
        _dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException(
                "WebView2SlackCredentialProvider needs a WPF Application dispatcher.");
    }

    public string SourceDescription => "WebView2 Slack session";

    public async Task<SlackCredentials?> GetCredentialsAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && _cached is not null)
            return _cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _cached is not null)
                return _cached;

            _cached = await _dispatcher.InvokeAsync(() => ReadCredentialsAsync(ct)).Task.Unwrap();
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Everything below runs on the WPF UI thread.
    private async Task<SlackCredentials?> ReadCredentialsAsync(CancellationToken ct)
    {
        try
        {
            await EnsureWebViewAsync(ct);
            if (!_navigated)
            {
                // The workspace URL redirects straight to that team's sign-in (SSO), which is
                // friendlier than app.slack.com (which bounces a signed-out user to marketing).
                var startUrl = string.IsNullOrWhiteSpace(_options.CurrentValue.WorkspaceUrl) ? SlackUrl : _options.CurrentValue.WorkspaceUrl;
                _web!.CoreWebView2.Navigate(startUrl);
                _navigated = true;
            }

            // Fast path: an already-signed-in session surfaces the token within seconds.
            var token = await PollForTokenAsync(LoggedInPollTimeout, ct);
            if (token is null)
            {
                ShowForLogin();
                _logger.LogInformation(
                    "Complete the Slack sign-in in the window (SSO included). It stays in this one " +
                    "window and closes itself once your session is read.");
                // Generous poll; if it still times out, the outer retry keeps polling this same
                // window without re-navigating, so a slow sign-in is never interrupted.
                token = await PollForTokenAsync(SignInPollTimeout, ct);
                if (token is not null)
                    HideOffScreen();
            }

            if (token is null)
            {
                _logger.LogWarning("Could not obtain a Slack token from the WebView2 session.");
                return null;
            }

            var cookie = await ReadCookieHeaderAsync();
            if (cookie is null)
            {
                _logger.LogWarning("Slack token found but the 'd' cookie was missing from the session.");
                return null;
            }

            return new SlackCredentials(token, cookie, _options.CurrentValue.WorkspaceUrl);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebView2 credential read failed (is the WebView2 Runtime installed?).");
            return null;
        }
    }

    private async Task EnsureWebViewAsync(CancellationToken ct)
    {
        if (_web is not null)
            return;

        var dataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SlackHuddleNotifier", "WebView2");
        Directory.CreateDirectory(dataFolder);

        _window = new Window
        {
            Title = "Sign in to Slack",
            Width = 480,
            Height = 700,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false,
            ShowActivated = false,
        };
        _web = new WebView2();
        _window.Content = _web;
        _window.Show(); // off-screen; needed so the control initialises its HWND

        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
        await _web.EnsureCoreWebView2Async(env);

        // Keep the whole flow - including the SSO "Authenticate" hop - inside this one
        // window instead of letting window.open() spawn popups we do not watch.
        _web.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            _web!.CoreWebView2.Navigate(e.Uri);
        };

        _logger.LogDebug("WebView2 initialised (data folder {Folder}).", dataFolder);
    }

    private async Task<string?> PollForTokenAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        string? lastUrl = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var url = _web!.CoreWebView2.Source;
            if (url != lastUrl)
            {
                _logger.LogDebug("WebView2 is at {Url}", url);
                lastUrl = url;
            }

            var raw = await _web.CoreWebView2.ExecuteScriptAsync(ExtractTokenScript);
            // ExecuteScriptAsync returns the JS result JSON-encoded; our result is a string,
            // so unwrap one layer to get the inner JSON object.
            var inner = JsonSerializer.Deserialize<string>(raw);
            if (inner is not null)
            {
                using var doc = JsonDocument.Parse(inner);
                var root = doc.RootElement;
                if (root.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    var via = root.TryGetProperty("via", out var v) ? v.GetString() : null;
                    _logger.LogInformation("Slack token located in WebView2 session (via {Via}).", via);
                    return t.GetString();
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
        }
        _logger.LogDebug("No token within {Timeout}s; window is at {Url}.", timeout.TotalSeconds, lastUrl);
        return null;
    }

    private async Task<string?> ReadCookieHeaderAsync()
    {
        var cookies = await _web!.CoreWebView2.CookieManager.GetCookiesAsync("https://app.slack.com");
        string? d = null, ds = null;
        foreach (var c in cookies)
        {
            if (c.Name == "d") d = c.Value;
            else if (c.Name == "d-s") ds = c.Value;
        }
        if (d is null)
            return null;

        _logger.LogDebug("Cookies read from session: d(len {D}){DS}.",
            d.Length, ds is null ? ", no d-s" : $", d-s(len {ds.Length})");
        return ds is null ? $"d={d}" : $"d={d}; d-s={ds}";
    }

    private void ShowForLogin()
    {
        if (_window is null || _window.ShowInTaskbar) return; // already surfaced
        _window.ShowInTaskbar = true;
        _window.Left = (SystemParameters.PrimaryScreenWidth - _window.Width) / 2;
        _window.Top = (SystemParameters.PrimaryScreenHeight - _window.Height) / 2;
        _window.Show();
        _window.Activate();
    }

    private void HideOffScreen()
    {
        if (_window is null) return;
        _window.ShowInTaskbar = false;
        _window.Left = -32000;
        _window.Top = -32000;
    }
}
