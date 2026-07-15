using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

public sealed class StartAgentSessionFromEntityShortcutHandler : ShortcutHandler
{
    private readonly AgentSessionShortcutContext agentSessionShortcutContext;
    private readonly OpenAgentSessionShortcutHandler openAgentSessionShortcutHandler;

    public StartAgentSessionFromEntityShortcutHandler(
        AgentSessionShortcutContext agentSessionShortcutContext,
        OpenAgentSessionShortcutHandler openAgentSessionShortcutHandler)
    {
        this.agentSessionShortcutContext = agentSessionShortcutContext;
        this.openAgentSessionShortcutHandler = openAgentSessionShortcutHandler;
    }

    public override ValueTask<bool> ShouldApplyTo(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        if (shortcut != Shortcut.StartAgentSession)
        {
            return ValueTask.FromResult(false);
        }

        if (entityViewModel.Data is not JsonElement data)
        {
            return ValueTask.FromResult(false);
        }

        return ValueTask.FromResult(data.TryGetProperty("path", out _) || data.TryGetProperty("home-directory", out _));
    }

    public override async Task<bool> Handle(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        if (entityViewModel.Data is not JsonElement data)
        {
            return false;
        }

        var workingDirectory = ResolveWorkingDirectory(data);
        var initialParameterValues = workingDirectory is not null
            ? new Dictionary<string, string>(StringComparer.Ordinal) { ["working-directory"] = workingDirectory }
            : (IReadOnlyDictionary<string, string>?)null;

        var preSelectedEntityId = await ResolveDefaultManifestEntityIdAsync(mainWindowViewModel, entityViewModel);

        var startAgentSessionTab = new StartAgentSessionOnProfileViewModel(
            mainWindowViewModel,
            this.agentSessionShortcutContext,
            this.openAgentSessionShortcutHandler,
            mainWindowViewModel,
            entityViewModel,
            preSelectedEntityId: preSelectedEntityId,
            initialParameterValues: initialParameterValues)
        {
            Id = $"start-agent-session-{entityViewModel.EntityId}",
            Title = $"Start Agent Session on {entityViewModel.DisplayName}",
        };

        await mainWindowViewModel.OpenTabAsync(startAgentSessionTab);
        return true;
    }

    private static string? ResolveWorkingDirectory(JsonElement data)
    {
        if (data.TryGetProperty("path", out var pathEl)
            && pathEl.ValueKind == JsonValueKind.String
            && pathEl.GetString() is { } path
            && !string.IsNullOrEmpty(path))
        {
            return path;
        }

        if (data.TryGetProperty("home-directory", out var homeEl)
            && homeEl.ValueKind == JsonValueKind.String
            && homeEl.GetString() is { } homeDir
            && !string.IsNullOrEmpty(homeDir))
        {
            return homeDir;
        }

        return null;
    }

    private static async Task<EntityId?> ResolveDefaultManifestEntityIdAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel entityViewModel)
    {
        try
        {
            var dataAccessLayer = mainWindowViewModel.EntityBroker.EntityRepository.DataAccessLayer;
            var workspaceEntitySession = mainWindowViewModel.EntityBroker.EntityRepository.WorkspaceEntitySession;

            // 1. Try entity-type default: for each specific entity type, find the default manifest
            var entityTypeNames = GetSpecificEntityTypeNames(entityViewModel.Data);
            foreach (var typeName in entityTypeNames)
            {
                var entityTypeEntityId = await FindEntityTypeEntityIdAsync(dataAccessLayer, typeName);
                if (entityTypeEntityId is null)
                {
                    continue;
                }

                var defaultId = await FindDefaultAppliedToAsync(dataAccessLayer, entityTypeEntityId.Value);
                if (defaultId is not null)
                {
                    return defaultId;
                }
            }

            // 2. Try owning user-computer-profile default
            var profileEntityId = workspaceEntitySession.UserComputerProfileEntityId;
            if (profileEntityId != default)
            {
                var defaultId = await FindDefaultAppliedToAsync(dataAccessLayer, profileEntityId);
                if (defaultId is not null)
                {
                    return defaultId;
                }
            }

            // 3. Try user default
            var userEntityId = workspaceEntitySession.UserEntityId;
            if (userEntityId != default)
            {
                var defaultId = await FindDefaultAppliedToAsync(dataAccessLayer, userEntityId);
                if (defaultId is not null)
                {
                    return defaultId;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> GetSpecificEntityTypeNames(JsonElement? data)
    {
        if (data is not JsonElement element
            || !element.TryGetProperty("entity-types", out var entityTypesEl)
            || entityTypesEl.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var names = new List<string>();
        foreach (var item in entityTypesEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String
                && item.GetString() is { } name
                && !string.Equals(name, "entity", StringComparison.Ordinal))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static async Task<EntityId?> FindEntityTypeEntityIdAsync(
        IDataAccessLayer dataAccessLayer,
        string entityTypeName)
    {
        var getResult = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = new EntityName("entity-types", entityTypeName),
                    },
                ],
            });

        var entityTypeSnapshot = getResult.Batches
            .SelectMany(static batch => batch.Entities)
            .FirstOrDefault();

        return entityTypeSnapshot?.EntityId;
    }

    private static async Task<EntityId?> FindDefaultAppliedToAsync(
        IDataAccessLayer dataAccessLayer,
        EntityId appliedToEntityId)
    {
        var queryResult = await dataAccessLayer.QueryAsync(
            new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier("default-manifest-for-entity"),
                        Clause = new AndQueryClause
                        {
                            Clauses =
                            [
                                new EntityTypeQueryClause
                                {
                                    EntityTypeNames = new EntityTypeNameSet(["default"]),
                                },
                                new EntityFieldQueryClause
                                {
                                    FieldPath = new FieldPath("participants", "applied-to"),
                                    ComparisonOperator = FieldComparisonOperator.Equals,
                                    Value = JsonSerializer.SerializeToElement(appliedToEntityId.Value.ToString()),
                                },
                            ],
                        },
                    },
                ],
            });

        foreach (var snapshot in queryResult.Batches.SelectMany(static batch => batch.Entities))
        {
            if (snapshot.Data is JsonElement data
                && data.TryGetProperty("participants", out var participants)
                && participants.TryGetProperty("value", out var valueEl))
            {
                var reference = valueEl.TryReadEntityReference();
                if (reference?.EntityId is { } entityId)
                {
                    return entityId;
                }
            }
        }

        return null;
    }
}


