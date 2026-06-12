using System.Text.Json;

namespace Phantom.Workspaces.Data;

public readonly record struct EntityTypeName(string Value);

public static class WorkspaceEntityNameFactory
{
    public static async Task<EntityName[]> CreateEntityNames(
        IDataAccessLayer dataAccessLayer,
        WorkspaceEntitySession workspaceEntitySession,
        EntityTypeName entityTypeName,
        string simpleName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entityTypeName.Value))
        {
            throw new ArgumentException("Entity type name is required.", nameof(entityTypeName));
        }

        if (string.IsNullOrWhiteSpace(simpleName))
        {
            throw new ArgumentException("Simple name is required.", nameof(simpleName));
        }

        var getResult = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = new EntityName("entity-types", entityTypeName.Value),
                    },
                ],
            },
            cancellationToken);
        var entityTypeEntity = getResult.Batches
            .SelectMany(static batch => batch.Entities)
            .FirstOrDefault();
        var defaultNamePrefixes = ReadDefaultNamePrefixes(entityTypeEntity?.Data);
        if (defaultNamePrefixes.Count == 0)
        {
            return [new EntityName(simpleName)];
        }

        var sessionNameResolver = new WorkspaceEntitySessionNameResolver(dataAccessLayer, workspaceEntitySession);
        var resolvedNames = await sessionNameResolver.GetResolvedNamesAsync(cancellationToken);
        var names = defaultNamePrefixes
            .SelectMany(
                prefix =>
                {
                    return sessionNameResolver.RewriteMetaVariables(prefix, resolvedNames)
                        .Select(rewrittenPrefix => new EntityName([.. rewrittenPrefix.Components, simpleName]));
                })
            .Distinct()
            .ToArray();
        return names;
    }

    private static IReadOnlyCollection<EntityName> ReadDefaultNamePrefixes(
        JsonElement? entityTypeData)
    {
        if (entityTypeData is not JsonElement dataElement
            || !dataElement.TryGetProperty("default-name-prefixes", out var defaultNamePrefixesElement)
            || defaultNamePrefixesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var prefixes = new List<EntityName>();
        foreach (var prefixElement in defaultNamePrefixesElement.EnumerateArray())
        {
            var entityName = prefixElement.TryReadEntityName();
            if (entityName is null)
            {
                continue;
            }

            prefixes.Add(entityName.Value);
        }

        return prefixes;
    }
}
