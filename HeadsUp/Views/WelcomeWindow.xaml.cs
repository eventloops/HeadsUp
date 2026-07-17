using HeadsUp.ViewModels;
using System.Windows;

namespace HeadsUp.Views;

public partial class WelcomeWindow : Window
{
    public WelcomeWindow(WelcomeViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.ConnectRequested += (_, _) => Close();
        viewModel.Dismissed += (_, _) => Close();
    }
}
