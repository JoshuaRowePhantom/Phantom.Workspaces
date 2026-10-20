using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services.Access;

namespace Phantom.Workspaces.Tests.Access;

[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialNameInspectionTests
{
    private const string TargetPrefix = "Phantom.Workspaces.Tests.MetadataOnly:";

    [Fact]
    public async Task StoredSecretInspection_RealPlatformNamesPath_ReadsOnlyNativeTargetNames()
    {
        using var native = new NativeNames(
            TargetPrefix + "TEST_SECRET",
            TargetPrefix + "TEST_SECRET_SECOND",
            TargetPrefix + "OTHER",
            "Other.Application:TEST_SECRET");
        var store = new WindowsCredentialManagerSecretStore(TargetPrefix, native);
        await using var executor = new StoredSecretAccessExecutor(store);
        var request = StoredSecretAccessExecutorTests.Request();
        request = request with { Sources = request.Sources.Where(source => source.Kind == CredentialSourceKind.SecretStore).ToArray() };
        var inspection = await executor.InspectAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(CredentialSourceUsability.Usable, Assert.Single(inspection.Sources).Usability);
        Assert.Equal(TargetPrefix + "TEST_SECRET*", native.Filter);
        Assert.Equal(1, native.Enumerations);
        Assert.Equal(1, native.Frees);
        Assert.Empty(inspection.CachedGenerations);
    }

    [Fact]
    public async Task NativeNameEnumeration_FiltersNamesAndFreesNativeAllocation()
    {
        using var native = new NativeNames(
            TargetPrefix + "TEST_SECRET",
            TargetPrefix + "TEST_SECRET_SECOND",
            TargetPrefix + "OTHER",
            "Other.Application:TEST_SECRET");
        var store = new WindowsCredentialManagerSecretStore(TargetPrefix, native);
        var names = await store.EnumerateNamesAsync("TEST_SECRET", TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "TEST_SECRET", "TEST_SECRET_SECOND" }, names);
        Assert.Equal(1, native.Frees);
    }

    [Fact]
    public async Task NativeNameEnumeration_CancelledAfterNativeReturn_StillFreesAllocation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var native = new NativeNames(TargetPrefix + "TEST_SECRET") { AfterEnumerate = cancellation.Cancel };
        var store = new WindowsCredentialManagerSecretStore(TargetPrefix, native);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.EnumerateNamesAsync("TEST_SECRET", cancellation.Token));
        Assert.Equal(1, native.Enumerations);
        Assert.Equal(1, native.Frees);
    }

    private sealed class NativeNames : WindowsCredentialManagerSecretStore.IWindowsCredentialNameNativeApi, IDisposable
    {
        private readonly List<IntPtr> allocations = [];
        private readonly IntPtr credentials;
        private readonly uint count;
        public string? Filter { get; private set; }
        public int Enumerations { get; private set; }
        public int Frees { get; private set; }
        public Action? AfterEnumerate { get; init; }

        public NativeNames(params string[] names)
        {
            this.count = checked((uint)names.Length);
            this.credentials = this.Allocate(names.Length * IntPtr.Size);
            for (var index = 0; index < names.Length; index++)
            {
                var credential = this.Allocate(80);
                Marshal.Copy(new byte[80], 0, credential, 80);
                var targetName = Marshal.StringToHGlobalUni(names[index]);
                this.allocations.Add(targetName);
                Marshal.WriteIntPtr(credential, 8, targetName);
                Marshal.WriteInt32(credential, IntPtr.Size == 8 ? 32 : 24, 2048);
                // Deliberately unreadable blob/user pointers: this is native metadata, not a value projection.
                Marshal.WriteIntPtr(credential, IntPtr.Size == 8 ? 40 : 28, new IntPtr(1));
                Marshal.WriteIntPtr(credential, IntPtr.Size == 8 ? 72 : 48, new IntPtr(1));
                Marshal.WriteIntPtr(this.credentials, index * IntPtr.Size, credential);
            }
        }

        public IntPtr Enumerate(string filter, out uint count)
        {
            this.Filter = filter;
            this.Enumerations++;
            count = this.count;
            this.AfterEnumerate?.Invoke();
            return this.credentials;
        }

        public void Free(IntPtr credentials)
        {
            Assert.Equal(this.credentials, credentials);
            this.Frees++;
        }

        public void Dispose()
        {
            foreach (var allocation in this.allocations)
            {
                Marshal.FreeHGlobal(allocation);
            }
        }

        private IntPtr Allocate(int size)
        {
            var pointer = Marshal.AllocHGlobal(size);
            this.allocations.Add(pointer);
            return pointer;
        }
    }
}
