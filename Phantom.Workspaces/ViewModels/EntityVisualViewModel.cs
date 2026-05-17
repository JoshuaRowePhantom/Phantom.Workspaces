using System.Collections.ObjectModel;
using Avalonia;

namespace Phantom.Workspaces.ViewModels;

public sealed class EntityVisualViewModel : ViewModelBase
{
    private bool isChildrenExpanded;

    public required string EntityId { get; init; }

    public required string DisplayName { get; init; }

    public required string EntityType { get; init; }

    public int IndentLevel { get; init; }

    public bool IsParentContext { get; init; }

    public ObservableCollection<string> Badges { get; } = new();

    public ObservableCollection<string> Shortcuts { get; } = new();

    public ObservableCollection<string> DisplayItems { get; } = new();

    public ObservableCollection<EntityVisualViewModel> Children { get; } = new();

    public ObservableCollection<EntityVisualViewModel> VisibleChildren { get; } = new();

    public RelayCommand ToggleChildrenCommand { get; }

    public EntityVisualViewModel()
    {
        this.ToggleChildrenCommand = new RelayCommand(
            _ =>
            {
                this.IsChildrenExpanded = !this.IsChildrenExpanded;
            },
            _ => this.HasChildren);
    }

    public bool HasChildren => this.Children.Count > 0;

    public bool IsChildrenExpanded
    {
        get => this.isChildrenExpanded;
        set
        {
            if (!this.SetProperty(ref this.isChildrenExpanded, value))
            {
                return;
            }

            this.VisibleChildren.Clear();
            if (value)
            {
                foreach (var child in this.Children)
                {
                    this.VisibleChildren.Add(child);
                }
            }

            this.RaisePropertyChanged(nameof(this.ChildrenButtonText));
        }
    }

    public string ChildrenButtonText => this.IsChildrenExpanded ? "Hide children" : "Show children";

    public Thickness IndentMargin => new(this.IndentLevel * 20, 0, 0, 0);
}
