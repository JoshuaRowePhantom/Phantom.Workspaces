using Avalonia.Media.Imaging;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class ChatHistoryImageViewModel : ViewModelBase, IDisposable
{
    public ChatHistoryImageViewModel(Bitmap? preview, string label)
    {
        this.Preview = preview;
        this.Label = label;
    }

    public Bitmap? Preview { get; }

    public bool HasPreview => this.Preview is not null;

    public string Label { get; }

    public void Dispose()
    {
        this.Preview?.Dispose();
    }
}
