using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Phantom.Workspaces.Install;

/// <summary>
/// The production <see cref="IStartupRegistration"/> backed by the per-user
/// <c>HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run</c> registry key. Writing a
/// value here makes Windows launch the command at user logon with no elevation required — unlike a
/// Task Scheduler entry in the protected root store, which is what caused the "Access is denied"
/// crash (issue #1349).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryStartupRegistration : IStartupRegistration
{
    /// <summary>The per-user Run key path under <see cref="Registry.CurrentUser"/>.</summary>
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <inheritdoc />
    public bool IsEnabled(string valueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(valueName) is not null;
    }

    /// <inheritdoc />
    public void Enable(string valueName, string commandLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(valueName, commandLine, RegistryValueKind.String);
    }

    /// <inheritdoc />
    public void Disable(string valueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(valueName) is not null)
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }
}
