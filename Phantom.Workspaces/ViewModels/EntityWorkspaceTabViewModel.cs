using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

public sealed class EntityWorkspaceTabViewModel : WorkspaceTabViewModel
{
    private readonly EntityCardViewResolver entityCardViewResolver = new();
    private readonly FieldEditorFactory? fieldEditorFactory;
    private EntityListNodeViewModel? entityCardNode;
    private Task<EntityListNodeViewModel?>? buildTask;

    public EntityWorkspaceTabViewModel(
        EntityBroker? entityBroker = null,
        EntityTypeViewCatalog? entityTypeViewCatalog = null)
    {
        if (entityBroker is not null && entityTypeViewCatalog is not null)
        {
            this.fieldEditorFactory = new FieldEditorFactory(entityBroker, entityTypeViewCatalog);
        }
    }

    public EntityListNodeViewModel? EntityCardNode
    {
        get
        {
            if (this.entityCardNode is not null || this.Entity is null)
            {
                return this.entityCardNode;
            }

            // Start building in background if not already started and we have a factory
            if (this.buildTask is null && this.fieldEditorFactory is not null)
            {
                this.buildTask = this.BuildEntityCardNodeAsync();
            }

            return null;
        }
        set
        {
            if (this.SetProperty(ref this.entityCardNode, value))
            {
                this.buildTask = null; // Cancel any pending build
            }
        }
    }

    private async Task<EntityListNodeViewModel?> BuildEntityCardNodeAsync()
    {
        if (this.Entity is null || this.fieldEditorFactory is null)
        {
            return null;
        }

        var nameComponents = ResolveNameComponents(this.Entity);
        var cardViewName = this.entityCardViewResolver.ResolveViewName(this.Entity);
        
        if (this.Entity.Data is not JsonElement entityData)
        {
            this.entityCardNode = new EntityListNodeViewModel(
                this.Entity,
                nameComponents,
                JsonSerializer.Serialize(nameComponents),
                fieldEditors: Array.Empty<EntityFieldEditorViewModel>(),
                cardViewName: cardViewName);
        }
        else
        {
            var fieldEditors = await this.fieldEditorFactory.BuildFieldEditorsAsync(
                entityData,
                this.Entity.EntityType).ConfigureAwait(false);

            this.entityCardNode = new EntityListNodeViewModel(
                this.Entity,
                nameComponents,
                JsonSerializer.Serialize(nameComponents),
                fieldEditors: fieldEditors,
                cardViewName: cardViewName);
        }

        this.RaisePropertyChanged(nameof(this.EntityCardNode));
        return this.entityCardNode;
    }

    private static IReadOnlyList<string> ResolveNameComponents(
        SubscribedEntityViewModel entity)
    {
        if (entity.Data is JsonElement entityData
            && EntityListNodeViewModel.TryGetPrimaryName(entityData, out var entityName))
        {
            return entityName.Components;
        }

        return [entity.EntityId.ToString()];
    }
}
