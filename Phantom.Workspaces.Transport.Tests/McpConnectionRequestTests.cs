using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Transport.Mcp;

namespace Phantom.Workspaces.Transport.Tests;

/// <summary>
/// Covers <see cref="McpConnectionRequest"/> under the #1477 trust-profile wire additions: the
/// launch host resolves stored profiles by reference and rejects any attempt to inject a compiled
/// MXC policy across the wire.
/// </summary>
public sealed class McpConnectionRequestTests
{
    [Fact]
    public void RemoteRequest_CompiledPolicyProperty_IsNotAccepted()
    {
        // The wire model has no compiled-policy shape. Attempting to inject one must fail closed —
        // the launch host, not the caller, is the sole compiler.
        var request = Parse("""
        {
          "type":"mcp",
          "connection":{
            "server-name":"srv",
            "endpoint":"stdio://?command=cmd",
            "compiled-policy":{"anything":true}
          }
        }
        """);

        var ex = Assert.Throws<InvalidOperationException>(
            () => McpConnectionRequest.RejectCompiledPolicyProperty(request));
        Assert.Contains("compiled-policy", ex.Message);
    }

    [Fact]
    public void RemoteRequest_TrustProfileReference_ReadableFromWire()
    {
        var request = Parse("""
        {
          "type":"mcp",
          "connection":{
            "server-name":"srv",
            "endpoint":"stdio://?command=cmd",
            "trust-profile-ref":"my-profile",
            "trust-profile-revision":"rev-42"
          }
        }
        """);

        var present = McpConnectionRequest.TryGetTrustProfileReference(
            request, out var reference, out var revision);
        Assert.True(present);
        Assert.Equal("my-profile", reference);
        Assert.Equal("rev-42", revision);
    }

    [Fact]
    public void FromToolWithTrustProfileReference_EmitsReferenceAndRevision()
    {
        var tool = new McpTool
        {
            ServerName = "srv",
            Connection = new AnonymousConnection { Endpoint = "stdio://?command=cmd" },
        };
        var wire = McpConnectionRequest.FromToolWithTrustProfileReference(tool, "my-profile", "rev-42");

        Assert.True(McpConnectionRequest.TryGetTrustProfileReference(wire, out var reference, out var revision));
        Assert.Equal("my-profile", reference);
        Assert.Equal("rev-42", revision);
        // Round-trip: RejectCompiledPolicyProperty must not throw when no compiled policy is set.
        McpConnectionRequest.RejectCompiledPolicyProperty(wire);
    }

    [Theory]
    [InlineData("""{"type":"mcp","connection":{"trust-profile-ref":"restricted"}}""")]
    [InlineData("""{"type":"mcp","connection":{"trust-profile-revision":"7"}}""")]
    [InlineData("""{"type":"mcp","connection":{"trust-profile-ref":42,"trust-profile-revision":"7"}}""")]
    [InlineData("""{"type":"mcp","connection":{"trust-profile-ref":"restricted","trust-profile-revision":7}}""")]
    [InlineData("""{"type":"mcp","connection":{"trust-profile-ref":" ","trust-profile-revision":"7"}}""")]
    [InlineData("""{"type":"mcp","connection":{"trust-profile-ref":"restricted","trust-profile-revision":" "}}""")]
    public void RemoteRequest_MalformedTrustIntent_FailsClosed(string json)
    {
        var request = Parse(json);

        Assert.Throws<InvalidOperationException>(
            () => McpConnectionRequest.TryGetTrustProfileReference(
                request,
                out _,
                out _));
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
