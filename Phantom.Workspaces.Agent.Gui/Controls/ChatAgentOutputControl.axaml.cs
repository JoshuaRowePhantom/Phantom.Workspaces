using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
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
}
