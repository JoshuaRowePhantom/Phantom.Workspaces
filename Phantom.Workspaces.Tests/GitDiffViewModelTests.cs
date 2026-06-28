using System.Linq;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class GitDiffViewModelTests
{
    private const string SamplePatch = """
        diff --git a/src/Foo.cs b/src/Foo.cs
        index abc1234..def5678 100644
        --- a/src/Foo.cs
        +++ b/src/Foo.cs
        @@ -10,6 +10,8 @@ namespace Example;
          context before
         unchanged line
        -removed line
        +added line 1
        +added line 2
          context after
        """;

    [Fact]
    public void ParseHunks_HunksContainCorrectLineNumbers()
    {
        var hunks = GitDiffViewModel.ParseHunks(SamplePatch);

        Assert.NotEmpty(hunks);
        var hunk = hunks[0];
        Assert.Equal(10, hunk.OldStart);
        Assert.Equal(10, hunk.NewStart);
    }

    [Fact]
    public void ParseHunks_AddedLinesMarkedAsAdded()
    {
        var hunks = GitDiffViewModel.ParseHunks(SamplePatch);
        var lines = hunks.SelectMany(h => h.Lines).ToList();

        var addedLines = lines.Where(l => l.Kind == GitDiffLineKind.Added).ToList();
        Assert.NotEmpty(addedLines);
        Assert.All(addedLines, l => Assert.Null(l.OldLineNumber));
        Assert.All(addedLines, l => Assert.NotNull(l.NewLineNumber));
    }

    [Fact]
    public void ParseHunks_RemovedLinesMarkedAsRemoved()
    {
        var hunks = GitDiffViewModel.ParseHunks(SamplePatch);
        var lines = hunks.SelectMany(h => h.Lines).ToList();

        var removedLines = lines.Where(l => l.Kind == GitDiffLineKind.Removed).ToList();
        Assert.NotEmpty(removedLines);
        Assert.All(removedLines, l => Assert.NotNull(l.OldLineNumber));
        Assert.All(removedLines, l => Assert.Null(l.NewLineNumber));
    }

    [Fact]
    public void ParseHunks_ContextLinesHaveBothLineNumbers()
    {
        var hunks = GitDiffViewModel.ParseHunks(SamplePatch);
        var lines = hunks.SelectMany(h => h.Lines).ToList();

        var contextLines = lines.Where(l => l.Kind == GitDiffLineKind.Context).ToList();
        Assert.NotEmpty(contextLines);
        Assert.All(contextLines, l => Assert.NotNull(l.OldLineNumber));
        Assert.All(contextLines, l => Assert.NotNull(l.NewLineNumber));
    }

    [Fact]
    public void ParseHunks_EmptyPatch_ReturnsEmptyList()
    {
        var hunks = GitDiffViewModel.ParseHunks(string.Empty);
        Assert.Empty(hunks);
    }

    [Fact]
    public void ParseHunks_LineNumbersAreSequential()
    {
        var hunks = GitDiffViewModel.ParseHunks(SamplePatch);
        Assert.NotEmpty(hunks);

        var hunk = hunks[0];
        var oldNumbers = hunk.Lines
            .Where(l => l.OldLineNumber.HasValue)
            .Select(l => l.OldLineNumber!.Value)
            .ToList();

        for (int i = 1; i < oldNumbers.Count; i++)
        {
            Assert.Equal(oldNumbers[i - 1] + 1, oldNumbers[i]);
        }
    }
}
