namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class FilesystemServiceToolsetTests
{
    [Fact]
    public async Task ListToolsAsync_ReturnsFilesystemToolSet()
    {
        await using var toolset = new FilesystemServiceToolset();

        var tools = await toolset.ListToolsAsync();
        var toolNames = tools
            .Select(static tool => tool.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["describe_edit", "edit", "edit_apply", "make_directory", "move_item", "read", "remove_item", "search"],
            toolNames);
    }

    [Fact]
    public async Task ListToolsAsync_CanBeCalledMultipleTimes()
    {
        await using var toolset = new FilesystemServiceToolset();

        var firstTools = await toolset.ListToolsAsync();
        var secondTools = await toolset.ListToolsAsync();

        Assert.Equal(firstTools.Count, secondTools.Count);
        Assert.NotEmpty(firstTools);
    }

}
