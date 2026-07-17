using HeadsUp.ViewModels;
using System;
using System.Windows;

namespace HeadsUp.Views;

public partial class WelcomeWindow : Window
{
    private readonly WelcomeViewModel _viewModel;

    public WelcomeWindow(WelcomeViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;

        // Connect switches to the connecting state (handled in the VM) — it doesn't close here.
        viewModel.Dismissed += (_, _) => Close();
        // CloseRequested can fire from the connection thread, so marshal to the UI thread.
        viewModel.CloseRequested += (_, _) => Dispatcher.BeginInvoke(new Action(Close));
        Closed += (_, _) => _viewModel.Dispose();
    }
}
