using System;
using System.Runtime.CompilerServices;

namespace Phantom.Workspaces.IntegrationTests;

/// <summary>
/// A <see cref="FactAttribute"/> that marks the test as skipped when
/// <c>PHANTOM_INTEGRATION_GITHUB_TOKEN</c> is not set in the environment.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute(
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = -1)
        : base(filePath, lineNumber)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PHANTOM_INTEGRATION_GITHUB_TOKEN")))
        {
            Skip = "PHANTOM_INTEGRATION_GITHUB_TOKEN is not set; skipping integration test.";
        }
    }
}
