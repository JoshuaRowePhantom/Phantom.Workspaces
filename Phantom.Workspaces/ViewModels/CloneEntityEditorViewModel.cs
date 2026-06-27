using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

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

        var cloneData = RewriteEntityId(entityData, this.CloneEntityId);
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

            var rewrittenRelData = RewriteRelationshipParticipantIds(relData, this.sourceEntity.EntityId, this.CloneEntityId);
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

    private static JsonElement RewriteEntityId(JsonElement entityData, EntityId newEntityId)
    {
        if (entityData.ValueKind != JsonValueKind.Object)
        {
            return entityData;
        }

        var node = JsonNode.Parse(entityData.GetRawText());
        if (node is JsonObject obj)
        {
            obj["entity-id"] = JsonValue.Create(newEntityId.ToString());
        }

        using var doc = JsonDocument.Parse(node!.ToJsonString());
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Rewrites all occurrences of <paramref name="sourceId"/> in the <c>participants</c>
    /// object of <paramref name="relationshipData"/> to <paramref name="cloneId"/>.
    /// Values outside the <c>participants</c> object are not rewritten.
    /// </summary>
    internal static JsonElement RewriteRelationshipParticipantIds(
        JsonElement relationshipData,
        EntityId sourceId,
        EntityId cloneId)
    {
        if (relationshipData.ValueKind != JsonValueKind.Object)
        {
            return relationshipData;
        }

        var node = JsonNode.Parse(relationshipData.GetRawText());
        if (node is JsonObject obj && obj["participants"] is JsonNode participantsNode)
        {
            RewriteIdsInNode(participantsNode, sourceId.ToString(), cloneId.ToString());
        }

        using var doc = JsonDocument.Parse(node!.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static void RewriteIdsInNode(JsonNode node, string sourceId, string cloneId)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(p => p.Key).ToArray())
            {
                var child = obj[key];
                if (child is JsonValue val && val.TryGetValue<string>(out var str) && str == sourceId)
                {
                    obj[key] = JsonValue.Create(cloneId);
                }
                else if (child is not null)
                {
                    RewriteIdsInNode(child, sourceId, cloneId);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                var child = arr[i];
                if (child is JsonValue val && val.TryGetValue<string>(out var str) && str == sourceId)
                {
                    arr[i] = JsonValue.Create(cloneId);
                }
                else if (child is not null)
                {
                    RewriteIdsInNode(child, sourceId, cloneId);
                }
            }
        }
    }
}
