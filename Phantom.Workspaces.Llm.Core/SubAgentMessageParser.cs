using System.Text.RegularExpressions;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// The parsed result of examining the dispatcher's last user message for a routing prefix.
/// Exactly one concrete subtype is produced per message.
/// </summary>
public abstract record SubAgentParseResult;

/// <summary>
/// Instruction to create a new sub-agent. The concrete sub-agent id is intentionally NOT computed
/// here: slug generation is deferred to the executor (see #1020) so this building block builds
/// independently. <see cref="ExplicitId"/> carries an explicit id token when one was supplied
/// (<c>new(def id):</c>); otherwise the executor generates a slug from <see cref="Prompt"/>, and
/// <see cref="PrefixSlugWithDefinitionName"/> indicates whether that slug should be prefixed with
/// the definition name (<c>new(def):</c> → <c>def-&lt;slug&gt;</c>) or used bare (<c>new:</c> →
/// <c>&lt;slug&gt;</c>).
/// </summary>
public sealed record CreateSubAgentInstruction : SubAgentParseResult
{
    /// <summary>The resolved agent-definition template the sub-agent is created from.</summary>
    public required AgentDefinitionTool Definition { get; init; }

    /// <summary>The prompt (message body after the prefix), preserved intact including newlines.</summary>
    public required string Prompt { get; init; }

    /// <summary>An explicit sub-agent id token, when supplied. Null means "generate a slug".</summary>
    public string? ExplicitId { get; init; }

    /// <summary>
    /// When true (<c>new(def):</c>) the generated slug is prefixed with the definition name to form
    /// <c>&lt;defName&gt;-&lt;slug&gt;</c>. Ignored when <see cref="ExplicitId"/> is non-null.
    /// </summary>
    public bool PrefixSlugWithDefinitionName { get; init; }
}

/// <summary>Instruction to route <see cref="Message"/> to the sub-agent identified by <see cref="Id"/>.</summary>
public sealed record RouteToSubAgentInstruction(string Id, string Message) : SubAgentParseResult;

/// <summary>
/// Instruction to route <see cref="Message"/> to the most-recently-dispatched sub-agent, resolved
/// to <see cref="Id"/>.
/// </summary>
public sealed record RouteToMostRecentInstruction(string Id, string Message) : SubAgentParseResult;

/// <summary>An error response the dispatcher must yield back to the user without routing.</summary>
public sealed record ParseErrorInstruction(string Message) : SubAgentParseResult;

/// <summary>
/// Parses the dispatcher's whole last user message for a routing prefix. The parser is a pure
/// function of the message and the caller-supplied most-recently-dispatched id; the dispatcher owns
/// the mutable tracking of that id and passes it in.
/// </summary>
public sealed class SubAgentMessageParser
{
    internal const string UnrecognisedPrefixMessage =
        "Unrecognised prefix. Use \"new: ...\", \"new(<id>): ...\", \"<id>: ...\",\n"
        + "or \": ...\" (route to most recent sub-agent).";

    internal const string NoSubAgentDispatchedMessage =
        "No sub-agent has been dispatched yet. Use new: <prompt> to create one.";

    private static readonly Regex NewWithArgsPattern = new(
        @"^new\((?<args>[^)]+)\):\s*(?<prompt>.+)$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex NewDefaultPattern = new(
        @"^new:\s*(?<prompt>.+)$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex RouteToMostRecentPattern = new(
        @"^:\s*(?<message>.+)$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex RouteToSubAgentPattern = new(
        @"^(?<id>[^\s:]+):\s*(?<message>.+)$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly char[] ArgumentSeparators = [' ', '\t', '\r', '\n', '\f', '\v'];

    private readonly SubAgentDispatcherOptions options;

    public SubAgentMessageParser(SubAgentDispatcherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    /// <summary>Parses <paramref name="message"/> into a routing instruction.</summary>
    /// <param name="message">The entire last user message content.</param>
    /// <param name="mostRecentlyDispatchedId">
    /// The id of the most-recently-dispatched sub-agent, or null if none has been dispatched yet.
    /// Consulted only by the bare <c>:</c> (route-to-most-recent) prefix.
    /// </param>
    public SubAgentParseResult Parse(string message, string? mostRecentlyDispatchedId = null)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (NewWithArgsPattern.Match(message) is { Success: true } withArgs)
        {
            return this.ParseNewWithArgs(
                withArgs.Groups["args"].Value,
                withArgs.Groups["prompt"].Value);
        }

        if (NewDefaultPattern.Match(message) is { Success: true } newDefault)
        {
            return this.ParseNewDefault(newDefault.Groups["prompt"].Value);
        }

        if (RouteToMostRecentPattern.Match(message) is { Success: true } mostRecent)
        {
            var body = mostRecent.Groups["message"].Value;
            if (mostRecentlyDispatchedId is null)
            {
                return new ParseErrorInstruction(NoSubAgentDispatchedMessage);
            }

            return new RouteToMostRecentInstruction(mostRecentlyDispatchedId, body);
        }

        if (RouteToSubAgentPattern.Match(message) is { Success: true } routed)
        {
            return new RouteToSubAgentInstruction(
                routed.Groups["id"].Value,
                routed.Groups["message"].Value);
        }

        return new ParseErrorInstruction(UnrecognisedPrefixMessage);
    }

    private SubAgentParseResult ParseNewWithArgs(string args, string prompt)
    {
        var tokens = args.Split(ArgumentSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return new ParseErrorInstruction(UnrecognisedPrefixMessage);
        }

        var definitionName = tokens[0];
        var definition = this.FindDefinition(definitionName);
        if (definition is null)
        {
            return new ParseErrorInstruction(this.UnknownDefinitionMessage(definitionName));
        }

        if (tokens.Length >= 2)
        {
            return new CreateSubAgentInstruction
            {
                Definition = definition,
                Prompt = prompt,
                ExplicitId = tokens[1],
                PrefixSlugWithDefinitionName = false,
            };
        }

        return new CreateSubAgentInstruction
        {
            Definition = definition,
            Prompt = prompt,
            ExplicitId = null,
            PrefixSlugWithDefinitionName = true,
        };
    }

    private SubAgentParseResult ParseNewDefault(string prompt)
    {
        var definition = this.FindDefaultDefinition();
        if (definition is null)
        {
            return new ParseErrorInstruction(this.UnknownDefinitionMessage("default"));
        }

        return new CreateSubAgentInstruction
        {
            Definition = definition,
            Prompt = prompt,
            ExplicitId = null,
            PrefixSlugWithDefinitionName = false,
        };
    }

    private AgentDefinitionTool? FindDefinition(string name)
    {
        foreach (var tool in this.options.AgentDefinitionTools)
        {
            if (string.Equals(tool.Name, name, StringComparison.Ordinal))
            {
                return tool;
            }
        }

        return null;
    }

    private AgentDefinitionTool? FindDefaultDefinition()
    {
        return this.FindDefinition("default")
            ?? (this.options.AgentDefinitionTools.Count > 0 ? this.options.AgentDefinitionTools[0] : null);
    }

    private string UnknownDefinitionMessage(string name)
    {
        var available = this.options.AgentDefinitionTools.Count == 0
            ? "(none)"
            : string.Join(", ", this.options.AgentDefinitionTools.Select(static tool => tool.Name));
        return $"Unknown agent definition '{name}'. Available: {available}.";
    }
}
