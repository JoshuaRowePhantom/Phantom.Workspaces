using System.Windows.Input;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public interface IQueueImmediacyViewModel
{
    QueueImmediacyOption SelectedImmediacyOption { get; }
    ICommand SetImmediacyCommand { get; }
    QueueImmediacyOption ImmediateImmediacyOption { get; }
    QueueImmediacyOption QueuedImmediacyOption { get; }
    QueueImmediacyOption HeldImmediacyOption { get; }
}
