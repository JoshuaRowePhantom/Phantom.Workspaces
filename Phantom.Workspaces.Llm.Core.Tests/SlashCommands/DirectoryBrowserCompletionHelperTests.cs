using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Llm.Tests.SlashCommands;

public sealed class DirectoryBrowserCompletionHelperTests
{
    [Fact]
    public void GetCompletions_ReturnsChildDirectories_ForExistingDir()
    {
        var dir = Path.GetTempPath();
        var results = DirectoryBrowserCompletionHelper.GetCompletions(dir);

        // Temp path always has subdirectories; results must be non-empty
        Assert.NotEmpty(results);
        foreach (var c in results)
        {
            Assert.StartsWith(dir, c.CompletionText, System.StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void GetCompletions_FiltersOnPrefix()
    {
        // Use a known directory where we control subdirectory names
        var root = Path.Combine(Path.GetTempPath(), $"dcbh_test_{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Alpha"));
        Directory.CreateDirectory(Path.Combine(root, "Beta"));
        Directory.CreateDirectory(Path.Combine(root, "Gamma"));

        try
        {
            var partial = Path.Combine(root, "Al");
            var results = DirectoryBrowserCompletionHelper.GetCompletions(partial);

            Assert.Single(results);
            Assert.Equal("Alpha", results[0].Label);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GetCompletions_CompletionTextEndsWithSeparator()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dcbh_sep_{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Sub"));

        try
        {
            var results = DirectoryBrowserCompletionHelper.GetCompletions(root);

            Assert.All(results, c => Assert.Equal(Path.DirectorySeparatorChar, c.CompletionText[^1]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GetCompletions_ReturnsEmpty_ForNonExistentDir()
    {
        var results = DirectoryBrowserCompletionHelper.GetCompletions(
            @"C:\DoesNotExist_XYZ_99999\SomeSubDir");

        Assert.Empty(results);
    }

    [Fact]
    public void GetCompletions_CapsAt20()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dcbh_cap_{System.Guid.NewGuid():N}");
        for (int i = 0; i < 25; i++)
        {
            Directory.CreateDirectory(Path.Combine(root, $"Dir{i:D2}"));
        }

        try
        {
            var results = DirectoryBrowserCompletionHelper.GetCompletions(root);
            Assert.True(results.Count <= 20);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GetCompletions_ReturnsEmpty_ForEmptyPartialPath()
    {
        var results = DirectoryBrowserCompletionHelper.GetCompletions(string.Empty);
        Assert.Empty(results);
    }
}

public sealed class WorkingDirectorySlashCommandHandlerCompletionTests
{
    private readonly WorkingDirectorySlashCommandHandler handler = new();

    private static readonly AgentDefinition EchoAgentDefinition =
        AgentDefinitionLoader.LoadAgentFromJson("""
        {
          "kind": "prompt",
          "name": "test-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
        }
        """);

    private static Task<AgentChat> CreateChatAsync() =>
        AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = EchoAgentDefinition,
        });

    [Fact]
    public async Task GetCompletionsAsync_DelegatesToHelper()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wdsch_{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "ProjectA"));
        Directory.CreateDirectory(Path.Combine(root, "ProjectB"));

        try
        {
            await using var chat = await CreateChatAsync();
            var context = new SlashCommandContext { AgentChat = chat };

            var results = await this.handler.GetCompletionsAsync(context, root, CancellationToken.None);
            var expected = DirectoryBrowserCompletionHelper.GetCompletions(root);

            Assert.Equal(expected.Count, results.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].CompletionText, results[i].CompletionText);
                Assert.Equal(expected[i].Label, results[i].Label);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
