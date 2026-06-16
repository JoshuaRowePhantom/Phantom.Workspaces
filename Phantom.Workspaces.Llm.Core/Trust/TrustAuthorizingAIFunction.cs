using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// Wraps an <see cref="AIFunction"/> so each invocation is authorized against a trust profile's
/// tool-call policy via <see cref="TrustToolCallAuthorizer"/>. A denied call returns a denial message
/// instead of executing the underlying tool, enforcing the profile during (remote) execution.
/// </summary>
public sealed class TrustAuthorizingAIFunction : AIFunction
{
    private readonly AIFunction innerFunction;
    private readonly TrustToolCallAuthorizer authorizer;

    public TrustAuthorizingAIFunction(AIFunction innerFunction, TrustToolCallAuthorizer authorizer)
    {
        ArgumentNullException.ThrowIfNull(innerFunction);
        ArgumentNullException.ThrowIfNull(authorizer);
        this.innerFunction = innerFunction;
        this.authorizer = authorizer;
    }

    public override string Name => this.innerFunction.Name;

    public override string Description => this.innerFunction.Description;

    public override JsonElement JsonSchema => this.innerFunction.JsonSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        if (!this.authorizer.IsToolCallAllowed(this.Name, ToInput(arguments)))
        {
            return $"Tool call '{this.Name}' was denied by the trust profile.";
        }

        return await this.innerFunction.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject ToInput(AIFunctionArguments arguments)
    {
        var input = new JsonObject();
        if (arguments is not null)
        {
            foreach (var argument in arguments)
            {
                input[argument.Key] = argument.Value is null
                    ? null
                    : JsonSerializer.SerializeToNode(argument.Value);
            }
        }

        return input;
    }
}
