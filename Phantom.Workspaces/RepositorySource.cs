using System;
using System.Collections.Generic;
using System.IO;

namespace Phantom.Workspaces;

public enum RepositorySourceType
{
    Unknown = 0,
    Web = 1,
    LocalGit = 2,
}

public sealed record RepositorySource(
    RepositorySourceType SourceType,
    string RawValue)
{
    public static RepositorySource Parse(
        IReadOnlyList<string> args)
    {
        if (args.Count == 0 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new RepositorySource(RepositorySourceType.Unknown, "(none)");
        }

        var firstArg = args[0].Trim();
        if (Uri.TryCreate(firstArg, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return new RepositorySource(RepositorySourceType.Web, firstArg);
        }

        return new RepositorySource(RepositorySourceType.LocalGit, Path.GetFullPath(firstArg));
    }
}
