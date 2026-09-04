using System.Diagnostics;

namespace Phantom.Workspaces.Services.Mcp;

/// <summary>
/// Production <see cref="ISystemBrowserLauncher"/> that opens the platform default browser through the
/// shell (<see cref="ProcessStartInfo.UseShellExecute"/>), which resolves the registered
/// <c>http</c>/<c>https</c> handler on every supported OS.
/// </summary>
public sealed class SystemBrowserLauncher : ISystemBrowserLauncher
{
    public void Open(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        using var process = Process.Start(new ProcessStartInfo(uri.ToString())
        {
            UseShellExecute = true,
        });
    }
}
