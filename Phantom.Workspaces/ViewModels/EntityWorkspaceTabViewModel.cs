using System;
using System.Collections.Generic;
using System.Text.Json;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

public sealed class EntityWorkspaceTabViewModel : WorkspaceTabViewModel
{
    private readonly EntityCardViewResolver entityCardViewResolver = new();
    private EntityListNodeViewModel? entityCardNode;

    public EntityListNodeViewModel? EntityCardNode
    {
        get
        {
            if (this.entityCardNode is not null || this.Entity is null)
            {
                return this.entityCardNode;
            }

            var nameComponents = ResolveNameComponents(this.Entity);
            var cardViewName = this.entityCardViewResolver.ResolveViewName(this.Entity);
            this.entityCardNode = new EntityListNodeViewModel(
                this.Entity,
                nameComponents,
                JsonSerializer.Serialize(nameComponents),
                cardViewName: cardViewName);
            return this.entityCardNode;
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
