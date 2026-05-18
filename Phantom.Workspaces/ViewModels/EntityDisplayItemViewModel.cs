namespace Phantom.Workspaces.ViewModels;

public sealed class EntityDisplayItemViewModel
{
    public EntityDisplayItemViewModel(
        string text)
    {
        this.Text = text;
    }

    public string Text { get; }
}
