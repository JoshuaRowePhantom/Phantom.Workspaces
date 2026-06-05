using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phantom.Workspaces.Agent.Gui.ViewModels;

namespace Phantom.Workspaces.Agent.Gui.Controls;

public partial class AgentChatOutputControl : UserControl
{
    private bool hasAppliedInitialOutputScroll;

    public AgentChatOutputControl()
    {
        this.InitializeComponent();
        this.Loaded += this.OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (this.DataContext is AgentViewModel agent)
        {
            agent.RebuildOutputDocument();
        }

        if (this.hasAppliedInitialOutputScroll)
        {
            return;
        }

        this.hasAppliedInitialOutputScroll = true;
        this.ScrollHistoryToBottom();
    }

    private void ScrollHistoryToBottom()
    {
        var viewer = this.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (viewer is null)
        {
            return;
        }

        var maxVerticalOffset = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
        viewer.Offset = new Avalonia.Vector(viewer.Offset.X, maxVerticalOffset);
    }
}
