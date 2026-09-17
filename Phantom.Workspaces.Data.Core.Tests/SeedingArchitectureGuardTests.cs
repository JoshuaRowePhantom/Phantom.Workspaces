using Phantom.Workspaces.Testing;

namespace Phantom.Workspaces.Data.Tests;

public sealed class SeedingArchitectureGuardTests
{
    private static readonly IReadOnlyDictionary<string, string> AllowedCategoryBFiles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Phantom.Workspaces.Tests\\EntityBrokerTests.cs"] =
                "CallbackDataAccessLayer decorates the initialized production repository to observe calls; it does not manufacture snapshots.",
            ["Phantom.Workspaces.Tests\\EntityClassifierToolTests.cs"] =
                "Classifier tests exercise replacement/deletion mechanics below the repository composition.",
            ["Phantom.Workspaces.Tests\\GitWorkspaceUpdateToolTests.cs"] =
                "Main-application compatibility suite outside the Tools test-project migration tracked by this issue.",
            ["Phantom.Workspaces.Tests\\McpServerEntityBoundExecutorResolutionTests.cs"] =
                "Resolver-isolation test for an already materialized entity.",
            ["Phantom.Workspaces.Tests\\McpServerEntityToolResourceFactoryTests.cs"] =
                "Resource factory isolation test for an already materialized entity.",
            ["Phantom.Workspaces.Tests\\RunningAgentChatTableTests.cs"] =
                "Runtime host isolation suite with a recording raw store.",
            ["Phantom.Workspaces.Tests\\ScheduledToolHostTests.cs"] =
                "Scheduler persistence-mechanics suite, not reusable valid fixture setup.",
            ["Phantom.Workspaces.Tests\\ScheduledToolPauseStateServiceTests.cs"] =
                "Pause-state persistence-mechanics suite, not reusable valid fixture setup.",
            ["Phantom.Workspaces.Tests\\ScheduledToolsPauseIndicatorViewModelTests.cs"] =
                "View-model isolation suite with explicit raw state.",
            ["Phantom.Workspaces.Tests\\VectorIndexerToolTests.cs"] =
                "Vector queue/index storage contract suite intentionally below schema validation.",
        };

    [Fact]
    public void SeedingArchitectureGuard_TestDeclaresAdHocDataAccessLayerFake_FailsUnlessAllowListed()
    {
        const string source =
            """
            private sealed class Fake : IDataAccessLayer
            {
                private readonly IReadOnlyList<EntitySnapshot> entities = [];
            }
            """;

        var violations = SeedingArchitectureGuard.FindViolations(source);

        Assert.Contains(violations, violation => violation.Contains("manufacture", StringComparison.Ordinal));
        Assert.Empty(FindUnallowedViolations("Allowed.cs", source, new Dictionary<string, string>
        {
            ["Allowed.cs"] = "Deliberately malformed input test.",
        }));
    }

    [Fact]
    public void SeedingArchitectureGuard_TestWritesEntityToRawInMemory_FailsUnlessAllowListed()
    {
        const string source =
            """
            var store = new InMemoryDataAccessLayer();
            await store.UpdateAsync(new UpdateRequest { Changes = changes });
            """;

        var violations = SeedingArchitectureGuard.FindViolations(source);

        Assert.Contains(violations, violation => violation.Contains("Raw InMemoryDataAccessLayer", StringComparison.Ordinal));
    }

    [Fact]
    public void SeedingArchitectureGuard_AllRuntimeEntityTestSeeds_UseValidatedOrDocumentedRawPath()
    {
        var root = FindRepositoryRoot();
        var projectDirectories = new[]
        {
            "Phantom.Workspaces.Transport.Tests",
            "Phantom.Workspaces.Tests",
            "Phantom.Workspaces.Llm.Core.Tests",
            "Phantom.Workspaces.Tools.Tests",
        };
        var violations = new List<string>();

        foreach (var projectDirectory in projectDirectories)
        {
            var fullDirectory = Path.Combine(root, projectDirectory);
            foreach (var file in Directory.EnumerateFiles(fullDirectory, "*.cs", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(root, file);
                var source = File.ReadAllText(file);
                violations.AddRange(FindUnallowedViolations(relativePath, source, AllowedCategoryBFiles));
            }
        }

        Assert.Empty(violations);
    }

    private static IEnumerable<string> FindUnallowedViolations(
        string relativePath,
        string source,
        IReadOnlyDictionary<string, string> allowList)
    {
        if (allowList.ContainsKey(relativePath))
        {
            return [];
        }

        return SeedingArchitectureGuard.FindViolations(source)
            .Select(violation => $"{relativePath}: {violation}");
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

        throw new InvalidOperationException("Could not locate the Phantom.Workspaces repository root.");
    }
}
