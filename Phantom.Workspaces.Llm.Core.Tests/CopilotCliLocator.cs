namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Locates the GitHub Copilot CLI executable for end-to-end BYOK tests. Prefers the explicit
/// <c>COPILOT_CLI_PATH</c> environment variable (the same override the opt-in
/// <c>CopilotByokTests</c> e2e tests use) and otherwise searches <c>PATH</c> for
/// <c>copilot.exe</c>/<c>copilot.cmd</c>/<c>copilot</c>, matching what
/// <c>(Get-Command copilot).Path</c> would resolve.
/// </summary>
public static class CopilotCliLocator
{
    /// <summary>
    /// Returns the full path of the Copilot CLI executable, or <see langword="null"/> when the
    /// CLI cannot be found on this machine.
    /// </summary>
    public static string? Find()
    {
        var explicitPath = Environment.GetEnvironmentVariable("COPILOT_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string[] fileNames = OperatingSystem.IsWindows()
            ? ["copilot.exe", "copilot.cmd", "copilot.bat"]
            : ["copilot"];

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var fileName in fileNames)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory.Trim(), fileName);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the CLI path or throws a descriptive error when the CLI is unavailable, so tests
    /// that require the real CLI fail loudly instead of hanging.
    /// </summary>
    public static string FindOrThrow()
        => Find() ?? throw new InvalidOperationException(
            "GitHub Copilot CLI not found. Install the 'copilot' CLI or set COPILOT_CLI_PATH "
            + "to the full path of the executable.");
}
