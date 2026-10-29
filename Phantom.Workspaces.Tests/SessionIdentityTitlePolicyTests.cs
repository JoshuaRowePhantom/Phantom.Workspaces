using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Services;

namespace Phantom.Workspaces.Tests;

public sealed class SessionIdentityTitlePolicyTests
{
    [Theory]
    [InlineData(null, "Agent session (unknown)")]
    [InlineData("", "Agent session (unknown)")]
    [InlineData("short", "Agent session short")]
    [InlineData("abcdefgh", "Agent session abcdefgh")]
    [InlineData("abcdefghijk", "Agent session abcdefgh")]
    [InlineData(" \t ", "Agent session  \t ")]
    public void ResolveFallback_WithoutNonblankCandidates_PreservesIdentifierFormatting(
        string? identifier,
        string expected)
    {
        var sessionId = identifier is null ? default : new AgentSessionId(identifier);

        Assert.Equal(expected, SessionIdentityTitlePolicy.ResolveFallback(sessionId));
        Assert.Equal(expected, SessionIdentityTitlePolicy.ResolveFallback(sessionId, null, "", " \t"));
    }

    [Theory]
    [InlineData("First", "Second", "Third", "First")]
    [InlineData(null, "Second", "Third", "Second")]
    [InlineData("", " \t", "Third", "Third")]
    [InlineData("  Padded name \t", "Second", null, "  Padded name \t")]
    [InlineData("abcdefghijk", null, null, "abcdefghijk")]
    public void ResolveFallback_ReturnsFirstNonblankCandidateVerbatim(
        string? first,
        string? second,
        string? third,
        string expected)
    {
        Assert.Equal(
            expected,
            SessionIdentityTitlePolicy.ResolveFallback(
                new AgentSessionId("abcdefghijk"),
                first,
                second,
                third));
    }
}
