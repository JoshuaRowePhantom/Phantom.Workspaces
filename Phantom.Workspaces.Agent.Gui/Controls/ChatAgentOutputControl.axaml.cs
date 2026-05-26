using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using Phantom.Workspaces.Agent.Gui.ViewModels;

namespace Phantom.Workspaces.Agent.Gui.Controls;

public partial class ChatAgentOutputControl : UserControl
{
    private const double BottomStickTolerance = 8;
    private AgentViewModel? viewModel;
    private bool stickToBottom = true;
    private bool pendingAutoScroll;

    public ChatAgentOutputControl()
    {
        this.InitializeComponent();
        this.DataContextChanged += this.OnDataContextChanged;
        this.HistoryScroll.ScrollChanged += this.OnHistoryScrollChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (this.viewModel is not null)
        {
            this.viewModel.History.CollectionChanged -= this.OnHistoryChanged;
            this.viewModel.RunningItems.CollectionChanged -= this.OnRunningItemsChanged;
        }

        this.viewModel = this.DataContext as AgentViewModel;

        if (this.viewModel is not null)
        {
            this.viewModel.History.CollectionChanged += this.OnHistoryChanged;
            this.viewModel.RunningItems.CollectionChanged += this.OnRunningItemsChanged;
        }
    }

    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.ScrollToEndIfAnchored();
    }

    private void OnRunningItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.ScrollToEndIfAnchored();
    }

    private void ScrollToEndIfAnchored()
    {
        if (!this.stickToBottom)
        {
            return;
        }

        this.RequestAutoScroll();
    }

    private void RequestAutoScroll()
    {
        if (this.pendingAutoScroll)
        {
            return;
        }

        this.pendingAutoScroll = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                this.pendingAutoScroll = false;
                this.HistoryScroll.ScrollToEnd();
                this.stickToBottom = true;
            },
            DispatcherPriority.Background);
    }

    private void OnHistoryScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (this.stickToBottom && e.ExtentDelta.Y > 0)
        {
            this.RequestAutoScroll();
            return;
        }

        this.stickToBottom = this.IsNearBottom();
    }

    private bool IsNearBottom()
    {
        var maxOffsetY = Math.Max(
            0,
            this.HistoryScroll.Extent.Height - this.HistoryScroll.Viewport.Height);
        return this.HistoryScroll.Offset.Y >= maxOffsetY - BottomStickTolerance;
    }

    private async void OnHistoryImageClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not ChatHistoryImageViewModel image || image.Preview is null)
        {
            return;
        }

        await ImagePreviewPresenter.ShowAsync(this, image.Preview, image.Label);
    }
}
