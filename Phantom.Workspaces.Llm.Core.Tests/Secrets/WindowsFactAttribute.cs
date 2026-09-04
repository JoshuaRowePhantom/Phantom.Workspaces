using System;
using System.Runtime.CompilerServices;

namespace Phantom.Workspaces.Llm.Core.Tests.Secrets;

/// <summary>
/// A <see cref="FactAttribute"/> that marks the test as skipped when the current operating system is
/// not Windows. Used by tests that exercise the real Windows Credential Manager.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Test requires Windows; skipping on non-Windows platform.";
        }
    }
}
