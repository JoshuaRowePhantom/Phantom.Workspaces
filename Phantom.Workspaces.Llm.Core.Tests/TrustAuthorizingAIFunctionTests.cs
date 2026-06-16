using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Trust;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class TrustAuthorizingAIFunctionTests
{
    private static TrustProfile ProfileAllowing(params string[] allowedToolNames)
    {
        var schemas = new List<JsonObject>();
        foreach (var toolName in allowedToolNames)
        {
            schemas.Add(new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    ["toolName"] = new JsonObject { ["const"] = toolName },
                },
            });
        }

        return TrustProfileComposer.Compose(
        [
            new TrustProfileDefinition
            {
                HostingWorkspacesClientInstances = ["."],
                AllowedMcpToolCallSchemas = schemas,
            },
        ]);
    }

    [Fact]
    public async Task InvokeAsync_AllowedTool_ExecutesUnderlyingFunction()
    {
        var authorizer = new TrustToolCallAuthorizer(ProfileAllowing("echo_tool"));
        var inner = AIFunctionFactory.Create((string value) => $"echoed:{value}", "echo_tool", "Echoes a value.");
        var authorized = new TrustAuthorizingAIFunction(inner, authorizer);

        var result = await authorized.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["value"] = "hello" }),
            CancellationToken.None);

        Assert.Contains("echoed:hello", result?.ToString());
    }

    [Fact]
    public async Task InvokeAsync_DeniedTool_ReturnsDenialWithoutExecuting()
    {
        var authorizer = new TrustToolCallAuthorizer(ProfileAllowing("echo_tool"));
        var executed = false;
        var inner = AIFunctionFactory.Create(
            (string value) =>
            {
                executed = true;
                return value;
            },
            "delete_tool",
            "Deletes things.");
        var authorized = new TrustAuthorizingAIFunction(inner, authorizer);

        var result = await authorized.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["value"] = "x" }),
            CancellationToken.None);

        Assert.False(executed);
        Assert.Contains("denied by the trust profile", result?.ToString());
    }

    [Fact]
    public void Apply_WrapsFunctionToolsWithAuthorizer()
    {
        var inner = AIFunctionFactory.Create((string value) => value, "echo_tool", "Echoes.");
        var chatOptions = new ChatOptions { Tools = [inner] };

        TrustToolAuthorization.Apply(chatOptions, ProfileAllowing("echo_tool"));

        Assert.IsType<TrustAuthorizingAIFunction>(Assert.Single(chatOptions.Tools!));
    }
}
