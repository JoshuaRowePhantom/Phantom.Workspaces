using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class EntityListFieldEditorTests
{
    private sealed class StubSearch : IEntityReferenceSearch
    {
        public Task<IReadOnlyList<EntityReferenceCandidate>> SearchAsync(
            string searchText,
            IReadOnlyCollection<string> entityTypes,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<EntityReferenceCandidate>>([]);
        }

        public Task<EntityReferenceCandidate?> ResolveAsync(
            string entityId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<EntityReferenceCandidate?>(
                new EntityReferenceCandidate(entityId, $"Name for {entityId}", "[\"tests\",\"x\"]"));
        }
    }

    [Fact]
    public async Task ResolveDisplayNamesAsync_ResolvesEachMemberDisplayName()
    {
        var editor = new EntityListFieldEditorViewModel(
            "target",
            ["id-one", "id-two"],
            ["entity"],
            new StubSearch());

        await editor.ResolveDisplayNamesAsync();

        Assert.Equal(2, editor.Items.Count);
        Assert.Equal("Name for id-one", editor.Items[0].DisplayName);
        Assert.Equal("Name for id-two", editor.Items[1].DisplayName);
        Assert.Contains("id-one", editor.Items[0].TooltipText, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ItemsAreSeededWithRawIds_BeforeResolution()
    {
        var editor = new EntityListFieldEditorViewModel(
            "schedule",
            ["raw-id"],
            ["schedule"],
            search: null);

        Assert.Single(editor.Items);
        Assert.Equal("raw-id", editor.Items[0].DisplayName);
        Assert.True(editor.HasItems);
    }
}
