using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using System.Reflection.Metadata;

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
        if (this.hasAppliedInitialOutputScroll)
        {
            return;
        }

        this.hasAppliedInitialOutputScroll = true;
        this.ScrollHistoryToBottom();

        bool hasPendingChange = false;
        this.HistoryDocument.Document.EnsureTextDocument().Changed += (s, e) =>
        {
            if (!hasPendingChange)
            {
                hasPendingChange = true;
                Task.Factory.StartNew(async () =>
                {
                    await Task.Delay(100);
                    var document = this.HistoryDocument.Document;
                    this.HistoryDocument.Document = null;
                    this.HistoryDocument.Document = document;
                    hasPendingChange = false;
                },
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.FromCurrentSynchronizationContext());
            }
        };
            
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
