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
            Options = new[] { JsonDocument.Parse("\"a\"").RootElement, JsonDocument.Parse("\"b\"").RootElement },
            AllowsMultiple = false,
        };
        Assert.Equal("multiple-choice", content.Type);
    }

    [Fact]
    public void MultipleChoiceModalContent_DuplicateOption_Rejected()
    {
        // #1485: publisher-side validation rejects duplicate options so callers cannot publish an
        // ambiguous multiple-choice modal.
        Assert.Throws<ArgumentException>(() => new MultipleChoiceModalContent
        {
            Options = new[] { JsonDocument.Parse("\"x\"").RootElement, JsonDocument.Parse("\"x\"").RootElement },
            AllowsMultiple = false,
        });
    }

    [Fact]
    public void MultipleChoiceModalContent_OptionsMutationAfterInit_DoesNotAffectContent()
    {
        // #1485: options are deep-cloned via JsonElement.Clone so caller mutations of the source
        // JsonDocument cannot affect the published modal.
        using var doc = JsonDocument.Parse("[\"a\", \"b\"]");
        var options = new[] { doc.RootElement[0], doc.RootElement[1] };
        var content = new MultipleChoiceModalContent { Options = options, AllowsMultiple = false };
        var before = content.Options[0].GetString();
        doc.Dispose(); // Underlying document is torn down; cloned options must still be readable.
        var after = content.Options[0].GetString();
        Assert.Equal(before, after);
        Assert.Equal("a", after);
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
