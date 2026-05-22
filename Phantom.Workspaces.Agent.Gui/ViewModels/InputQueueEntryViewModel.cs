using System.Windows.Input;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class InputQueueEntryViewModel : ViewModelBase
{
    private readonly InputQueueViewModel parent;
    private readonly AgentChatQueue queue;
    private readonly int index;
    private bool isEditing;
    private string editText;

    public InputQueueEntryViewModel(
        InputQueueViewModel parent,
        AgentChatQueue queue,
        int index,
        string text)
    {
        this.parent = parent;
        this.queue = queue;
        this.index = index;
        this.Text = text;
        this.editText = text;
        this.RemoveCommand = new RelayCommand(this.Remove);
        this.EditCommand = new RelayCommand(this.BeginEdit);
        this.SaveEditCommand = new RelayCommand(this.SaveEdit);
        this.CancelEditCommand = new RelayCommand(this.CancelEdit);
    }

    public string Text { get; private set; }

    public bool IsEditing
    {
        get => this.isEditing;
        private set
        {
            if (this.SetProperty(ref this.isEditing, value))
            {
                this.RaisePropertyChanged(nameof(this.IsNotEditing));
            }
        }
    }

    public bool IsNotEditing => !this.IsEditing;

    public string EditText
    {
        get => this.editText;
        set => this.SetProperty(ref this.editText, value);
    }

    public ICommand RemoveCommand { get; }

    public ICommand EditCommand { get; }

    public ICommand SaveEditCommand { get; }

    public ICommand CancelEditCommand { get; }

    public void RefreshText(string text)
    {
        this.Text = text;
        if (!this.IsEditing)
        {
            this.EditText = text;
        }
        this.RaisePropertyChanged(nameof(this.Text));
    }

    private void Remove() => this.parent.RemoveQueueItem(this.queue, this.index);

    private void BeginEdit()
    {
        this.EditText = this.Text;
        this.IsEditing = true;
    }

    private void SaveEdit()
    {
        this.parent.UpdateQueueItem(this.queue, this.index, this.EditText);
        this.IsEditing = false;
    }

    private void CancelEdit()
    {
        this.EditText = this.Text;
        this.IsEditing = false;
    }
}
