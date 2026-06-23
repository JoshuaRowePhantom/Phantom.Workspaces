using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

public sealed class EntityWorkspaceTabViewModel : WorkspaceTabViewModel
{
    private readonly EntityCardViewResolver entityCardViewResolver = new();
    private readonly FieldEditorFactory? fieldEditorFactory;
    private EntityListNodeViewModel? entityCardNode;

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

            var nameComponents = ResolveNameComponents(this.Entity);
            this.entityCardNode = new EntityListNodeViewModel(
                this.Entity,
                nameComponents,
                JsonSerializer.Serialize(nameComponents),
                cardViewName: this.entityCardViewResolver.ResolveViewName(this.Entity),
                fieldEditorFactory: this.fieldEditorFactory);
            return this.entityCardNode;
        }
        set
        {
            this.SetProperty(ref this.entityCardNode, value);
        }
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
