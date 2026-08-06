using System;
using System.Runtime.Versioning;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Llm.Core.Tests.Secrets;

/// <summary>
/// Exercises <see cref="WindowsCredentialManagerSecretStore"/> against the real Windows Credential
/// Manager, using an isolated target prefix and a deterministic, constant credential name so that an
/// interrupted run never orphans a credential at an unpredictable location.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialManagerSecretStoreTests
{
    // Isolated prefix so these tests never touch the user's real Phantom.Workspaces credentials.
    private const string TestTargetPrefix = "Phantom.Workspaces.Tests:";

    // A single compiled-in constant (never Guid.NewGuid()) so any orphan from an interrupted prior
    // run is at a deterministic, discoverable name and is cleaned up by the next run.
    private const string ConstantName = "WindowsCredentialManagerSecretStore-3f5b0c8e-4a2d-4f6b-9c1a-2d7e8f9a0b1c";

    private const string SecondConstantName = ConstantName + "-second";

    // Process-wide (and cross-process) mutual exclusion so parallel xUnit workers or two concurrent
    // dotnet test invocations on the same box cannot race on the shared constant credential name.
    private const string SemaphoreName =
        @"Global\Phantom.Workspaces.Tests.WindowsCredentialManagerSecretStore";

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

    private static string Reveal(SecureString value)
        => Phantom.Workspaces.Llm.Secrets.SecureStringMarshal.Use(value, plaintext => plaintext);

    private static async Task WithExclusiveStoreAsync(Func<WindowsCredentialManagerSecretStore, Task> body)
    {
        using var semaphore = new Semaphore(1, 1, SemaphoreName);
        semaphore.WaitOne();
        try
        {
            var store = new WindowsCredentialManagerSecretStore(TestTargetPrefix);

            // Best-effort cleanup of any orphan from a prior interrupted run.
            await TryDeleteAsync(store, ConstantName);
            await TryDeleteAsync(store, SecondConstantName);

            try
            {
                await body(store);
            }
            finally
            {
                await TryDeleteAsync(store, ConstantName);
                await TryDeleteAsync(store, SecondConstantName);
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static async Task TryDeleteAsync(WindowsCredentialManagerSecretStore store, string name)
    {
        try
        {
            await store.DeleteAsync(name, CancellationToken.None);
        }
        catch
        {
            // Deleting a non-existent credential is fine; ignore.
        }
    }

    [WindowsFact]
    public async Task Write_ThenRead_ReturnsSameValue()
    {
        await WithExclusiveStoreAsync(async store =>
        {
            await store.WriteAsync(ConstantName, MakeSecureString("s3cr3t-value"), CancellationToken.None);

            var read = await store.ReadAsync(ConstantName, CancellationToken.None);

            Assert.NotNull(read);
            Assert.Equal("s3cr3t-value", Reveal(read!));
        });
    }

    [WindowsFact]
    public async Task Read_Missing_ReturnsNull()
    {
        await WithExclusiveStoreAsync(async store =>
        {
            var read = await store.ReadAsync(ConstantName, CancellationToken.None);

            Assert.Null(read);
        });
    }

    [WindowsFact]
    public async Task Delete_ExistingCredential_RemovesIt()
    {
        await WithExclusiveStoreAsync(async store =>
        {
            await store.WriteAsync(ConstantName, MakeSecureString("to-be-deleted"), CancellationToken.None);

            await store.DeleteAsync(ConstantName, CancellationToken.None);

            var read = await store.ReadAsync(ConstantName, CancellationToken.None);
            Assert.Null(read);
        });
    }

    [WindowsFact]
    public async Task EnumerateNamesAsync_WithPrefix_ReturnsMatchingNames()
    {
        await WithExclusiveStoreAsync(async store =>
        {
            await store.WriteAsync(ConstantName, MakeSecureString("v1"), CancellationToken.None);
            await store.WriteAsync(SecondConstantName, MakeSecureString("v2"), CancellationToken.None);

            var names = await store.EnumerateNamesAsync(ConstantName, CancellationToken.None);

            Assert.Contains(ConstantName, names);
            Assert.Contains(SecondConstantName, names);
            Assert.All(names, name => Assert.StartsWith(ConstantName, name));
        });
    }
}
