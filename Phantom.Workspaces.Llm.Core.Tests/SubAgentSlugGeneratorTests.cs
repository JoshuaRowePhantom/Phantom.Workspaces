using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class SubAgentSlugGeneratorTests
{
    [Fact]
    public void MultiWordPrompt_ProducesLowercaseHyphenatedSlug_TruncatedToFiveWords()
    {
        var slug = SubAgentSlugGenerator.GenerateSlug("Investigate the flaky parser test failure now");

        Assert.Equal("investigate-the-flaky-parser-test", slug);
    }

    [Fact]
    public void ShortPrompt_UsesAllWords()
    {
        var slug = SubAgentSlugGenerator.GenerateSlug("File a bug");

        Assert.Equal("file-a-bug", slug);
    }

    [Fact]
    public void PunctuationAndWhitespace_AreNormalised()
    {
        var slug = SubAgentSlugGenerator.GenerateSlug("  Fix the   widget's colour!!! (please) ");

        Assert.Equal("fix-the-widget-s-colour", slug);
    }

    [Fact]
    public void EmptyOrSymbolOnlyPrompt_FallsBackToDefault()
    {
        Assert.Equal("sub-agent", SubAgentSlugGenerator.GenerateSlug("   "));
        Assert.Equal("sub-agent", SubAgentSlugGenerator.GenerateSlug("!!!"));
    }

    [Fact]
    public void Collision_AppendsDeduplicationSuffix()
    {
        var existing = new[] { "file-a-bug" };

        var slug = SubAgentSlugGenerator.GenerateSlug("File a bug", existing);

        Assert.Equal("file-a-bug-2", slug);
    }

    [Fact]
    public void RepeatedCollision_IncrementsSuffix()
    {
        var existing = new[] { "file-a-bug", "file-a-bug-2", "file-a-bug-3" };

        var slug = SubAgentSlugGenerator.GenerateSlug("File a bug!", existing);

        Assert.Equal("file-a-bug-4", slug);
    }

    [Fact]
    public void Collision_IsCaseInsensitive()
    {
        var existing = new[] { "FILE-A-BUG" };

        var slug = SubAgentSlugGenerator.GenerateSlug("File a bug", existing);

        Assert.Equal("file-a-bug-2", slug);
    }

    [Fact]
    public void NoCollision_ReturnsBaseSlug()
    {
        var existing = new[] { "something-else" };

        var slug = SubAgentSlugGenerator.GenerateSlug("File a bug", existing);

        Assert.Equal("file-a-bug", slug);
    }

    [Fact]
    public void DigitsArePreserved()
    {
        var slug = SubAgentSlugGenerator.GenerateSlug("Upgrade to version 2 of the api");

        Assert.Equal("upgrade-to-version-2-of", slug);
    }
}
