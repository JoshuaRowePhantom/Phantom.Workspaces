using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Testing;

namespace Phantom.Workspaces.Tests;

public sealed class AutoResumeServiceTests
{
    // --- ReadFromEntityData tests ---

    [Fact]
    public void ReadFromEntityData_WhenAutoResumeIsPresent_ReturnsSettings()
    {
        using var doc = JsonDocument.Parse("""
            {
              "entity-types": ["entity", "agent-session"],
              "auto-resume": {
                "trusted-executor": ".",
                "resume-prompt": "Continue where you left off."
              }
            }
            """);

        var result = AutoResumeService.ReadFromEntityData(doc.RootElement);

        Assert.NotNull(result);
        Assert.Equal(".", result!.TrustedExecutor);
        Assert.Equal("Continue where you left off.", result.ResumePrompt);
    }

    [Fact]
    public void ReadFromEntityData_WithNoResumePrompt_ReturnsSettingsWithNullPrompt()
    {
        using var doc = JsonDocument.Parse("""
            {
              "entity-types": ["entity", "agent-session"],
              "auto-resume": {
                "trusted-executor": "."
              }
            }
            """);

        var result = AutoResumeService.ReadFromEntityData(doc.RootElement);

        Assert.NotNull(result);
        Assert.Equal(".", result!.TrustedExecutor);
        Assert.Null(result.ResumePrompt);
    }

    [Fact]
    public void ReadFromEntityData_WhenAutoResumeIsAbsent_ReturnsNull()
    {
        using var doc = JsonDocument.Parse("""
            {
              "entity-types": ["entity", "agent-session"]
            }
            """);

        var result = AutoResumeService.ReadFromEntityData(doc.RootElement);

        Assert.Null(result);
    }

    [Fact]
    public void ReadFromEntityData_WhenEntityDataIsNull_ReturnsNull()
    {
        var result = AutoResumeService.ReadFromEntityData(null);

        Assert.Null(result);
    }

    [Fact]
    public void ReadFromEntityData_WhenTrustedExecutorIsEmpty_ReturnsNull()
    {
        using var doc = JsonDocument.Parse("""
            {
              "entity-types": ["entity", "agent-session"],
              "auto-resume": {
                "trusted-executor": "   "
              }
            }
            """);

        var result = AutoResumeService.ReadFromEntityData(doc.RootElement);

        Assert.Null(result);
    }

    // --- FindMatchingSessionsAsync tests ---

    [Fact]
    public async Task FindMatchingSessionsAsync_WithMatchingLocalExecutor_ReturnsSession()
    {
        var entityId = new EntityId("aaaabbbb-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var agentSessionId = "test-session-auto-resume-local";
        var dal = await CreateDataAccessLayerAsync(
            CreateAgentSessionEntity(entityId, agentSessionId, trustedExecutor: ".", resumePrompt: null));

        var results = await AutoResumeService.FindMatchingSessionsAsync(dal, ".", TestContext.Current.CancellationToken);

        var session = Assert.Single(results);
        Assert.Equal(entityId, session.EntityId);
        Assert.Equal(agentSessionId, session.AgentSessionId);
        Assert.Equal(AutoResumeService.DefaultResumePrompt, session.ResumePrompt);
    }

    [Fact]
    public async Task FindMatchingSessionsAsync_WithCustomResumePrompt_UsesCustomPrompt()
    {
        var entityId = new EntityId("aaaabbbb-aaaa-4aaa-8aaa-aaaaaaaaaaab");
        var agentSessionId = "test-session-auto-resume-custom-prompt";
        var dal = await CreateDataAccessLayerAsync(
            CreateAgentSessionEntity(entityId, agentSessionId, trustedExecutor: ".", resumePrompt: "My custom prompt"));

        var results = await AutoResumeService.FindMatchingSessionsAsync(dal, ".", TestContext.Current.CancellationToken);

        var session = Assert.Single(results);
        Assert.Equal("My custom prompt", session.ResumePrompt);
    }

    [Fact]
    public async Task FindMatchingSessionsAsync_WithNonMatchingExecutor_ReturnsEmpty()
    {
        var entityId = new EntityId("aaaabbbb-aaaa-4aaa-8aaa-aaaaaaaaaaac");
        var agentSessionId = "test-session-auto-resume-non-matching";
        var dal = await CreateDataAccessLayerAsync(
            CreateAgentSessionEntity(entityId, agentSessionId, trustedExecutor: "different-executor", resumePrompt: null));

        var results = await AutoResumeService.FindMatchingSessionsAsync(dal, ".", TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task FindMatchingSessionsAsync_WithNoAutoResume_ReturnsEmpty()
    {
        var entityId = new EntityId("aaaabbbb-aaaa-4aaa-8aaa-aaaaaaaaaaad");
        var agentSessionId = "test-session-no-auto-resume";
        var dal = await CreateDataAccessLayerAsync(
            CreateAgentSessionEntityWithNoAutoResume(entityId, agentSessionId));

        var results = await AutoResumeService.FindMatchingSessionsAsync(dal, ".", TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task FindMatchingSessionsAsync_WithMultipleSessions_ReturnsOnlyMatching()
    {
        var matchingEntityId = new EntityId("aaaabbbb-aaaa-4aaa-8aaa-aaaaaaaaaaae");
        var nonMatchingEntityId = new EntityId("aaaabbbb-aaaa-4aaa-8aaa-aaaaaaaaaaaf");
        var dal = await CreateDataAccessLayerAsync(
            CreateAgentSessionEntity(matchingEntityId, "matching-session", trustedExecutor: ".", resumePrompt: null),
            CreateAgentSessionEntity(nonMatchingEntityId, "non-matching-session", trustedExecutor: "other-executor", resumePrompt: null));

        var results = await AutoResumeService.FindMatchingSessionsAsync(dal, ".", TestContext.Current.CancellationToken);

        var session = Assert.Single(results);
        Assert.Equal(matchingEntityId, session.EntityId);
    }

    private static async Task<IDataAccessLayer> CreateDataAccessLayerAsync(params JsonElement[] entities)
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        await fixture.SeedManyValidAsync(entities);
        return fixture.DataAccessLayer;
    }

    private static JsonElement CreateAgentSessionEntity(
        EntityId entityId,
        string agentSessionId,
        string trustedExecutor,
        string? resumePrompt)
    {
        var resumePromptJson = resumePrompt is not null
            ? $",\"resume-prompt\":{JsonSerializer.Serialize(resumePrompt)}"
            : string.Empty;
        using var doc = JsonDocument.Parse($$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "agent-session"],
              "agent-session-id": "{{agentSessionId}}",
              "auto-resume": {
                "trusted-executor": "{{trustedExecutor}}"{{resumePromptJson}}
              }
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement CreateAgentSessionEntityWithNoAutoResume(
        EntityId entityId,
        string agentSessionId)
    {
        using var doc = JsonDocument.Parse($$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "agent-session"],
              "agent-session-id": "{{agentSessionId}}"
            }
            """);
        return doc.RootElement.Clone();
    }
}
