using System.Runtime.InteropServices;
using System.Security;

namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// Marshals a <see cref="SecureString"/> to a temporary plaintext buffer only for the duration of a
/// caller-supplied delegate, and zero-frees the native BSTR immediately afterwards (in a
/// <c>finally</c> block, so a thrown exception still zeroes the buffer).
/// </summary>
/// <remarks>
/// <para>
/// This is the single approved plaintext conversion path. The plaintext <see cref="string"/> passed
/// to the delegate must be handed straight to an SDK constructor and must not be stashed in a
/// Phantom.Workspaces-owned field.
/// </para>
/// <para>
/// Documented limitation: .NET managed <see cref="string"/> cannot be reliably zeroed by user code
/// (interning, string dedup, GC copy). What this helper guarantees is that (a) the native BSTR
/// buffer is zero-freed, and (b) the plaintext lifetime is bounded to a single caller-supplied
/// delegate.
/// </para>
/// </remarks>
public static class SecureStringMarshal
{
    public static T Use<T>(SecureString value, Func<string, T> body)
        => Use(value, body, DefaultSecureStringMarshaller.Instance);

    public static Task<T> UseAsync<T>(SecureString value, Func<string, Task<T>> body)
        => UseAsync(value, body, DefaultSecureStringMarshaller.Instance);

    internal static T Use<T>(SecureString value, Func<string, T> body, ISecureStringMarshaller marshaller)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(body);

        var bstr = marshaller.ToBstr(value);
        try
        {
            var plain = marshaller.ToPlaintext(bstr);
            return body(plain);
        }
        finally
        {
            marshaller.ZeroFree(bstr);
        }
    }

    internal static async Task<T> UseAsync<T>(
        SecureString value, Func<string, Task<T>> body, ISecureStringMarshaller marshaller)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(body);

        var bstr = marshaller.ToBstr(value);
        try
        {
            var plain = marshaller.ToPlaintext(bstr);
            return await body(plain).ConfigureAwait(false);
        }
        finally
        {
            marshaller.ZeroFree(bstr);
        }
    }
}

/// <summary>
/// Abstraction over the native BSTR marshalling operations, so the zero-free invariant can be
/// verified deterministically in tests without native spying.
/// </summary>
internal interface ISecureStringMarshaller
{
    IntPtr ToBstr(SecureString value);

    string ToPlaintext(IntPtr bstr);

    void ZeroFree(IntPtr bstr);
}

internal sealed class DefaultSecureStringMarshaller : ISecureStringMarshaller
{
    public static readonly DefaultSecureStringMarshaller Instance = new();

    public IntPtr ToBstr(SecureString value) => Marshal.SecureStringToBSTR(value);

    public string ToPlaintext(IntPtr bstr) => Marshal.PtrToStringBSTR(bstr) ?? string.Empty;

    public void ZeroFree(IntPtr bstr) => Marshal.ZeroFreeBSTR(bstr);
}
