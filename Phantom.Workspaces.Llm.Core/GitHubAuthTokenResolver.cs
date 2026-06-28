using System;
using System.ComponentModel;
using Phantom.Workspaces;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Resolves a GitHub authentication token from the environment, falling back to the GitHub CLI.
/// The token is taken from the predefined <c>GITHUB_TOKEN</c> environment variable when set;
/// otherwise it is obtained from <c>gh auth token</c>. This is the single place that resolves the
/// GitHub token (for example for GitHub model/MCP API keys and the dev tunnel
/// <c>X-Tunnel-Authorization</c> token), so the token source is never a user-configured field.
/// </summary>
public static class GitHubAuthTokenResolver
{
    /// <summary>The predefined environment variable that holds a GitHub token when present.</summary>
    public const string GitHubTokenEnvironmentVariable = "GITHUB_TOKEN";

    private static readonly TimeSpan GitHubCliTimeout = TimeSpan.FromMilliseconds(10_000);

    /// <summary>
    /// Resolves the GitHub token from <c>GITHUB_TOKEN</c>, falling back to <c>gh auth token</c>.
    /// Returns <see langword="null"/> when neither source yields a token.
    /// </summary>
    public static string? Resolve()
    {
        var environmentValue = Environment.GetEnvironmentVariable(GitHubTokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue;
        }

        return ResolveFromCli();
    }

    /// <summary>
    /// Resolves the GitHub token from the GitHub CLI (<c>gh auth token</c>). Returns
    /// <see langword="null"/> when the CLI is unavailable or returns no token.
    /// </summary>
    public static string? ResolveFromCli()
    {
        try
        {
            var result = ProcessRunner.RunProcessAsync(
                new RunProcessParameters(
                    Command: "gh",
                    Arguments: ["auth", "token"],
                    Timeout: GitHubCliTimeout))
                .GetAwaiter().GetResult();

            if (result.ExitCode != 0)
            {
                return null;
            }

            return result.StandardOut.Trim();
        }
        catch (Win32Exception)
        {
            return null;
        }
    }
}
