using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Tests;

/// <summary>
/// #1485: contract tests for <see cref="AgentChatModal"/> and its content hierarchy.
/// </summary>
public sealed class AgentChatModalContractTests
{
    [Fact]
    public void FreeformModalContent_TypeDiscriminator_IsFreeform()
    {
        var content = new FreeformModalContent { IsRequired = true };
        Assert.Equal("freeform", content.Type);
    }

    [Fact]
    public void MultipleChoiceModalContent_TypeDiscriminator_IsMultipleChoice()
    {
        var content = new MultipleChoiceModalContent
        {
            Options = Array.Empty<JsonElement>(),
            AllowsMultiple = false,
        };
        Assert.Equal("multiple-choice", content.Type);
    }

    [Fact]
    public void ApprovalModalContent_TypeDiscriminator_IsApproval()
    {
        var content = new ApprovalModalContent { ApproveLabel = "Yes", RejectLabel = "No" };
        Assert.Equal("approval", content.Type);
    }

    [Fact]
    public void AgentChatModal_RequiredMembers_AreMarkedRequired()
    {
        var required = typeof(AgentChatModal)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttributesData()
                .Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.RequiredMemberAttribute"))
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains(nameof(AgentChatModal.Id), required);
        Assert.Contains(nameof(AgentChatModal.OwnerAgentId), required);
        Assert.Contains(nameof(AgentChatModal.Title), required);
        Assert.Contains(nameof(AgentChatModal.Body), required);
        Assert.Contains(nameof(AgentChatModal.Content), required);
    }

    [Fact]
    public void AgentChatModal_EqualValuesWithSameContent_CompareEqual()
    {
        AgentChatModalContent content = new ApprovalModalContent { ApproveLabel = "Y", RejectLabel = "N" };
        var a = new AgentChatModal
        {
            Id = "m1",
            OwnerAgentId = "owner",
            Title = "t",
            Body = "b",
            Content = content,
        };
        var b = new AgentChatModal
        {
            Id = "m1",
            OwnerAgentId = "owner",
            Title = "t",
            Body = "b",
            Content = new ApprovalModalContent { ApproveLabel = "Y", RejectLabel = "N" },
        };
        Assert.Equal(a, b);
    }
}
