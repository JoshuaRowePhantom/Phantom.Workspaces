using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

public sealed class StartShellOnProfileShortcutHandler : ShortcutHandler
{
    public override bool ShouldApplyTo(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        return shortcut == Shortcut.StartShell
            && entityViewModel.IsEntityType("user-computer-profile");
    }

    public override async Task<bool> Handle(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        // Get the user-computer-profile names to construct shell name
        if (entityViewModel.Data is not JsonElement profileData
            || !profileData.TryGetProperty("names", out var namesElement)
            || namesElement.ValueKind != JsonValueKind.Array
            || namesElement.GetArrayLength() == 0)
        {
            return false;
        }

        // Parse the first name to get components
        var firstNameElement = namesElement[0];
        if (firstNameElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var nameComponents = new System.Collections.Generic.List<string>();
        foreach (var component in firstNameElement.EnumerateArray())
        {
            if (component.ValueKind == JsonValueKind.String)
            {
                nameComponents.Add(component.GetString() ?? string.Empty);
            }
        }

        // Create shell entity under the user-computer-profile
        var shellEntity = await this.CreateShellEntityAsync(
            mainWindowViewModel,
            entityViewModel,
            nameComponents.ToArray());

        if (shellEntity is null)
        {
            return false;
        }

        // TODO: Start shell on the host via PTY
        // TODO: Show PTY view
        // For now, just open the shell entity
        return await new OpenEntityShortcutHandler().Handle(mainWindowViewModel, Shortcut.Open, shellEntity);
    }

    private async Task<SubscribedEntityViewModel?> CreateShellEntityAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel hostProfileEntity,
        string[] profileNameComponents)
    {
        var workspaceEntitySession = mainWindowViewModel.EntityBroker.EntityRepository.WorkspaceEntitySession;
        
        // Create shell entity name using the default naming pattern for shells
        var shellNames = await WorkspaceEntityNameFactory.CreateEntityNames(
            mainWindowViewModel.EntityBroker.EntityRepository.DataAccessLayer,
            workspaceEntitySession,
            new EntityTypeName("shell"),
            "default-shell");

        var shellEntityData = CreateShellEntityData(
            hostProfileEntity.EntityId,
            shellNames);

        var createShellResult = await mainWindowViewModel.EntityBroker.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = $"Create default shell for {hostProfileEntity.DisplayName}.",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        Data = shellEntityData,
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            });

        var createShellEntityResult = createShellResult.EntityResults
            .FirstOrDefault(entityResult => entityResult.UpdateState != UpdateState.Failed && entityResult.CurrentEntity is not null);

        if (createShellEntityResult?.CurrentEntity is not EntitySnapshot createdShellSnapshot)
        {
            return null;
        }

        // Create owned-by relationship from profile to shell
        await this.CreateOwnershipRelationshipAsync(
            mainWindowViewModel,
            hostProfileEntity.EntityId,
            createdShellSnapshot.EntityId);

        var createdShellEntities = await mainWindowViewModel.EntityBroker.GetEntitiesAsync([createdShellSnapshot.EntityId]);
        return createdShellEntities.FirstOrDefault();
    }

    private static JsonElement CreateShellEntityData(
        EntityId hostProfileEntityId,
        System.Collections.Generic.IReadOnlyCollection<EntityName> shellNames)
    {
        var entityId = new EntityId();
        var namesJson = string.Join(
            ", ",
            shellNames.Select(
                static entityName => $"[{string.Join(", ", entityName.Components.Select(static component => JsonSerializer.Serialize(component)))}]"));

        // Default to PowerShell on Windows, bash otherwise
        var defaultCommand = System.OperatingSystem.IsWindows() ? "pwsh" : "bash";
        var defaultArgs = System.OperatingSystem.IsWindows() ? "-NoLogo" : "-i";

        using var shellDocument = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "shell"],
              "names": [{{namesJson}}],
              "display-name": { "default": "Default Shell" },
              "command": "{{defaultCommand}}",
              "command-arguments": ["{{defaultArgs}}"]
            }
            """);
        return shellDocument.RootElement.Clone();
    }

    private async Task CreateOwnershipRelationshipAsync(
        MainWindowViewModel mainWindowViewModel,
        EntityId ownerEntityId,
        EntityId targetEntityId)
    {
        var relationshipEntityId = new EntityId();
        using var relationshipDocument = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{relationshipEntityId}}",
              "entity-types": ["entity", "relationship", "owned-by"],
              "names": [["relationship", "{{relationshipEntityId}}"]],
              "participants": {
                "owner": "{{ownerEntityId}}",
                "target": "{{targetEntityId}}"
              },
              "note": {
                "default": {
                  "mime-type": "text/plain",
                  "text": "Shell owned by user-computer-profile"
                }
              }
            }
            """);

        await mainWindowViewModel.EntityBroker.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Create owned-by relationship for shell.",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        Data = relationshipDocument.RootElement.Clone(),
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            });
    }
}
