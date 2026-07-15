using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Phantom.Workspaces.Llm.Shell;
using Phantom.Workspaces.Llm.Trust;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class TrustedExecutorTests
{
    private static TrustProfile BuildProfileWithTools(params string[] allowedToolNames)
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

        var definition = new TrustProfileDefinition
        {
            HostingWorkspacesClientInstances = ["."],
            AllowedMcpToolCallSchemas = schemas,
        };

        return TrustProfileComposer.Compose([definition]);
    }

    [Fact]
    public void Authorizer_AllowsListedTool_DeniesOthers()
    {
        var profile = BuildProfileWithTools("read_file", "write_file");
        var authorizer = new TrustToolCallAuthorizer(profile);

        Assert.True(authorizer.IsToolCallAllowed("read_file", new JsonObject { ["path"] = "/a" }));
        Assert.True(authorizer.IsToolCallAllowed("write_file", new JsonObject { ["path"] = "/a" }));
        Assert.False(authorizer.IsToolCallAllowed("delete_file", new JsonObject { ["path"] = "/a" }));
    }

    [Fact]
    public void Authorizer_EmptyPolicy_DeniesAll()
    {
        var profile = BuildProfileWithTools();
        var authorizer = new TrustToolCallAuthorizer(profile);

        Assert.False(authorizer.IsToolCallAllowed("read_file", null));
    }

    [Fact]
    public void LocalExecutor_CanExecute_OnlyLocal()
    {
        var local = new LocalTrustedExecutor();

        Assert.True(local.CanExecute("."));
        Assert.False(local.CanExecute("remote-a"));
    }

    private static TrustedStreamRequest MakeStreamRequest(string kind = "test")
        => new()
        {
            TargetClientInstance = TrustProfile.LocalClientInstance,
            StreamKind = kind,
            OpenPayload = JsonDocument.Parse("{}").RootElement,
        };

    [Fact]
    public void LocalExecutor_OpenStreamAsync_UnknownKind_ThrowsNotImplemented()
    {
        var local = new LocalTrustedExecutor();

        Assert.Throws<NotImplementedException>(
            () => local.OpenStreamAsync(MakeStreamRequest("unknown-kind")).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task LocalExecutor_OpenStreamAsync_RegisteredHandler_ReturnsStream()
    {
        var local = new LocalTrustedExecutor();
        var handler = new FakeLocalStreamHandler();
        local.RegisterStreamHandler("shell", handler);

        var stream = await local.OpenStreamAsync(MakeStreamRequest("shell"));

        Assert.NotNull(stream);
        Assert.True(handler.WasInvoked);
        await stream.DisposeAsync();
    }

    private sealed class FakeLocalStreamHandler : ILocalStreamHandler
    {
        public bool WasInvoked { get; private set; }

        public Task HandleAsync(JsonElement openPayload, IStreamMessageChannel hostEnd, CancellationToken ct)
        {
            WasInvoked = true;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task LocalExecutor_HandleStreamAsync_UnknownKind_ThrowsNotImplemented()
    {
        var local = new LocalTrustedExecutor();
        var pair = new InMemoryStreamMessageChannelPair();

        Assert.Throws<NotImplementedException>(
            () => local.HandleStreamAsync("unknown-kind", JsonDocument.Parse("{}").RootElement, pair.HostEnd).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task LocalExecutor_HandleStreamAsync_RegisteredHandler_InvokesHandler()
    {
        var local = new LocalTrustedExecutor();
        var handler = new FakeLocalStreamHandler();
        local.RegisterStreamHandler("shell", handler);

        var pair = new InMemoryStreamMessageChannelPair();
        await local.HandleStreamAsync("shell", JsonDocument.Parse("{}").RootElement, pair.HostEnd);

        Assert.True(handler.WasInvoked);
    }

    [Fact]
    public async Task LocalExecutor_OpenStreamAsync_ShellKind_ReturnsStream()
    {
        var exitTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pty = new FakePseudoTerminal(exitTcs);
        var local = new LocalTrustedExecutor();
        local.RegisterStreamHandler("shell", new LocalShellStreamHandler(_ => pty));

        var shellRequest = new TrustedStreamRequest
        {
            TargetClientInstance = TrustProfile.LocalClientInstance,
            StreamKind = "shell",
            OpenPayload = JsonDocument.Parse("""{"command":"test"}""").RootElement,
        };

        var stream = await local.OpenStreamAsync(shellRequest);

        Assert.NotNull(stream);
        await stream.DisposeAsync();
        exitTcs.TrySetResult(0);
    }

    [Fact]
    public async Task LocalExecutor_RunToolAsync_DelegatesToRegisteredRunner()
    {
        var local = new LocalTrustedExecutor();
        TrustedToolRequest? received = null;
        local.RegisterToolRunner((req, _) => { received = req; return Task.CompletedTask; });

        var request = new TrustedToolRequest
        {
            ToolTypeName = "git-workspace-scan",
            ToolEntityId = Guid.NewGuid().ToString(),
            TargetClientInstance = TrustProfile.LocalClientInstance,
        };

        await local.RunToolAsync(request);

        Assert.Same(request, received);
    }

    [Fact]
    public void LocalExecutor_RunToolAsync_ThrowsWhenNoRunnerRegistered()
    {
        var local = new LocalTrustedExecutor();
        var request = new TrustedToolRequest
        {
            ToolTypeName = "git-workspace-scan",
            ToolEntityId = Guid.NewGuid().ToString(),
            TargetClientInstance = TrustProfile.LocalClientInstance,
        };

        Assert.Throws<NotSupportedException>(
            () => local.RunToolAsync(request).GetAwaiter().GetResult());
    }

    [Fact]
    public void LocalExecutor_RunToolAsync_ThrowsWhenTargetIsNotLocal()
    {
        var local = new LocalTrustedExecutor();
        var request = new TrustedToolRequest
        {
            ToolTypeName = "git-workspace-scan",
            ToolEntityId = Guid.NewGuid().ToString(),
            TargetClientInstance = "remote-a",
        };

        Assert.Throws<InvalidOperationException>(
            () => local.RunToolAsync(request).GetAwaiter().GetResult());
    }
}
