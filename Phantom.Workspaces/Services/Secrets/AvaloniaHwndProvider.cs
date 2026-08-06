using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace Phantom.Workspaces.Services.Secrets;

public sealed class AvaloniaHwndProvider : IHwndProvider
{
    public nint GetActiveHwnd()
    {
        return (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow
            ?.TryGetPlatformHandle()
            ?.Handle
            ?? 0;
    }
}
