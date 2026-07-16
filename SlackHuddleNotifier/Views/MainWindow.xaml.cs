using SlackHuddleNotifier.ViewModels;
using System.Windows;

namespace SlackHuddleNotifier.Views;

public partial class MainWindow : Window
{
    public MainWindow(TrayViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.ExitRequested += (_, _) => Application.Current.Shutdown();
    }
}
