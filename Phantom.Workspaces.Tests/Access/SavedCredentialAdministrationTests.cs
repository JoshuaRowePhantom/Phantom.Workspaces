using System.Security;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services.Access;

namespace Phantom.Workspaces.Tests.Access;

public sealed class SavedCredentialAdministrationTests
{
    [Fact]
    public async Task Inventory_UnusedAndProviderOwnedNames_DoesNotReadValuesAndFailsClosedForManagedRows()
    {
        var store = new Store();
        store.Values["Unused"] = "value";
        store.Values["oauth:v2:test"] = "envelope";
        store.Values["mcp-oauth:legacy"] = "envelope";
        store.Values["devtunnel:legacy"] = "envelope";
        store.Values["unrecognized:internal"] = "envelope";
        var service = new SavedCredentialAdministration(store);
        var items = await service.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(5, items.Count);
        Assert.Equal(0, store.Reads);
        Assert.True(items.Single(item => item.Name == "Unused").CanManage);
        foreach (var item in items.Where(item => !item.CanManage))
        {
            await Assert.ThrowsAsync<SavedCredentialOperationException>(() => service.ReadForEditorAsync(item, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<SavedCredentialOperationException>(() => service.DeleteAsync(item, TestContext.Current.CancellationToken));
        }
        Assert.Equal(0, store.Reads);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad\nname")]
    [InlineData("bad}name")]
    [InlineData("${SECRET:Bad}")]
    [InlineData("Phantom.Workspaces:Name")]
    [InlineData("oauth:legacy")]
    [InlineData("other:internal")]
    public async Task Create_InvalidOrReservedName_NeverWrites(string name)
    {
        var store = new Store();
        var service = new SavedCredentialAdministration(store);
        using var value = Secure("marker");
        await Assert.ThrowsAsync<SavedCredentialOperationException>(() => service.CreateAsync(name, value, TestContext.Current.CancellationToken));
        Assert.Empty(store.Values);
    }

    [Fact]
    public async Task CreateUpdateAndDelete_PreserveStableIdentityAndRejectCollisionOrDeletedRow()
    {
        var store = new Store();
        var service = new SavedCredentialAdministration(store);
        using var first = Secure("first");
        using var second = Secure("second");
        await service.CreateAsync("BuildKey", first, TestContext.Current.CancellationToken);
        var row = Assert.Single(await service.ListAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<SavedCredentialOperationException>(() => service.CreateAsync("buildkey", second, TestContext.Current.CancellationToken));
        await service.UpdateAsync(row, second, TestContext.Current.CancellationToken);
        Assert.Equal("second", store.Values["BuildKey"]);
        Assert.Equal(row, Assert.Single(await service.ListAsync(TestContext.Current.CancellationToken)));
        await service.DeleteAsync(row, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<SavedCredentialOperationException>(() => service.UpdateAsync(row, first, TestContext.Current.CancellationToken));
        Assert.Empty(store.Values);
    }

    [Fact]
    public async Task ConcurrentCreate_SerializesPreflightAndDoesNotOverwrite()
    {
        var store = new Store();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeWrite = async () => { entered.SetResult(); await release.Task; };
        var service = new SavedCredentialAdministration(store);
        using var value = Secure("first");
        var first = service.CreateAsync("Name", value, TestContext.Current.CancellationToken);
        await entered.Task;
        var second = service.CreateAsync("name", value, TestContext.Current.CancellationToken);
        release.SetResult();
        await first;
        await Assert.ThrowsAsync<SavedCredentialOperationException>(() => second);
        Assert.Single(store.Values);
    }

    [Fact]
    public async Task UnsupportedStore_DisablesValueOperationsInsteadOfUsableEmptyVault()
    {
        var service = new SavedCredentialAdministration(new NullPlatformSecretStore());
        Assert.False(service.IsSupported);
        Assert.Empty(await service.ListAsync(TestContext.Current.CancellationToken));
        using var value = Secure("marker");
        await Assert.ThrowsAsync<SavedCredentialOperationException>(() => service.CreateAsync("Name", value, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OversizedOrEmptyValueAndForeignIdentity_FailBeforeStorage()
    {
        var store = new Store();
        var service = new SavedCredentialAdministration(store);
        using var empty = Secure("");
        using var oversized = Secure(new string('x', 1281));
        using var valid = Secure("valid");
        await Assert.ThrowsAsync<SavedCredentialOperationException>(() => service.CreateAsync("Name", empty, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<SavedCredentialOperationException>(() => service.CreateAsync("Name", oversized, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<SavedCredentialOperationException>(() => service.CreateAsync(new string('n', 257), valid, TestContext.Current.CancellationToken));
        var foreign = new SavedCredentialMetadata(Guid.NewGuid(), "Name", true);
        await Assert.ThrowsAsync<SavedCredentialOperationException>(() => service.ReadForEditorAsync(foreign, TestContext.Current.CancellationToken));
        Assert.Empty(store.Values);
        Assert.Equal(0, store.Reads);
    }

    internal static SecureString Secure(string text)
    {
        var value = new SecureString();
        foreach (var character in text) value.AppendChar(character);
        value.MakeReadOnly();
        return value;
    }

    internal sealed class Store : IPlatformSecretStore
    {
        public bool SupportsNamedCredentialManagement => true;
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Reads { get; private set; }
        public Func<Task>? BeforeWrite { get; set; }
        public Task<IReadOnlyList<string>> EnumerateNamesAsync(string prefix, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(this.Values.Keys.ToArray());
        public Task<SecureString?> ReadAsync(string name, CancellationToken ct)
        {
            this.Reads++;
            return Task.FromResult(this.Values.TryGetValue(name, out var value) ? Secure(value) : null);
        }
        public async Task WriteAsync(string name, SecureString value, CancellationToken ct)
        {
            if (this.BeforeWrite is { } before) await before();
            this.Values[name] = new System.Net.NetworkCredential("", value).Password;
        }
        public Task DeleteAsync(string name, CancellationToken ct)
        {
            this.Values.Remove(name);
            return Task.CompletedTask;
        }
    }
}
