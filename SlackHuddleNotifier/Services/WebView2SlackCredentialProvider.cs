using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
/// paste. The window doubles as a "Connecting…" loading screen and only reveals the
/// Slack sign-in page when there's no existing session. Windows-only; registered by the WPF head.
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
    private readonly AppStatus _status;
    private readonly ILogger<WebView2SlackCredentialProvider> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Window? _window;   // hosts the WebView2 (off-screen unless a sign-in is needed)
    private Window? _splash;   // separate "Connecting…" loading window (avoids WebView2 airspace)
    private WebView2? _web;
    private SlackCredentials? _cached;
    private bool _navigated;

    public WebView2SlackCredentialProvider(
        IOptionsMonitor<SlackClientOptions> options,
        AppStatus status,
        ILogger<WebView2SlackCredentialProvider> logger)
    {
        _options = options;
        _status = status;
        _logger = logger;
        _dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException(
                "WebView2SlackCredentialProvider needs a WPF Application dispatcher.");

        // Keep the "Connecting…" splash up until the socket actually connects, so nothing
        // appears to vanish between reading the session and being live.
        _status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppStatus.IsConnected) && _status.IsConnected)
                _dispatcher.BeginInvoke(new Action(CloseSplash));
        };
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
            ShowSplash(); // "Connecting…" while the off-screen web view loads/checks the session
            if (!_navigated)
            {
                // The workspace URL redirects straight to that team's sign-in (SSO), which is
                // friendlier than app.slack.com (which bounces a signed-out user to marketing).
                var startUrl = string.IsNullOrWhiteSpace(_options.CurrentValue.WorkspaceUrl) ? SlackUrl : _options.CurrentValue.WorkspaceUrl;
                _web!.CoreWebView2.Navigate(startUrl);
                _navigated = true;
            }

            // Fast path: a warm session surfaces the token within seconds, so an already-signed-in
            // user just sees the splash and never the login page.
            var token = await PollForTokenAsync(LoggedInPollTimeout, ct);
            if (token is null)
            {
                // No existing session - hide the splash and bring the web view forward to sign in.
                CloseSplash();
                ShowWebViewOnScreen();
                _logger.LogInformation(
                    "Complete the Slack sign-in in the window (SSO included). It closes itself once "
                    + "your session is read.");
                // Generous poll; if it still times out, the outer retry keeps polling this same
                // window without re-navigating, so a slow sign-in is never interrupted.
                token = await PollForTokenAsync(SignInPollTimeout, ct);
                HideWebView();
                ShowSplash(); // back to "Connecting…" while the session finishes authenticating
            }

            if (token is null)
            {
                _logger.LogWarning("Could not obtain a Slack token from the WebView2 session.");
                return null; // leave the splash up; the outer retry loop keeps trying
            }

            var cookie = await ReadCookieHeaderAsync();
            if (cookie is null)
            {
                _logger.LogWarning("Slack token found but the 'd' cookie was missing from the session.");
                return null;
            }

            // Splash stays up until the source reports IsConnected (handled in the constructor).
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

        _web = new WebView2();
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
            Content = _web,
        };
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

    /// <summary>A clean, floating "Connecting to Slack…" card - separate window to dodge WebView2 airspace.</summary>
    private static Window BuildSplash()
    {
        var font = new FontFamily("Segoe UI");
        var accent = new SolidColorBrush(Color.FromRgb(0x4A, 0x15, 0x4B));
        var muted = new SolidColorBrush(Color.FromRgb(0x66, 0x70, 0x85));

        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico");
        if (File.Exists(iconPath))
        {
            try
            {
                panel.Children.Add(new Image
                {
                    Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(iconPath)),
                    Width = 44,
                    Height = 44,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 14)
                });
            }
            catch { /* icon is a nicety, not required */ }
        }

        panel.Children.Add(new TextBlock
        {
            Text = "Heads-Up",
            FontFamily = font,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x18, 0x28)),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Connecting to Slack…",
            FontFamily = font,
            FontSize = 13,
            Foreground = muted,
            Margin = new Thickness(0, 5, 0, 20),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(new ProgressBar
        {
            IsIndeterminate = true,
            Width = 240,
            Height = 4,
            Foreground = accent,
            Background = new SolidColorBrush(Color.FromRgb(0xEC, 0xEE, 0xF1)),
            BorderThickness = new Thickness(0)
        });

        var card = new Border
        {
            Background = System.Windows.Media.Brushes.White,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(44, 34, 44, 36),
            Margin = new Thickness(24), // leaves room for the drop shadow
            Child = panel,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                Opacity = 0.16,
                BlurRadius = 30,
                ShadowDepth = 4,
                Direction = 270
            }
        };

        return new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = true,
            Topmost = true,
            Title = "Connecting to Slack…",
            Background = System.Windows.Media.Brushes.Transparent,
            Content = card,
        };
    }

    private void ShowSplash()
    {
        _splash ??= BuildSplash();
        _splash.Show();
        _splash.Activate();
    }

    private void CloseSplash()
    {
        if (_splash is null) return;
        _splash.Close();
        _splash = null;
    }

    /// <summary>Bring the web-view window on-screen so the user can complete sign-in.</summary>
    private void ShowWebViewOnScreen()
    {
        if (_window is null) return;
        _window.Title = "Sign in to Slack";
        _window.ShowInTaskbar = true;
        _window.WindowState = WindowState.Normal;
        _window.Left = (SystemParameters.PrimaryScreenWidth - _window.Width) / 2;
        _window.Top = (SystemParameters.PrimaryScreenHeight - _window.Height) / 2;
        _window.Show();
        _window.Activate();
    }

    /// <summary>Tuck the web-view window off-screen (kept alive for reuse/refresh).</summary>
    private void HideWebView()
    {
        if (_window is null) return;
        _window.ShowInTaskbar = false;
        _window.Left = -32000;
        _window.Top = -32000;
    }
}
