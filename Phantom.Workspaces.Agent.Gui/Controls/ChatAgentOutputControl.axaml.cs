using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using Phantom.Workspaces.Agent.Gui.ViewModels;

namespace Phantom.Workspaces.Agent.Gui.Controls;

public partial class ChatAgentOutputControl : UserControl
{
    private AgentViewModel? viewModel;

    public ChatAgentOutputControl()
    {
        this.InitializeComponent();
        this.DataContextChanged += this.OnDataContextChanged;
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
        Dispatcher.UIThread.Post(() => this.HistoryScroll.ScrollToEnd());
    }

    private void OnRunningItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => this.HistoryScroll.ScrollToEnd());
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
