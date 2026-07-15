using System.Text.Json;
using Phantom.Workspaces.Transport.ReverseHttp;

namespace Phantom.Workspaces.Transport.Tests;

public sealed class ReverseHttpServerTransportFactoryTests
{
    [Fact]
    public async Task ReverseHttpServerTransportFactory_Registration_StoresChannel()
    {
        var factory = new ReverseHttpServerTransportFactory();
        using var request = JsonDocument.Parse("""{"type":"reverse-register","entity-id":"machine-c"}""");

        var lease = await factory.OnChannelOpenAsync(request.RootElement, new ReverseHttpClientTransportFactoryTests.FakeMessageChannel());

        Assert.NotNull(lease);
        Assert.True(factory.IsRegistered("machine-c"));
        Assert.Equal(1, factory.RegistrationCount);
    }

    [Fact]
    public async Task ReverseHttpServerTransportFactory_Registration_Dispose_RemovesChannel()
    {
        var factory = new ReverseHttpServerTransportFactory();
        using var request = JsonDocument.Parse("""{"type":"reverse-register","entity-id":"machine-c"}""");
        var lease = await factory.OnChannelOpenAsync(request.RootElement, new ReverseHttpClientTransportFactoryTests.FakeMessageChannel());

        await lease!.DisposeAsync();

        Assert.False(factory.IsRegistered("machine-c"));
        Assert.Equal(0, factory.RegistrationCount);
    }

    [Fact]
    public async Task ReverseHttpServerTransportFactory_Lookup_NotRegistered_SendsError()
    {
        var factory = new ReverseHttpServerTransportFactory();
        var channel = new ReverseHttpClientTransportFactoryTests.FakeMessageChannel();
        using var request = JsonDocument.Parse("""{"type":"reverse-http","entity-id":"missing-machine"}""");

        var lease = await factory.OnChannelOpenAsync(request.RootElement, channel);
        await lease!.DisposeAsync();
        var error = await channel.Reader.ReadAsync();

        Assert.Equal("channel-open-error", error.GetProperty("type").GetString());
        Assert.Equal("not-registered", error.GetProperty("error-code").GetString());
        Assert.True(channel.Reader.Completion.IsCompleted);
    }

    [Fact]
    public async Task ReverseHttpServerTransportFactory_MultipleRegistrations_IndependentSlots()
    {
        var factory = new ReverseHttpServerTransportFactory();
        using var firstRequest = JsonDocument.Parse("""{"type":"reverse-register","entity-id":"first-machine"}""");
        using var secondRequest = JsonDocument.Parse("""{"type":"reverse-register","entity-id":"second-machine"}""");
        var firstLease = await factory.OnChannelOpenAsync(firstRequest.RootElement, new ReverseHttpClientTransportFactoryTests.FakeMessageChannel());
        var secondLease = await factory.OnChannelOpenAsync(secondRequest.RootElement, new ReverseHttpClientTransportFactoryTests.FakeMessageChannel());

        await firstLease!.DisposeAsync();

        Assert.False(factory.IsRegistered("first-machine"));
        Assert.True(factory.IsRegistered("second-machine"));
        Assert.Equal(1, factory.RegistrationCount);

        await secondLease!.DisposeAsync();
    }

    [Fact]
    public async Task ReverseHttpServerTransportFactory_OtherDescriptors_ReturnsNull()
    {
        var factory = new ReverseHttpServerTransportFactory();
        using var request = JsonDocument.Parse("""{"type":"http","entity-id":"machine-c"}""");

        var lease = await factory.OnChannelOpenAsync(request.RootElement, new ReverseHttpClientTransportFactoryTests.FakeMessageChannel());

        Assert.Null(lease);
        Assert.Equal(0, factory.RegistrationCount);
    }

    [Fact]
    public async Task ReverseHttpServerTransportFactory_ConcurrentAccess_ThreadSafe()
    {
        var factory = new ReverseHttpServerTransportFactory();
        var leases = await Task.WhenAll(Enumerable.Range(0, 50).Select(async index =>
        {
            using var request = JsonDocument.Parse($$"""{"type":"reverse-register","entity-id":"machine-{{index}}"}""");
            return await factory.OnChannelOpenAsync(request.RootElement, new ReverseHttpClientTransportFactoryTests.FakeMessageChannel());
        }));

        Assert.Equal(50, factory.RegistrationCount);

        await Task.WhenAll(leases.Select(static lease => lease!.DisposeAsync().AsTask()));

        Assert.Equal(0, factory.RegistrationCount);
    }
}
