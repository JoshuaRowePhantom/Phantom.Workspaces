using Microsoft.Win32;
using System.Runtime.Versioning;
using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

/// <summary>
/// Windows-only round-trip tests for <see cref="RegistryStartupRegistration"/>. Each test uses a
/// unique, test-scoped value name under the real HKCU Run key and always cleans up after itself.
/// </summary>
public sealed class RegistryStartupRegistrationTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void EnableThenIsEnabledThenDisable_RoundTrips()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var valueName = "Phantom.Workspaces.Test." + Guid.NewGuid().ToString("N");
        var registration = new RegistryStartupRegistration();
        try
        {
            Assert.False(registration.IsEnabled(valueName));

            registration.Enable(valueName, @"""C:\app\current\Phantom.Workspaces.exe"" --startup");

            Assert.True(registration.IsEnabled(valueName));
            using (var key = Registry.CurrentUser.OpenSubKey(RegistryStartupRegistration.RunKeyPath))
            {
                Assert.NotNull(key);
                var stored = key!.GetValue(valueName) as string;
                Assert.Contains("--startup", stored, StringComparison.Ordinal);
            }

            registration.Disable(valueName);
            Assert.False(registration.IsEnabled(valueName));
        }
        finally
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryStartupRegistration.RunKeyPath, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Disable_IsIdempotentWhenMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var valueName = "Phantom.Workspaces.Test." + Guid.NewGuid().ToString("N");
        var registration = new RegistryStartupRegistration();

        registration.Disable(valueName);

        Assert.False(registration.IsEnabled(valueName));
    }
}
