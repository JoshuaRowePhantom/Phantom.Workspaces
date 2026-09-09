using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class InputQueueGroupViewModel : ViewModelBase, IDisposable, IQueueImmediacyViewModel
{
    private readonly InputQueueViewModel parent;
    private readonly string queueId;
    private readonly RelayCommand toggleComposerCommand;
    private readonly RelayCommand removeQueueCommand;
    private readonly RelayCommand<QueueImmediacyOption> setImmediacyCommand;
    private readonly object itemsLock = new();
    private bool isComposerVisible;

    public InputQueueGroupViewModel(InputQueueViewModel parent, string queueId, QueueComposerViewModel composer)
    {
        this.parent = parent;
        this.queueId = queueId;
        this.Composer = composer;
        this.Composer.HideOwnerComposerAction = this.HideComposer;
        this.Items = [];
        this.toggleComposerCommand = new RelayCommand(this.ToggleComposer);
        this.removeQueueCommand = new RelayCommand(this.RemoveQueue);
        this.setImmediacyCommand = new RelayCommand<QueueImmediacyOption>(this.SetImmediacy);
        this.Refresh();
    }

    public QueueComposerViewModel Composer { get; }

    internal string QueueId => this.queueId;

    public string? Name => this.ShowName ? this.QueueSnapshot.Name : null;

    public bool ShowName => this.parent.HasMultipleQueues || !this.QueueSnapshot.IsDefault;

    public bool IsDefault => this.QueueSnapshot.IsDefault;

    public bool IsHeld => this.QueueSnapshot.Immediacy == AgentInputQueueImmediacy.Held;

    public bool IsImmediate => this.QueueSnapshot.Immediacy == AgentInputQueueImmediacy.Immediate;

    public bool IsQueued => this.QueueSnapshot.Immediacy == AgentInputQueueImmediacy.Queue;

    public bool CanToggleComposer => !this.IsDefault;

    public bool CanRemoveQueue => !this.IsDefault;

    public int ItemCount => this.Items.Count;

    public string ItemCountText => this.ItemCount == 1 ? "1 item" : $"{this.ItemCount} items";

    public bool HasItems => this.ItemCount > 0;

    public ObservableCollection<InputQueueEntryViewModel> Items { get; }

    public ICommand ToggleComposerCommand => this.toggleComposerCommand;

    public ICommand RemoveQueueCommand => this.removeQueueCommand;

    public ICommand SetImmediacyCommand => this.setImmediacyCommand;

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
        get => QueueImmediacyOption.All.First(option => option.Value == this.QueueSnapshot.Immediacy);
        set
        {
            if (value.Value != this.QueueSnapshot.Immediacy)
            {
                this.parent.SetQueueImmediacy(this.queueId, value.Value);
                this.RaisePropertyChanged(nameof(this.SelectedImmediacyOption));
                this.RaisePropertyChanged(nameof(this.IsHeld));
                this.RaisePropertyChanged(nameof(this.IsImmediate));
                this.RaisePropertyChanged(nameof(this.IsQueued));
            }
        }
    }

    public IReadOnlyList<QueueImmediacyOption> ImmediacyOptions => QueueImmediacyOption.All;

    public QueueImmediacyOption ImmediateImmediacyOption => QueueImmediacyOption.All[0];

    public QueueImmediacyOption QueuedImmediacyOption => QueueImmediacyOption.All[1];

    public QueueImmediacyOption HeldImmediacyOption => QueueImmediacyOption.All[2];

    public void SetImmediacy(QueueImmediacyOption option) => this.SelectedImmediacyOption = option;

    private void RemoveQueue()
    {
        if (this.IsDefault)
        {
            return;
        }

        this.parent.RemoveInputQueue(this.queueId);
    }

    public void Refresh()
    {
        var queueSnapshot = this.QueueSnapshot;

        lock (this.itemsLock)
        {
            var itemsById = this.Items.ToDictionary(static item => item.ItemId, StringComparer.Ordinal);
            for (var i = this.Items.Count - 1; i >= 0; i--)
            {
                if (!queueSnapshot.Items.Any(item => string.Equals(item.ItemId, this.Items[i].ItemId, StringComparison.Ordinal)))
                {
                    this.Items.RemoveAt(i);
                }
            }

            for (var index = 0; index < queueSnapshot.Items.Length; index++)
            {
                var itemSnapshot = queueSnapshot.Items[index];
                if (!itemsById.TryGetValue(itemSnapshot.ItemId, out var itemViewModel))
                {
                    itemViewModel = new InputQueueEntryViewModel(this.parent, this.queueId, itemSnapshot);
                    this.Items.Insert(index, itemViewModel);
                    continue;
                }

                itemViewModel.Refresh(itemSnapshot);
                var currentIndex = this.Items.IndexOf(itemViewModel);
                if (currentIndex >= 0 && currentIndex != index)
                {
                    this.Items.Move(currentIndex, index);
                }
            }
        }

        this.Composer.RefreshQueueState();
        this.RaisePropertyChanged(nameof(this.ItemCount));
        this.RaisePropertyChanged(nameof(this.ItemCountText));
        this.RaisePropertyChanged(nameof(this.HasItems));
        this.RaisePropertyChanged(nameof(this.Name));
        this.RaisePropertyChanged(nameof(this.ShowName));
        this.RaisePropertyChanged(nameof(this.IsHeld));
        this.RaisePropertyChanged(nameof(this.IsImmediate));
        this.RaisePropertyChanged(nameof(this.IsQueued));
        this.RaisePropertyChanged(nameof(this.SelectedImmediacyOption));
        this.RaisePropertyChanged(nameof(this.HasComposer));
        this.RaisePropertyChanged(nameof(this.CanToggleComposer));
        this.RaisePropertyChanged(nameof(this.CanRemoveQueue));
    }

    public void Dispose()
    {
        if (!this.IsDefault)
        {
            this.Composer.Dispose();
        }
    }

    private AgentInputQueueSnapshot QueueSnapshot
        => this.parent.TryGetQueueSnapshot(this.queueId, out var snapshot)
            ? snapshot
            : throw new InvalidOperationException($"Queue '{this.queueId}' is no longer available.");
}
