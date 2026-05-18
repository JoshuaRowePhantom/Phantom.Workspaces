using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using Avalonia;

namespace Phantom.Workspaces.ViewModels;

public sealed class ViewEntityViewModel : ViewModelBase
{
    private readonly ObservableCollection<string> displayItems = [];

    public ViewEntityViewModel(
        SubscribedEntityViewModel entity,
        int indentLevel,
        bool isParentContext = false)
    {
        this.Entity = entity;
        this.Badges = new BadgesViewModel(entity.Badges);
        this.IndentLevel = indentLevel;
        this.IsParentContext = isParentContext;
        this.Entity.PropertyChanged += this.OnEntityPropertyChanged;
        this.RefreshCollections();
    }

    public SubscribedEntityViewModel Entity { get; }

    public string EntityId => this.Entity.EntityId.ToString();

    public string DisplayName => this.Entity.DisplayName;

    public string EntityType => this.Entity.EntityType;

    public int IndentLevel { get; }

    public bool IsParentContext { get; }

    public BadgesViewModel Badges { get; }

    public ObservableCollection<string> DisplayItems => this.displayItems;

    public Thickness IndentMargin => new(this.IndentLevel * 20, 0, 0, 0);

    private void OnEntityPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(SubscribedEntityViewModel.Snapshot), System.StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(SubscribedEntityViewModel.DisplayName), System.StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(SubscribedEntityViewModel.EntityType), System.StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.DisplayName));
            this.RaisePropertyChanged(nameof(this.EntityType));
            this.RefreshCollections();
        }
    }

    private void RefreshCollections()
    {
        this.displayItems.Clear();

        if (this.Entity.Snapshot.Data is not JsonElement)
        {
            return;
        }

        foreach (var displayItem in this.Entity.DisplayItems)
        {
            this.displayItems.Add(displayItem);
        }
    }
}
