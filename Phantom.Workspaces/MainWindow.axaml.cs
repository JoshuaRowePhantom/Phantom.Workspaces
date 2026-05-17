using Avalonia.Controls;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces;

public partial class MainWindow : Window
{
    public MainWindow()
        : this(new MainWindowViewModel(new RepositorySource(RepositorySourceType.Unknown, "(none)")))
    {
    }

    public MainWindow(
        MainWindowViewModel viewModel)
    {
        InitializeComponent();
        this.DataContext = viewModel;
    }
}