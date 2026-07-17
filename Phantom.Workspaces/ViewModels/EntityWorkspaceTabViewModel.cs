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
    private readonly MainWindowViewModel? mainWindowViewModel;
    private EntityListNodeViewModel? entityCardNode;

    public EntityWorkspaceTabViewModel(
        EntityBroker? entityBroker = null,
        EntityTypeViewCatalog? entityTypeViewCatalog = null,
        MainWindowViewModel? mainWindowViewModel = null)
    {
        this.mainWindowViewModel = mainWindowViewModel;
        if (entityBroker is not null && entityTypeViewCatalog is not null)
        {
            this.fieldEditorFactory = new FieldEditorFactory(
                entityBroker,
                entityTypeViewCatalog,
                entityReferenceSearch: new EntityReferenceSearch(entityBroker));
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

            // The card resolves its own shortcuts when given a shortcut context, so the single-entity
            // view shows action buttons without going through ViewEntityViewModel.InitializeAsync.
            if (this.mainWindowViewModel is { } owner)
            {
                this.entityCardNode.Card.SetShortcutContext(owner, owner.ShortcutManager);
            }

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
