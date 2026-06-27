using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

public sealed class CloneRelationshipSelectionItemViewModel : ViewModelBase
{
    private bool isSelected = true;

    public CloneRelationshipSelectionItemViewModel(EntitySnapshot relationship)
    {
        this.Relationship = relationship;
    }

    public EntitySnapshot Relationship { get; }

    public string RelationshipId => this.Relationship.EntityId.ToString();

    public bool IsSelected
    {
        get => this.isSelected;
        set => this.SetProperty(ref this.isSelected, value);
    }
}
