using HeadsUp.ViewModels;
using System.Windows;

namespace HeadsUp.Views;

public partial class MainWindow : Window
{
    public MainWindow(TrayViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.ExitRequested += (_, _) => Application.Current.Shutdown();
    }
}
