using System.Windows.Input;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class InputQueueEntryAttachmentViewModel : ViewModelBase
{
    public InputQueueEntryAttachmentViewModel(string label, ICommand removeCommand)
    {
        this.Label = label;
        this.RemoveCommand = removeCommand;
    }

    public string Label { get; }

    public ICommand RemoveCommand { get; }
}
