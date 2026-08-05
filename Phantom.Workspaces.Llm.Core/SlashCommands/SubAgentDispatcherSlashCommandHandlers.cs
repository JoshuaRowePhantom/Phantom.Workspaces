using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Llm.SlashCommands;

/// <summary>
/// Handles <c>/available-subagents</c>: lists every <c>agent-definition</c> tool entry the dispatcher
/// can instantiate. Takes no arguments.
/// </summary>
internal sealed class AvailableSubAgentsSlashCommandHandler : ISlashCommandHandler
{
    private readonly ISubAgentDispatcherCommandClient client;

    public AvailableSubAgentsSlashCommandHandler(ISubAgentDispatcherCommandClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        this.client = client;
    }

    public string Name => "available-subagents";

    public string Description => "List the sub-agent definitions this dispatcher can instantiate";

    public string? Usage => "/available-subagents";

    public string? LongDescription => null;

    public Task<SlashCommandResult> ExecuteAsync(
        SlashCommandContext context,
        string arguments,
        CancellationToken cancellationToken)
    {
        var definitions = this.client.AvailableDefinitions;
        if (definitions.Count == 0)
        {
            return Task.FromResult(new SlashCommandResult
            {
                StatusMessage = "No sub-agent definitions are available.",
            });
        }

        var builder = new StringBuilder();
        builder.Append("Available sub-agent definitions:");
        foreach (var definition in definitions)
        {
            builder.Append('\n').Append("  ").Append(definition.Name).Append("  — ").Append(definition.Description);
        }

        return Task.FromResult(new SlashCommandResult { StatusMessage = builder.ToString() });
    }
}

/// <summary>
/// Handles <c>/new-subagent &lt;definition-id&gt; [subagent-id] [prompt]</c>: the slash-command wrapper for the
/// <c>new(&lt;def&gt; &lt;id&gt;):</c> routing prefix.
/// </summary>
internal sealed class NewSubAgentSlashCommandHandler : ISlashCommandHandler
{
    private readonly ISubAgentDispatcherCommandClient client;

    public NewSubAgentSlashCommandHandler(ISubAgentDispatcherCommandClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        this.client = client;
    }

    public string Name => "new-subagent";

    public string Description => "Create a new sub-agent from a named definition";

    public string? Usage => "/new-subagent <definition-id> [subagent-id] [prompt]";

    public string? LongDescription =>
        "Creates a new sub-agent from the named definition. Equivalent to sending \"new(<definition-id> <subagent-id>): <prompt>\".";

    public Task<IReadOnlyList<SlashCommandCompletion>> GetCompletionsAsync(
        SlashCommandContext context,
        string partialArguments,
        CancellationToken cancellationToken)
    {
        // Completions are only offered for the first token (the definition id). Once a whitespace
        // separator has been typed the caller is entering the sub-agent id / prompt, for which no
        // completion is offered.
        if (SubAgentSlashCommandParsing.ContainsWhitespace(partialArguments.TrimStart()))
        {
            return Task.FromResult<IReadOnlyList<SlashCommandCompletion>>(Array.Empty<SlashCommandCompletion>());
        }

        var prefix = partialArguments.Trim();
        var completions = this.client.AvailableDefinitions
            .Where(definition => definition.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(definition => new SlashCommandCompletion(
                CompletionText: definition.Name,
                Label: definition.Name,
                Description: definition.Description))
            .ToArray();

        return Task.FromResult<IReadOnlyList<SlashCommandCompletion>>(completions);
    }

    public Task<SlashCommandResult> ExecuteAsync(
        SlashCommandContext context,
        string arguments,
        CancellationToken cancellationToken)
    {
        var (definitionId, subAgentId, prompt) = SubAgentSlashCommandParsing.ParseNewSubAgent(arguments);
        if (definitionId.Length == 0)
        {
            return Task.FromResult(new SlashCommandResult
            {
                StatusMessage = "Usage: /new-subagent <definition-id> [subagent-id] [prompt]",
            });
        }

        var routingArgs = subAgentId.Length == 0 ? definitionId : $"{definitionId} {subAgentId}";

        if (prompt.Length == 0)
        {
            return Task.FromResult(new SlashCommandResult
            {
                StatusMessage =
                    $"Type a prompt to create sub-agent from \"{definitionId}\" (equivalent to \"new({routingArgs}): <prompt>\").",
            });
        }

        var message = $"new({routingArgs}): {prompt}";
        context.AgentChat.EnqueueUserMessage(message);

        var label = subAgentId.Length == 0 ? definitionId : subAgentId;
        return Task.FromResult(new SlashCommandResult { StatusMessage = $"Creating sub-agent \"{label}\"." });
    }
}

/// <summary>
/// Handles <c>/subagent &lt;subagent-id&gt; [message]</c>: routes a message to an existing sub-agent. Equivalent
/// to the <c>&lt;id&gt;:</c> routing prefix.
/// </summary>
internal sealed class SubAgentSlashCommandHandler : ISlashCommandHandler
{
    private readonly ISubAgentDispatcherCommandClient client;

