using System.Windows.Input;
using Avalonia.Media.Imaging;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class QueueComposerAttachmentViewModel : ViewModelBase, IDisposable
{
    public QueueComposerAttachmentViewModel(Bitmap? preview, string label, ICommand removeCommand)
    {
        this.Preview = preview;
        this.Label = label;
        this.RemoveCommand = removeCommand;
    }

    public Bitmap? Preview { get; }

    public string Label { get; }

    public ICommand RemoveCommand { get; }

    public void Dispose()
    {
        this.Preview?.Dispose();
    }
}
