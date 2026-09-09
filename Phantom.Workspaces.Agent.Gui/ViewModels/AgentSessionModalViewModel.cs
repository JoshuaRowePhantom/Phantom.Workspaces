using System.Text.Json;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class AgentSessionModalViewModel
{
    private readonly AgentViewModel owner;

    internal AgentSessionModalViewModel(AgentViewModel owner, AgentChatModal modal, bool isDescendant)
    {
        this.owner = owner;
        this.Id = modal.Id;
        this.OwnerAgentId = modal.OwnerAgentId;
        this.Title = modal.Title;
        this.Body = modal.Body;
        this.Content = modal.Content;
        this.IsDescendant = isDescendant;
    }

    public string Id { get; }
    public string OwnerAgentId { get; }
    public string Title { get; }
    public string Body { get; }
    public AgentChatModalContent Content { get; }
    public bool IsDescendant { get; }

    public Task RespondAsync(JsonElement response, CancellationToken ct = default)
        => this.owner.RespondToModalAsync(this.Id, response, ct);
}
