namespace Phantom.Workspaces.ViewModels;

public sealed class StatusItem : ViewModelBase, IStatusItem
{
    private RunningStatus runningStatus;
    private ErrorStatus errorStatus;

    public RunningStatus RunningStatus
    {
        get => this.runningStatus;
        set => this.SetProperty(ref this.runningStatus, value);
    }

    public ErrorStatus ErrorStatus
    {
        get => this.errorStatus;
        set => this.SetProperty(ref this.errorStatus, value);
    }
}
