namespace Phantom.Workspaces.Install;

/// <summary>
/// Resolves the managed install root. An explicit override (the hidden <c>--install-root</c>
/// argument) takes precedence, then the <c>PHANTOM_WORKSPACES_INSTALL_ROOT</c> environment
/// variable, then the per-user default under <c>%LOCALAPPDATA%</c>. The override seams are
/// development- and test-only and default to the production value, so shipping behavior is
/// unchanged.
/// </summary>
public static class InstallRootResolver
{
    /// <summary>The environment variable that overrides the install root.</summary>
    public const string InstallRootEnvironmentVariable = "PHANTOM_WORKSPACES_INSTALL_ROOT";

    /// <summary>The per-user application directory name under <c>%LOCALAPPDATA%</c>.</summary>
    public const string ApplicationDirectoryName = "Phantom.Workspaces";

    /// <summary>The managed application folder name holding <c>current</c>/<c>versions</c>/<c>updates</c>.</summary>
    public const string AppFolderName = "app";

    /// <summary>
    /// Resolves the install root using the supplied <paramref name="environment"/> and
    /// <paramref name="localApplicationDataProvider"/> seams, so resolution is unit-testable.
    /// </summary>
    public static string Resolve(
        string? overridePath,
        Func<string, string?> environment,
        Func<string> localApplicationDataProvider)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(localApplicationDataProvider);

        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        var environmentOverride = environment(InstallRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentOverride))
        {
            return environmentOverride;
        }

        return Path.Combine(localApplicationDataProvider(), ApplicationDirectoryName, AppFolderName);
    }

    /// <summary>Resolves the install root against the real process environment.</summary>
    public static string Resolve(string? overridePath)
    {
        return Resolve(
            overridePath,
            Environment.GetEnvironmentVariable,
            static () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }
}
