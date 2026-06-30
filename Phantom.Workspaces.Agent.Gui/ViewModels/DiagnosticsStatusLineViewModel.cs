namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class DiagnosticsStatusLineViewModel : ViewModelBase
{
    private int errorCount;
    private int warningCount;

    public int ErrorCount
    {
        get => this.errorCount;
        set
        {
            if (this.SetProperty(ref this.errorCount, value))
            {
                this.RaisePropertyChanged(nameof(this.HasErrors));
                this.RaisePropertyChanged(nameof(this.HasVisibleContent));
            }
        }
    }

    public int WarningCount
    {
        get => this.warningCount;
        set
        {
            if (this.SetProperty(ref this.warningCount, value))
            {
                this.RaisePropertyChanged(nameof(this.HasWarnings));
                this.RaisePropertyChanged(nameof(this.HasVisibleContent));
            }
        }
    }

    public bool HasErrors => this.errorCount > 0;

    public bool HasWarnings => this.warningCount > 0;

    public bool HasVisibleContent => this.HasErrors || this.HasWarnings;
}