    public SubAgentSlashCommandHandler(ISubAgentDispatcherCommandClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        this.client = client;
    }

    public string Name => "subagent";

    public string Description => "Route a message to an existing sub-agent";

    public string? Usage => "/subagent <subagent-id> [message]";

    public string? LongDescription =>
        "Routes the message to the named sub-agent. Equivalent to sending \"<subagent-id>: <message>\".";

    public Task<IReadOnlyList<SlashCommandCompletion>> GetCompletionsAsync(
        SlashCommandContext context,
        string partialArguments,
        CancellationToken cancellationToken)
    {
        if (SubAgentSlashCommandParsing.ContainsWhitespace(partialArguments.TrimStart()))
        {
            return Task.FromResult<IReadOnlyList<SlashCommandCompletion>>(Array.Empty<SlashCommandCompletion>());
        }

        var prefix = partialArguments.Trim();
        var completions = this.client.ActiveSubAgents
            .Where(subAgent => subAgent.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(subAgent => new SlashCommandCompletion(
                CompletionText: subAgent.Id,
                Label: subAgent.Id,
                Description: subAgent.Description))
            .ToArray();

        return Task.FromResult<IReadOnlyList<SlashCommandCompletion>>(completions);
    }

    public Task<SlashCommandResult> ExecuteAsync(
        SlashCommandContext context,
        string arguments,
        CancellationToken cancellationToken)
    {
        var (subAgentId, message) = SubAgentSlashCommandParsing.ParseSubAgent(arguments);
        if (subAgentId.Length == 0)
        {
            return Task.FromResult(new SlashCommandResult
            {
                StatusMessage = "Usage: /subagent <subagent-id> [message]",
            });
        }

        if (message.Length == 0)
        {
            return Task.FromResult(new SlashCommandResult
            {
                StatusMessage = $"Type a message to route to sub-agent \"{subAgentId}\" (equivalent to \"{subAgentId}: <message>\").",
            });
        }

        context.AgentChat.EnqueueUserMessage($"{subAgentId}: {message}");
        return Task.FromResult(new SlashCommandResult { StatusMessage = $"Routing to sub-agent \"{subAgentId}\"." });
    }
}

/// <summary>Pure parsing helpers shared by the sub-agent dispatcher slash-command handlers.</summary>
internal static class SubAgentSlashCommandParsing
{
    private static readonly char[] Whitespace = [' ', '\t', '\r', '\n', '\f', '\v'];

    public static bool ContainsWhitespace(string value) => value.IndexOfAny(Whitespace) >= 0;

    /// <summary>
    /// Parses <c>&lt;definition-id&gt; [subagent-id] [prompt]</c> into its three components.
    /// The prompt preserves the remainder verbatim (including internal whitespace).
    /// </summary>
    public static (string DefinitionId, string SubAgentId, string Prompt) ParseNewSubAgent(string arguments)
    {
        var (definitionId, afterDefinition) = SplitFirstToken(arguments);
        if (definitionId.Length == 0)
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        var (subAgentId, afterSubAgent) = SplitFirstToken(afterDefinition);
        var prompt = afterSubAgent.TrimStart();
        return (definitionId, subAgentId, prompt);
    }

    /// <summary>Parses <c>&lt;subagent-id&gt; [message]</c>.</summary>
    public static (string SubAgentId, string Message) ParseSubAgent(string arguments)
    {
        var (subAgentId, afterId) = SplitFirstToken(arguments);
        return (subAgentId, afterId.TrimStart());
    }

    private static (string Token, string Remainder) SplitFirstToken(string value)
    {
        var trimmed = value.TrimStart();
        if (trimmed.Length == 0)
        {
            return (string.Empty, string.Empty);
        }

        var separatorIndex = trimmed.IndexOfAny(Whitespace);
        return separatorIndex < 0
            ? (trimmed, string.Empty)
            : (trimmed[..separatorIndex], trimmed[(separatorIndex + 1)..]);
    }
}
