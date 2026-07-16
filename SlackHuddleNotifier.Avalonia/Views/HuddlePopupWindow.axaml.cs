using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System.Net.Http;

namespace SlackHuddleNotifier.AvaloniaApp.Views;

public partial class HuddlePopupWindow : Window
{
    private static readonly HttpClient Http = new();

    private DispatcherTimer? _countdownTimer;

    public HuddlePopupWindow()
    {
        InitializeComponent();
        // SizeToContent means Bounds isn't final at Opened, so re-pin on every
        // layout pass; the window never moves once content height settles.
        Opened += (_, _) => PositionBottomRight();
        LayoutUpdated += (_, _) => PositionBottomRight();
    }

    private void PositionBottomRight()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null)
            return;

        var workArea = screen.WorkingArea;
        var frame = PixelSize.FromSize(Bounds.Size, screen.Scaling);
        Position = new PixelPoint(
            workArea.X + workArea.Width - frame.Width - 16,
            workArea.Y + workArea.Height - frame.Height - 16);
    }

    /// <summary>Marks the popup as a priority huddle (red header, green join button).</summary>
    public void SetPriority(bool priority)
    {
        if (priority)
            Classes.Add("priority");
    }

    /// <summary>Downloads the initiator's avatar and lays it over the initials circle.</summary>
    public async void LoadAvatar(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            using var stream = new MemoryStream(bytes);
            AvatarEllipse.Fill = new ImageBrush(new Bitmap(stream)) { Stretch = Stretch.UniformToFill };
        }
        catch
        {
            // Initials stay visible if the avatar can't be fetched.
        }
    }

    /// <summary>Shrinks the countdown bar from full width to zero over the popup lifetime.</summary>
    public void StartCountdown(TimeSpan duration)
    {
        var started = DateTimeOffset.Now;
        var scale = (ScaleTransform)CountdownFill.RenderTransform!;

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _countdownTimer.Tick += (_, _) =>
        {
            var remaining = 1 - (DateTimeOffset.Now - started) / duration;
            scale.ScaleX = Math.Max(0, remaining);
            if (remaining <= 0)
                _countdownTimer!.Stop();
        };
        _countdownTimer.Start();
    }

    public void AnimateIn()
    {
        Opacity = 0;
        Show();
        FadeTo(1, TimeSpan.FromMilliseconds(250));
    }

    public void AnimateOut()
    {
        _countdownTimer?.Stop();
        FadeTo(0, TimeSpan.FromMilliseconds(200), Close);
    }

    private void FadeTo(double target, TimeSpan duration, Action? completed = null)
    {
        var from = Opacity;
        var started = DateTimeOffset.Now;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var progress = Math.Min(1, (DateTimeOffset.Now - started) / duration);
            Opacity = from + (target - from) * progress;
            if (progress >= 1)
            {
                timer.Stop();
                completed?.Invoke();
            }
        };
        timer.Start();
    }
}
