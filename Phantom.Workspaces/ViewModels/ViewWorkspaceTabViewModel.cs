using System.Threading.Tasks;

namespace Phantom.Workspaces.ViewModels;

public sealed class ViewWorkspaceTabViewModel : WorkspaceTabViewModel
{
    private string? searchText;
    private bool hideUnmatched;

    public ViewWorkspaceTabViewModel(MainWindowViewModel owner, ViewDefinitionViewModel definition)
    {
        this.Definition = definition;
        this.EditDefinitionCommand = new RelayCommand(async _ => await owner.OpenViewDefinitionAsync(this));
    }

    public ViewDefinitionViewModel Definition { get; }

    public ViewPopulationViewModel Population { get; } = new();

    public RelayCommand EditDefinitionCommand { get; }

    public string? SearchText
    {
        get => this.searchText;
        set
        {
            if (this.SetProperty(ref this.searchText, value))
            {
                this.Population.ApplyFind(value, this.hideUnmatched);
            }
        }
    }

    public bool HideUnmatched
    {
        get => this.hideUnmatched;
        set
        {
            if (this.SetProperty(ref this.hideUnmatched, value))
            {
                this.Population.ApplyFind(this.searchText, value);
            }
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await this.Population.DisposeAsync();
        await base.DisposeAsync();
    }
}
