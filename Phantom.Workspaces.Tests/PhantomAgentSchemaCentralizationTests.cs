using System.Text.RegularExpressions;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// #1416: <see cref="Phantom.Workspaces.Llm.PhantomAgentSchema"/> is the single sanctioned entry
/// point for loading AgentSchema agent definitions, manifests, and MCP tools. This source-scan guard
/// fails the build if any production file (other than <c>PhantomAgentSchema</c>) calls the AgentSchema
/// <c>FromJson</c>/<c>FromYaml</c> overloads directly, or constructs a bare <c>LoadContext</c> /
/// <c>TrackingLoadContext</c> — any such bypass silently drops the Phantom <c>type</c> transport field
/// and reverts to <c>AutoDetect</c>.
/// </summary>
public sealed class PhantomAgentSchemaCentralizationTests
{
    private static readonly Regex[] ForbiddenPatterns =
    [
        new(@"\bAgentDefinition\.FromJson\b", RegexOptions.Compiled),
        new(@"\bAgentDefinition\.FromYaml\b", RegexOptions.Compiled),
        new(@"\bAgentManifest\.FromJson\b", RegexOptions.Compiled),
        new(@"\bAgentManifest\.FromYaml\b", RegexOptions.Compiled),
        new(@"\bMcpTool\.FromJson\b", RegexOptions.Compiled),
        new(@"(?<!Mcp)\bTool\.FromJson\b", RegexOptions.Compiled),
        new(@"\bnew\s+TrackingLoadContext\b", RegexOptions.Compiled),
        new(@"\bnew\s+LoadContext\b", RegexOptions.Compiled),
    ];

    [Fact]
    public void Production_DoesNotCallAgentSchemaFromJsonDirectly_OutsidePhantomAgentSchema()
    {
        var root = FindRepositoryRoot();

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (IsExcluded(root, file))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (var pattern in ForbiddenPatterns)
            {
                if (pattern.IsMatch(text))
                {
                    violations.Add($"{Path.GetRelativePath(root, file)} matches /{pattern}/");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Production code must load AgentSchema definitions/manifests/tools only through "
            + "PhantomAgentSchema (#1416). Offending sites:\n" + string.Join("\n", violations));
    }

    private static bool IsExcluded(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');

        // Only PhantomAgentSchema itself may reference the AgentSchema FromJson/FromYaml overloads.
        if (relative.EndsWith("/PhantomAgentSchema.cs", StringComparison.Ordinal)
            || relative.Equals("PhantomAgentSchema.cs", StringComparison.Ordinal))
        {
            return true;
        }

        // Build artifacts, the vendored submodule, and test projects are out of scope.
        if (relative.Contains("/obj/", StringComparison.Ordinal)
            || relative.Contains("/bin/", StringComparison.Ordinal)
            || relative.StartsWith("Phantom.Dock.Avalonia.TabSwitching/", StringComparison.Ordinal))
        {
            return true;
        }

        var projectSegment = relative.Split('/', 2)[0];
        return projectSegment.EndsWith(".Tests", StringComparison.Ordinal)
            || projectSegment.EndsWith(".Test", StringComparison.Ordinal)
            || projectSegment.EndsWith(".WebViewTests", StringComparison.Ordinal)
            || projectSegment.EndsWith(".IntegrationTests", StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Phantom.Workspaces.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
