using System;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

public sealed class OpenAssociatedWorkspaceShortcutHandler : ShortcutHandler
{
    public override bool ShouldApplyTo(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        return shortcut == Shortcut.OpenWorkspace
            && !entityViewModel.IsEntityType("workspace")
            && GetRelatedWorkspaceIds(mainWindowViewModel, entityViewModel).Count > 0;
    }

    public override async Task<bool> Handle(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        if (shortcut != Shortcut.OpenWorkspace)
        {
            return false;
        }

        var workspaceIds = GetRelatedWorkspaceIds(mainWindowViewModel, entityViewModel);
        if (workspaceIds.Count == 0)
        {
            return false;
        }

        var openWorkspaceIds = mainWindowViewModel.WorkspacePanes
            .Select(pane => pane.Entity.EntityId)
            .ToHashSet();
        var targetId = workspaceIds.FirstOrDefault(openWorkspaceIds.Contains);
        if (targetId == default)
        {
            targetId = workspaceIds[0];
        }

        await mainWindowViewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = targetId });
        return true;
    }

    private static IReadOnlyList<EntityId> GetRelatedWorkspaceIds(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel entityViewModel)
    {
        var result = new List<EntityId>();
        var seen = new HashSet<EntityId>();
        foreach (var relationship in entityViewModel.Relationships)
        {
            if (!EntityPresentation.IsEntityType(relationship, "related")
                || relationship.Data is not { } relationshipData
                || !RelationshipParticipantIdExtractor.TryGetRelationshipParticipantIds(relationshipData, out var participantIds))
            {
                continue;
            }

            foreach (var participantId in participantIds)
            {
                if (participantId == entityViewModel.EntityId
                    || !seen.Add(participantId)
                    || !mainWindowViewModel.EntityBroker.TryGetEntity(participantId, out var participant)
                    || participant?.IsEntityType("workspace") != true)
                {
                    continue;
                }

                result.Add(participantId);
            }
        }

        return result;
    }
}
