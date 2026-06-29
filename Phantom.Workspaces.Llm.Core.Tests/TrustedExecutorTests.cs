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
    public void Selector_LocalInstance_SelectsLocalExecutor()
    {
        var profile = BuildProfileWithTools("read_file");
        var local = new LocalTrustedExecutor();
        var selector = new TrustedExecutorSelector([local, new FakeRemoteExecutor()]);

        var selected = selector.SelectExecutor(profile, TrustProfile.LocalClientInstance);

        Assert.Same(local, selected);
    }

    [Fact]
    public void Selector_RemoteInstance_SelectsRemoteExecutor()
    {
        var definition = new TrustProfileDefinition
        {
            HostingWorkspacesClientInstances = ["remote-a"],
        };
        var profile = TrustProfileComposer.Compose([definition]);
        var remote = new FakeRemoteExecutor();
        var selector = new TrustedExecutorSelector([new LocalTrustedExecutor(), remote]);

        var selected = selector.SelectExecutor(profile, "remote-a");

        Assert.Same(remote, selected);
    }

    [Fact]
    public void Selector_InstanceNotPermitted_Throws()
    {
        var profile = BuildProfileWithTools("read_file"); // permits only "."
        var selector = new TrustedExecutorSelector([new LocalTrustedExecutor()]);

        var exception = Assert.Throws<InvalidOperationException>(
            () => selector.SelectExecutor(profile, "remote-a"));
        Assert.Contains("does not permit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Selector_NoExecutorForInstance_Throws()
    {
        var definition = new TrustProfileDefinition
        {
            HostingWorkspacesClientInstances = ["remote-a"],
        };
        var profile = TrustProfileComposer.Compose([definition]);
        var selector = new TrustedExecutorSelector([new LocalTrustedExecutor()]);

        Assert.Throws<InvalidOperationException>(
            () => selector.SelectExecutor(profile, "remote-a"));
    }

    [Fact]
    public void LocalExecutor_CanExecute_OnlyLocal()
    {
        var local = new LocalTrustedExecutor();

        Assert.True(local.CanExecute("."));
        Assert.False(local.CanExecute("remote-a"));
    }

    private sealed class FakeRemoteExecutor : ITrustedExecutor
    {
        public bool CanExecute(string targetClientInstance)
            => !string.Equals(targetClientInstance, TrustProfile.LocalClientInstance, StringComparison.Ordinal);

        public Task<AgentChat> CreateAgentChatAsync(
            TrustedExecutionRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Stream> OpenStreamAsync(TrustedStreamRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
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
}
