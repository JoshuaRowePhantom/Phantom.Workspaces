using System.Collections.Specialized;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phantom.Workspaces.Agent.Gui.ViewModels;

namespace Phantom.Workspaces.Agent.Gui.Controls;

public partial class AgentChatInputQueueControl : UserControl
{
    private InputQueueViewModel? viewModel;

    public AgentChatInputQueueControl()
    {
        this.InitializeComponent();
        this.DataContextChanged += this.OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (this.viewModel is not null)
        {
            this.viewModel.Queues.CollectionChanged -= this.OnQueuesCollectionChanged;
            this.UnwireEditHandlers(this.viewModel);
        }

        this.viewModel = this.DataContext as InputQueueViewModel;
        if (this.viewModel is null)
        {
            return;
        }

        this.viewModel.Queues.CollectionChanged += this.OnQueuesCollectionChanged;
        this.WireEditHandlers(this.viewModel);
    }

    private void OnQueuesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (this.viewModel is null)
        {
            return;
        }

        this.UnwireEditHandlers(this.viewModel);
        this.WireEditHandlers(this.viewModel);
    }

    private void OnQueueItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (this.viewModel is null)
        {
            return;
        }

        this.UnwireEditHandlers(this.viewModel);
        this.WireEditHandlers(this.viewModel);
    }

    private void OnItemEditStarted(object? sender, EventArgs e)
    {
        if (sender is not InputQueueEntryViewModel item)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => this.FocusEditBox(item));
    }

    private void WireEditHandlers(InputQueueViewModel vm)
    {
        foreach (var queue in vm.Queues)
        {
            queue.Items.CollectionChanged += this.OnQueueItemsCollectionChanged;
            foreach (var item in queue.Items)
            {
                item.EditStarted += this.OnItemEditStarted;
            }
        }
    }

    private void UnwireEditHandlers(InputQueueViewModel vm)
    {
        foreach (var queue in vm.Queues)
        {
            queue.Items.CollectionChanged -= this.OnQueueItemsCollectionChanged;
            foreach (var item in queue.Items)
            {
                item.EditStarted -= this.OnItemEditStarted;
            }
        }
    }

    private void FocusEditBox(InputQueueEntryViewModel item)
    {
        var editBox = this.GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(box => ReferenceEquals(box.DataContext, item));

        if (editBox is null)
        {
            return;
        }

        editBox.Focus();
        editBox.SelectAll();
    }

    private async void OnQueueAttachmentImageClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not InputQueueEntryAttachmentViewModel attachment || attachment.Preview is null)
        {
            return;
        }

        await ImagePreviewPresenter.ShowAsync(this, attachment.Preview, attachment.Label);
    }
}
