using System.Collections.Immutable;

namespace Phantom.Workspaces.Llm;

public sealed record LlmSession
{
    public required ImmutableList<LlmConversation> Conversations { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}
