using System.Text.Json;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class AgentSessionModalViewModel
{
    private readonly AgentViewModel owner;

    internal AgentSessionModalViewModel(AgentViewModel owner, AgentChatModal modal)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        ArgumentNullException.ThrowIfNull(modal);
        this.Id = modal.Id;
        this.Title = modal.Title;
        this.Body = modal.Body;
        this.Content = modal.Content;
    }

    public string Id { get; }
    public string Title { get; }
    public string Body { get; }
    public AgentChatModalContent Content { get; }

    public Task RespondAsync(JsonElement response, CancellationToken ct = default)
    {
        this.ValidateResponse(response);
        return this.owner.RespondToModalAsync(this.Id, response, ct);
    }

    private void ValidateResponse(JsonElement response)
    {
        switch (this.Content)
        {
            case FreeformModalContent freeform:
                if (response.ValueKind != JsonValueKind.String
                    || (freeform.IsRequired && string.IsNullOrWhiteSpace(response.GetString())))
                {
                    throw new ArgumentException("A valid text response is required.", nameof(response));
                }
                break;

            case MultipleChoiceModalContent choices:
                if (choices.AllowsMultiple)
                {
                    if (response.ValueKind != JsonValueKind.Array
                        || response.EnumerateArray().Any(option => !Contains(choices.Options, option)))
                    {
                        throw new ArgumentException("The response must contain only offered choices.", nameof(response));
                    }
                }
                else if (!Contains(choices.Options, response))
                {
                    throw new ArgumentException("The response must be one of the offered choices.", nameof(response));
                }
                break;

            case ApprovalModalContent:
                if (response.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new ArgumentException("The response must be an approval boolean.", nameof(response));
                }
                break;

            default:
                throw new ArgumentException("The modal content type is not supported.", nameof(response));
        }
    }

    private static bool Contains(IReadOnlyList<JsonElement> options, JsonElement candidate)
        => options.Any(option => string.Equals(
            option.GetRawText(),
            candidate.GetRawText(),
            StringComparison.Ordinal));
}
