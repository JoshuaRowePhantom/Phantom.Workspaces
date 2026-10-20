using System.ComponentModel;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Security;
using Meziantou.Framework.Win32;

namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// A thin adapter over the Windows Credential Manager (via
/// <see cref="Meziantou.Framework.Win32.CredentialManager"/>) that stores each secret under a
/// target name of the form <c>"{prefix}{name}"</c>. Values are marshalled to plaintext only for the
/// microseconds required to hand them to the credential API, via
/// <see cref="SecureStringMarshal.Use{T}(SecureString, System.Func{string, T})"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialManagerSecretStore : IPlatformSecretStore
{
    /// <summary>The default target-name prefix for all Phantom.Workspaces credentials.</summary>
    public const string DefaultTargetPrefix = "Phantom.Workspaces:";

    private const int ErrorNotFound = 1168;

    private readonly string targetPrefix;
    private readonly IWindowsCredentialNameNativeApi nativeNames;

    /// <summary>
    /// Creates a store that reads and writes credentials under <paramref name="targetPrefix"/>.
    /// Defaults to <see cref="DefaultTargetPrefix"/>. Tests supply an isolated prefix so they never
    /// touch a user's real credentials.
    /// </summary>
    public WindowsCredentialManagerSecretStore(string? targetPrefix = null)
        : this(targetPrefix ?? DefaultTargetPrefix, new WindowsCredentialNameNativeApi())
    {
    }

    internal WindowsCredentialManagerSecretStore(
        string targetPrefix,
        IWindowsCredentialNameNativeApi nativeNames)
    {
        this.targetPrefix = targetPrefix;
        this.nativeNames = nativeNames;
    }

    /// <inheritdoc />
    public Task<SecureString?> ReadAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ct.ThrowIfCancellationRequested();

        // Meziantou's CredentialManager is annotated [SupportedOSPlatform("windows5.1.2600")]; this
        // type is windows-only and every supported Windows release exceeds that baseline.
#pragma warning disable CA1416
        var credential = CredentialManager.ReadCredential(this.targetPrefix + name);
#pragma warning restore CA1416
        if (credential?.Password is not { } password)
        {
            return Task.FromResult<SecureString?>(null);
        }

        return Task.FromResult<SecureString?>(ToSecureString(password));
    }

    /// <inheritdoc />
    public Task WriteAsync(string name, SecureString value, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        ct.ThrowIfCancellationRequested();

        SecureStringMarshal.Use(value, plaintext =>
        {
#pragma warning disable CA1416
            CredentialManager.WriteCredential(
                applicationName: this.targetPrefix + name,
                userName: Environment.UserName,
                secret: plaintext,
                persistence: CredentialPersistence.LocalMachine);
#pragma warning restore CA1416
            return true;
        });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ct.ThrowIfCancellationRequested();

#pragma warning disable CA1416
        try
        {
            CredentialManager.DeleteCredential(this.targetPrefix + name);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorNotFound)
        {
            return Task.CompletedTask;
        }
#pragma warning restore CA1416
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> EnumerateNamesAsync(string prefix, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ct.ThrowIfCancellationRequested();

        var targetNamePrefix = this.targetPrefix + prefix;
        var credentials = this.nativeNames.Enumerate(targetNamePrefix + "*", out var count);
        if (credentials == IntPtr.Zero)
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
        try
        {
            var names = new List<string>();
            for (var index = 0; index < count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var credential = Marshal.ReadIntPtr(credentials, checked(index * IntPtr.Size));
                // CREDENTIALW begins with two DWORDs followed by TargetName on both Windows architectures.
                // Do not marshal the structure: its other pointers include CredentialBlob and user identity.
                var targetName = Marshal.PtrToStringUni(Marshal.ReadIntPtr(credential, 8));
                if (targetName?.StartsWith(targetNamePrefix, StringComparison.Ordinal) == true)
                {
                    names.Add(targetName[this.targetPrefix.Length..]);
                }
            }
            return Task.FromResult<IReadOnlyList<string>>(names);
        }
        finally
        {
            this.nativeNames.Free(credentials);
        }
    }

    internal interface IWindowsCredentialNameNativeApi
    {
        IntPtr Enumerate(
            string filter,
            out uint count);

        void Free(IntPtr credentials);
    }

    [SupportedOSPlatform("windows")]
    internal sealed class WindowsCredentialNameNativeApi : IWindowsCredentialNameNativeApi
    {
        public IntPtr Enumerate(
            string filter,
            out uint count)
        {
            if (CredEnumerate(filter, 0, out count, out var credentials))
            {
                return credentials;
            }
            var error = Marshal.GetLastWin32Error();
            if (error == 1168)
            {
                return IntPtr.Zero;
            }
            throw new Win32Exception(error);
        }

        public void Free(IntPtr credentials) => CredFree(credentials);

        [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredEnumerate(
            string filter,
            uint flags,
            out uint count,
            out IntPtr credentials);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr credentials);
    }

    private static SecureString ToSecureString(string value)
    {
        var secure = new SecureString();
        foreach (var character in value)
        {
            secure.AppendChar(character);
        }

        secure.MakeReadOnly();
        return secure;
    }
}
