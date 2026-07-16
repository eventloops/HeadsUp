using System.Windows;

namespace SlackHuddleNotifier.Views;

public partial class HuddlePopupWindow : Window
{
    public HuddlePopupWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionBottomRight();
    }

    private void PositionBottomRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 16;
        Top = workArea.Bottom - ActualHeight - 16;
    }

    /// <summary>Shrinks the countdown bar from full width to zero over the popup lifetime.</summary>
    public void StartCountdown(TimeSpan duration)
    {
        var animation = new System.Windows.Media.Animation.DoubleAnimation(1, 0, duration)
        {
            FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
        };
        CountdownScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, animation);
    }

    /// <summary>Slide the window in with a fade animation.</summary>
    public void AnimateIn()
    {
        Opacity = 0;
        Show();

        var animation = new System.Windows.Media.Animation.DoubleAnimation(0, 1,
            TimeSpan.FromMilliseconds(250));
        BeginAnimation(OpacityProperty, animation);
    }

    /// <summary>Fade out and close.</summary>
    public void AnimateOut()
    {
        var animation = new System.Windows.Media.Animation.DoubleAnimation(1, 0,
            TimeSpan.FromMilliseconds(200));
        animation.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, animation);
    }
}
