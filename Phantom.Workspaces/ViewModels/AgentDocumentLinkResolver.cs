namespace Phantom.Workspaces.ViewModels;

internal static class AgentDocumentLinkResolver
{
    internal static Uri Resolve(
        string reference,
        string? workingDirectory,
        bool isLocalFileSystem)
    {
        var windowsPath = reference.Length >= 3 && char.IsLetter(reference[0]) && reference[1] == ':'
            && reference[2] is '\\' or '/' || reference.StartsWith(@"\\", StringComparison.Ordinal);
        if (!windowsPath && Uri.TryCreate(reference, UriKind.Absolute, out var absolute))
        {
            if (absolute.Scheme is "http" or "https" or "mailto")
                return absolute;
            if (absolute.IsFile && isLocalFileSystem)
                return absolute;
            throw new InvalidOperationException("This document destination is not supported by this browser.");
        }

        if (!isLocalFileSystem)
            throw new InvalidOperationException("Opening documents from a remote agent filesystem is not supported.");
        if (!windowsPath && (workingDirectory is null || !Path.IsPathFullyQualified(workingDirectory)))
            throw new InvalidOperationException("The originating agent has no absolute document directory.");

        var fragmentIndex = reference.IndexOf('#');
        var fragment = fragmentIndex >= 0 ? reference[fragmentIndex..] : string.Empty;
        var encodedPath = fragmentIndex >= 0 ? reference[..fragmentIndex] : reference;
        var path = Uri.UnescapeDataString(encodedPath);
        var fullPath = windowsPath ? Path.GetFullPath(path) : Path.GetFullPath(path, workingDirectory!);
        return new Uri(new Uri(fullPath).AbsoluteUri + fragment);
    }
}
