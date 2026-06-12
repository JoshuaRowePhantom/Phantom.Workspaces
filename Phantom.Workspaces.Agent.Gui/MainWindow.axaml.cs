using Avalonia.Controls;
using Phantom.Workspaces.Agent.Gui.ViewModels;

namespace Phantom.Workspaces.Agent.Gui;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        this.DataContext = viewModel;
        this.InitializeComponent();
    }
}
