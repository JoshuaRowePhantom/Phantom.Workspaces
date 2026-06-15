using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phantom.Workspaces.Agent.Gui.ViewModels;

namespace Phantom.Workspaces.Agent.Gui.Controls;

public partial class AgentChatOutputControl : UserControl
{
    public static readonly StyledProperty<AgentChatOutputMode> OutputModeProperty =
        AvaloniaProperty.Register<AgentChatOutputControl, AgentChatOutputMode>(
            nameof(OutputMode),
            AgentChatOutputMode.FlowDocument);

    private bool hasAppliedInitialOutputScroll;
    private bool hasAppliedInitialSelectableOutputScroll;
    private bool selectableOutputPinnedToBottom = true;
    private Span? selectableOutputRootSpan;
    private AgentViewModel? subscribedViewModel;

    public AgentChatOutputControl()
    {
        this.InitializeComponent();
        this.Loaded += this.OnLoaded;
        this.Unloaded += this.OnUnloaded;
        this.SelectableOutputScrollViewer.ScrollChanged += this.OnSelectableOutputScrollChanged;
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

        if (change.Property == DataContextProperty)
        {
            this.AttachSelectableInlineRootSpan();
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        this.AttachSelectableInlineRootSpan();
        this.ApplyOutputModeVisibility();
        if (this.OutputMode == AgentChatOutputMode.SelectableTextBox
            && !this.hasAppliedInitialSelectableOutputScroll)
        {
            this.hasAppliedInitialSelectableOutputScroll = true;
            this.ScheduleSelectableOutputScrollToBottom();
        }

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

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        this.DetachSelectableOutputSubscription();
    }

    private void ScrollHistoryToBottom()
    {
        var viewer = this.HistoryDocument.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (viewer is null)
        {
            return;
        }

        var maxVerticalOffset = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
        viewer.Offset = new Avalonia.Vector(viewer.Offset.X, maxVerticalOffset);
    }

    private void OnSelectableOutputContentChanged(object? sender, EventArgs e)
    {
        if (this.OutputMode != AgentChatOutputMode.SelectableTextBox)
        {
            return;
        }

        if (!this.selectableOutputPinnedToBottom)
        {
            return;
        }

        this.ScheduleSelectableOutputScrollToBottom();
    }

    private void OnSelectableOutputScrollChanged(
        object? sender,
        ScrollChangedEventArgs e)
    {
        var maxVerticalOffset = Math.Max(
            0,
            this.SelectableOutputScrollViewer.Extent.Height - this.SelectableOutputScrollViewer.Viewport.Height);
        this.selectableOutputPinnedToBottom = maxVerticalOffset <= 0
            || this.SelectableOutputScrollViewer.Offset.Y >= maxVerticalOffset - 1;
    }

    private void ScheduleSelectableOutputScrollToBottom()
    {
        Dispatcher.UIThread.Post(
            this.ScrollSelectableOutputToBottom,
            DispatcherPriority.Background);
    }

    private void ScrollSelectableOutputToBottom()
    {
        var maxVerticalOffset = Math.Max(
            0,
            this.SelectableOutputScrollViewer.Extent.Height - this.SelectableOutputScrollViewer.Viewport.Height);
        this.SelectableOutputScrollViewer.Offset = new Avalonia.Vector(
            this.SelectableOutputScrollViewer.Offset.X,
            maxVerticalOffset);
    }

    private void ApplyOutputModeVisibility()
    {
        var isFlowDocument = this.OutputMode == AgentChatOutputMode.FlowDocument;
        this.HistoryDocument.IsVisible = isFlowDocument;
        this.SelectableOutputContainer.IsVisible = !isFlowDocument;

        if (!isFlowDocument)
        {
            this.ScheduleSelectableOutputScrollToBottom();
        }
    }

    private void AttachSelectableInlineRootSpan()
    {
        var selectableOutputText = this.SelectableOutputText;
        if (selectableOutputText is null)
        {
            return;
        }

        if (this.DataContext is not AgentViewModel agentViewModel)
        {
            selectableOutputText.Inlines.Clear();
            this.selectableOutputRootSpan = null;
            this.DetachSelectableOutputSubscription();
            return;
        }

        this.AttachSelectableOutputSubscription(agentViewModel);

        var rootSpan = agentViewModel.OutputSelectableRootSpan;
        if (ReferenceEquals(this.selectableOutputRootSpan, rootSpan))
        {
            return;
        }

        selectableOutputText.Inlines.Clear();
        selectableOutputText.Inlines.Add(rootSpan);
        this.selectableOutputRootSpan = rootSpan;
    }

    private void AttachSelectableOutputSubscription(AgentViewModel agentViewModel)
    {
        if (ReferenceEquals(this.subscribedViewModel, agentViewModel))
        {
            return;
        }

        this.DetachSelectableOutputSubscription();
        this.subscribedViewModel = agentViewModel;
        agentViewModel.SelectableOutputContentChanged += this.OnSelectableOutputContentChanged;
    }

    private void DetachSelectableOutputSubscription()
    {
        if (this.subscribedViewModel is null)
        {
            return;
        }

        this.subscribedViewModel.SelectableOutputContentChanged -= this.OnSelectableOutputContentChanged;
        this.subscribedViewModel = null;
    }
}
