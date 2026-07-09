namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class DetailContentSlot : ViewModelBase
{
    private bool isVisible;

    public DetailContentSlot(object content)
    {
        this.Content = content;
    }

    public object Content { get; }

    public bool IsVisible
    {
        get => this.isVisible;
        set => this.SetProperty(ref this.isVisible, value);
    }
}
