namespace Phantom.Workspaces.ViewModels;

public sealed class LoadingWindowViewModel : ViewModelBase
{
    private string statusText = "Loading workspace data and initializing services.";

    public string StatusText
    {
        get => this.statusText;
        set => this.SetProperty(ref this.statusText, value);
    }
}
