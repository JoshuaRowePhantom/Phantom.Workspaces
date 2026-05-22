using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class InputQueueGroupViewModel : ViewModelBase
{
    private readonly InputQueueViewModel parent;
    private readonly AgentChatQueue queue;
    private readonly RelayCommand toggleComposerCommand;
    private bool isComposerVisible;

    public InputQueueGroupViewModel(InputQueueViewModel parent, AgentChatQueue queue, QueueComposerViewModel composer)
    {
        this.parent = parent;
        this.queue = queue;
        this.Composer = composer;
        this.Items = [];
        this.toggleComposerCommand = new RelayCommand(this.ToggleComposer);
        this.Refresh();
    }

    public QueueComposerViewModel Composer { get; }

    public string? Name => this.ShowName ? this.queue.Name : null;

    public bool ShowName => this.parent.HasMultipleQueues || !this.queue.IsDefault;

    public bool IsDefault => this.queue.IsDefault;

    public bool IsHeld => this.queue.IsHeld;

    public bool IsImmediate => !this.queue.IsHeld && this.queue.Immediacy == AgentInputQueueImmediacy.Immediate;

    public bool IsQueued => !this.queue.IsHeld && this.queue.Immediacy == AgentInputQueueImmediacy.Queue;

    public bool CanToggleComposer => !this.IsDefault;

    public int ItemCount => this.Items.Count;

    public string ItemCountText => this.ItemCount == 1 ? "1 item" : $"{this.ItemCount} items";

    public bool HasItems => this.ItemCount > 0;

    public bool HasNoItems => !this.HasItems;

    public ObservableCollection<InputQueueEntryViewModel> Items { get; }

    public ICommand ToggleComposerCommand => this.toggleComposerCommand;

    public bool IsComposerVisible
    {
        get => this.isComposerVisible;
        private set
        {
            if (this.SetProperty(ref this.isComposerVisible, value))
            {
                this.RaisePropertyChanged(nameof(this.HasComposer));
            }
        }
    }

    public bool HasComposer => this.IsDefault || this.IsComposerVisible;

    public void ToggleComposer()
    {
        this.IsComposerVisible = !this.IsComposerVisible;
    }

    public void HideComposer()
    {
        if (!this.IsDefault)
        {
            this.IsComposerVisible = false;
        }
    }

    public QueueImmediacyOption SelectedImmediacyOption
    {
        get => QueueImmediacyOption.All.First(option => option.Value == this.queue.Immediacy);
        set
        {
            if (value.Value != this.queue.Immediacy)
            {
                this.parent.SetQueueImmediacy(this.queue, value.Value);
                this.RaisePropertyChanged(nameof(this.SelectedImmediacyOption));
                this.RaisePropertyChanged(nameof(this.IsHeld));
                this.RaisePropertyChanged(nameof(this.IsImmediate));
                this.RaisePropertyChanged(nameof(this.IsQueued));
            }
        }
    }

    public IReadOnlyList<QueueImmediacyOption> ImmediacyOptions => QueueImmediacyOption.All;

    public void Refresh()
    {
        if (this.Items.Count > 0)
        {
            this.Items.Clear();
        }

        var queueItems = this.queue.Items;
        for (var index = 0; index < queueItems.Count; index++)
        {
            var message = queueItems[index];
            var text = message.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                text = "(empty)";
            }

            this.Items.Add(new InputQueueEntryViewModel(this.parent, this.queue, index, text));
        }

        this.RaisePropertyChanged(nameof(this.ItemCount));
        this.RaisePropertyChanged(nameof(this.ItemCountText));
        this.RaisePropertyChanged(nameof(this.HasItems));
        this.RaisePropertyChanged(nameof(this.HasNoItems));
        this.RaisePropertyChanged(nameof(this.IsHeld));
        this.RaisePropertyChanged(nameof(this.IsImmediate));
        this.RaisePropertyChanged(nameof(this.IsQueued));
        this.RaisePropertyChanged(nameof(this.SelectedImmediacyOption));
        this.RaisePropertyChanged(nameof(this.HasComposer));
        this.RaisePropertyChanged(nameof(this.CanToggleComposer));
    }
}
