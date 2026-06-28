using Avalonia.Controls;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Controls;

public partial class GitWorkspacesControl : UserControl
{
    public GitWorkspacesControl()
    {
        InitializeComponent();
    }

    public GitWorkspacesControl(GitWorkspacesViewModel viewModel)
        : this()
    {
        this.DataContext = viewModel;
    }
}
