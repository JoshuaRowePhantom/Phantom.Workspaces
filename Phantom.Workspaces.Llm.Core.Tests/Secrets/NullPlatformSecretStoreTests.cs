using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Llm.Core.Tests.Secrets;

public sealed class NullPlatformSecretStoreTests
{
    private static SecureString MakeSecureString(string value)
    {
        var secure = new SecureString();
        foreach (var character in value)
        {
            secure.AppendChar(character);
        }

        secure.MakeReadOnly();
        return secure;
    }

    [Fact]
    public async Task ReadAsync_Always_ReturnsNull()
    {
        var store = new NullPlatformSecretStore();

        var read = await store.ReadAsync("any-name", CancellationToken.None);

        Assert.Null(read);
    }

    [Fact]
    public async Task WriteAsync_Throws_PlatformNotSupportedException()
    {
        var store = new NullPlatformSecretStore();

        await Assert.ThrowsAsync<PlatformNotSupportedException>(
            () => store.WriteAsync("any-name", MakeSecureString("value"), CancellationToken.None));
    }

    [Fact]
    public async Task DeleteAsync_Throws_PlatformNotSupportedException()
    {
        var store = new NullPlatformSecretStore();

        await Assert.ThrowsAsync<PlatformNotSupportedException>(
            () => store.DeleteAsync("any-name", CancellationToken.None));
    }

    [Fact]
    public async Task EnumerateNamesAsync_Always_ReturnsEmpty()
    {
        var store = new NullPlatformSecretStore();

        var names = await store.EnumerateNamesAsync(string.Empty, CancellationToken.None);

        Assert.Empty(names);
    }
}
