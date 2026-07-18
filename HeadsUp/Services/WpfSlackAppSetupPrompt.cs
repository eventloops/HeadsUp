using System.Windows.Threading;
using HeadsUp.ViewModels;
using HeadsUp.Views;

namespace HeadsUp.Services;

/// <summary>
/// Shows the Slack-app setup window and returns what the user entered.
///
/// The provider that calls this runs on a background thread, so the window has to be marshalled
/// onto the UI dispatcher; the returned task completes when the window closes.
/// </summary>
public sealed class WpfSlackAppSetupPrompt : ISlackAppSetupPrompt
{
    private readonly IBrowserLauncher _browser;
    private readonly Dispatcher _dispatcher;

    public WpfSlackAppSetupPrompt(IBrowserLauncher browser)
    {
        _browser = browser;
        _dispatcher = System.Windows.Application.Current.Dispatcher;
    }

    public async Task<SlackAppSetup?> RequestAsync(CancellationToken ct)
    {
        return await _dispatcher.InvokeAsync(() =>
        {
            var vm = new SlackAppSetupViewModel(_browser);
            var window = new SlackAppSetupWindow(vm);

            // Modal: there is nothing useful to do in the app until this is answered, and it
            // keeps a second prompt from stacking if another connection attempt comes round.
            window.ShowDialog();
            return vm.Result;
        }).Task.WaitAsync(ct);
    }
}
