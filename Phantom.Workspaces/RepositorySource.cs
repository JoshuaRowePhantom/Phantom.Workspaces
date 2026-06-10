using System;
using System.Collections.Generic;
using System.IO;

namespace Phantom.Workspaces;

public enum RepositorySourceType
{
    Unknown = 0,
    Web = 1,
    LocalGit = 2,
    MongoDb = 3,
}

public sealed record RepositorySource(
    RepositorySourceType SourceType,
    string RawValue,
    string? MongoDbContainerName = null,
    string? MongoDbDataDirectory = null,
    string? MongoDbDatabaseName = null,
    string? MongoDbRootCollectionName = null,
    int? MongoDbHostPort = null)
{
    public static RepositorySource Parse(
        IReadOnlyList<string> args)
    {
        if (args.Count == 0 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new RepositorySource(RepositorySourceType.Unknown, "(none)");
        }

        var firstArg = args[0].Trim();
        if (firstArg.StartsWith("--", StringComparison.Ordinal))
        {
            return ParseNamedArguments(args);
        }

        if (Uri.TryCreate(firstArg, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return new RepositorySource(RepositorySourceType.Web, firstArg);
        }

        return new RepositorySource(RepositorySourceType.LocalGit, Path.GetFullPath(firstArg));
    }

    private static RepositorySource ParseNamedArguments(
        IReadOnlyList<string> args)
    {
        var namedArguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var argumentIndex = 0; argumentIndex < args.Count; argumentIndex += 2)
        {
            var argumentName = args[argumentIndex].Trim();
            if (!argumentName.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Named argument '{argumentName}' must start with '--'.",
                    nameof(args));
            }

            if (argumentIndex == args.Count - 1)
            {
                throw new ArgumentException(
                    $"Named argument '{argumentName}' is missing a value.",
                    nameof(args));
            }

            namedArguments[argumentName] = args[argumentIndex + 1];
        }

        if (!namedArguments.TryGetValue("--data-store", out var dataStore)
            || !string.Equals(dataStore, "mongodb", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Named arguments must include '--data-store mongodb'.",
                nameof(args));
        }

        if (!namedArguments.TryGetValue("--mongodb-container-name", out var mongoDbContainerName)
            || string.IsNullOrWhiteSpace(mongoDbContainerName))
        {
            throw new ArgumentException(
                "Named arguments must include '--mongodb-container-name'.",
                nameof(args));
        }

        if (!namedArguments.TryGetValue("--mongodb-root-collection-name", out var mongoDbRootCollectionName)
            || string.IsNullOrWhiteSpace(mongoDbRootCollectionName))
        {
            throw new ArgumentException(
                "Named arguments must include '--mongodb-root-collection-name'.",
                nameof(args));
        }

        namedArguments.TryGetValue("--mongodb-data-directory", out var mongoDbDataDirectory);
        namedArguments.TryGetValue("--mongodb-database-name", out var mongoDbDatabaseName);
        namedArguments.TryGetValue("--mongodb-host-port", out var mongoDbHostPortText);

        var mongoDbHostPort = ParseOptionalInteger(mongoDbHostPortText, "--mongodb-host-port");
        var resolvedDataDirectory = string.IsNullOrWhiteSpace(mongoDbDataDirectory)
            ? null
            : Path.GetFullPath(mongoDbDataDirectory);

        return new RepositorySource(
            RepositorySourceType.MongoDb,
            string.Join(' ', args),
            MongoDbContainerName: mongoDbContainerName,
            MongoDbDataDirectory: resolvedDataDirectory,
            MongoDbDatabaseName: mongoDbDatabaseName,
            MongoDbRootCollectionName: mongoDbRootCollectionName,
            MongoDbHostPort: mongoDbHostPort);
    }

    private static int? ParseOptionalInteger(
        string? value,
        string argumentName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!int.TryParse(value, out var parsedInteger))
        {
            throw new ArgumentException(
                $"Named argument '{argumentName}' must be an integer value.");
        }

        return parsedInteger;
    }
}
