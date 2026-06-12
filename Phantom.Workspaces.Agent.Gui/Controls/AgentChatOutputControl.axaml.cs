using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.VisualTree;

namespace Phantom.Workspaces.Agent.Gui.Controls;

public partial class AgentChatOutputControl : UserControl
{
    public static readonly StyledProperty<AgentChatOutputMode> OutputModeProperty =
        AvaloniaProperty.Register<AgentChatOutputControl, AgentChatOutputMode>(
            nameof(OutputMode),
            AgentChatOutputMode.FlowDocument);

    private bool hasAppliedInitialOutputScroll;

    public AgentChatOutputControl()
    {
        this.InitializeComponent();
        this.Loaded += this.OnLoaded;
        this.ApplyOutputModeVisibility();
    }

    public AgentChatOutputMode OutputMode
    {
        get => this.GetValue(OutputModeProperty);
        set => this.SetValue(OutputModeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == OutputModeProperty)
        {
            this.ApplyOutputModeVisibility();
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        this.ApplyOutputModeVisibility();
        if (this.OutputMode != AgentChatOutputMode.FlowDocument)
        {
            return;
        }

        if (this.hasAppliedInitialOutputScroll)
        {
            return;
        }

        this.hasAppliedInitialOutputScroll = true;
        this.ScrollHistoryToBottom();

        bool hasPendingChange = false;
        this.HistoryDocument.Document?.EnsureTextDocument().Changed += (s, e) =>
        {
            if (!hasPendingChange)
            {
                hasPendingChange = true;
                Task.Factory.StartNew(async () =>
                {
                    await Task.Delay(250);
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

    private void ApplyOutputModeVisibility()
    {
        var isFlowDocument = this.OutputMode == AgentChatOutputMode.FlowDocument;
        this.HistoryDocument.IsVisible = isFlowDocument;
        this.SelectableOutputText.IsVisible = !isFlowDocument;
    }
}
