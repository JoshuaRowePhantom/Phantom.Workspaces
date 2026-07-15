using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Utilities;

namespace Phantom.Workspaces.ViewModels;

public sealed class CloneEntityEditorViewModel : ViewModelBase
{
    private readonly SubscribedEntityViewModel sourceEntity;
    private readonly MainWindowViewModel mainWindowViewModel;
    private readonly CloneEntityWorkspaceTabViewModel ownerTab;

    public CloneEntityEditorViewModel(
        SubscribedEntityViewModel sourceEntity,
        MainWindowViewModel mainWindowViewModel,
        CloneEntityWorkspaceTabViewModel ownerTab)
    {
        this.sourceEntity = sourceEntity;
        this.mainWindowViewModel = mainWindowViewModel;
        this.ownerTab = ownerTab;
        this.CloneEntityId = new EntityId();
        this.Relationships = new ObservableCollection<CloneRelationshipSelectionItemViewModel>(
            sourceEntity.Relationships.Select(r => new CloneRelationshipSelectionItemViewModel(r)));
        this.SaveCloneCommand = new RelayCommand(async _ => await this.SaveCloneAsync());
        this.CancelCommand = new RelayCommand(_ => mainWindowViewModel.CloseTab(ownerTab));
    }

    public EntityId CloneEntityId { get; }

    public ObservableCollection<CloneRelationshipSelectionItemViewModel> Relationships { get; }

    public RelayCommand SaveCloneCommand { get; }

    public RelayCommand CancelCommand { get; }

    private async Task SaveCloneAsync()
    {
        if (this.sourceEntity.Data is not JsonElement entityData)
        {
            return;
        }

        var changes = new List<EntityChange>();

        var cloneData = EntityCloneHelper.RewriteEntityId(entityData, this.CloneEntityId);
        changes.Add(new EntityChange
        {
            EntityId = this.CloneEntityId,
            EntityChangeMode = EntityChangeMode.Replace,
            Data = cloneData,
        });

        foreach (var selection in this.Relationships.Where(r => r.IsSelected))
        {
            if (selection.Relationship.Data is not JsonElement relData)
            {
                continue;
            }

            var rewrittenRelData = EntityCloneHelper.RewriteRelationshipParticipantIds(relData, this.sourceEntity.EntityId, this.CloneEntityId);
            changes.Add(new EntityChange
            {
                EntityChangeMode = EntityChangeMode.Replace,
                Data = rewrittenRelData,
            });
        }

        await this.mainWindowViewModel.EntityBroker.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown { Text = $"Clone entity {this.sourceEntity.DisplayName}." },
                },
                Changes = changes,
            });

        this.mainWindowViewModel.CloseTab(this.ownerTab);
    }

    internal static JsonElement RewriteEntityId(JsonElement entityData, EntityId newEntityId)
        => EntityCloneHelper.RewriteEntityId(entityData, newEntityId);

    /// <summary>
    /// Rewrites all occurrences of <paramref name="sourceId"/> in the <c>participants</c>
    /// object of <paramref name="relationshipData"/> to <paramref name="cloneId"/>.
    /// Values outside the <c>participants</c> object are not rewritten.
    /// </summary>
    internal static JsonElement RewriteRelationshipParticipantIds(
        JsonElement relationshipData,
        EntityId sourceId,
        EntityId cloneId)
        => EntityCloneHelper.RewriteRelationshipParticipantIds(relationshipData, sourceId, cloneId);
}
