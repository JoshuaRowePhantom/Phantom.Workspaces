using System;
using System.Security;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Secrets;
using SecureStringMarshal = Phantom.Workspaces.Llm.Secrets.SecureStringMarshal;

namespace Phantom.Workspaces.Llm.Core.Tests.Secrets;

public class SecureStringMarshalTests
{
    private static SecureString Make(string value)
    {
        var ss = new SecureString();
        foreach (var c in value)
        {
            ss.AppendChar(c);
        }
        ss.MakeReadOnly();
        return ss;
    }

    private sealed class SpyMarshaller : ISecureStringMarshaller
    {
        private readonly string _plaintext;
        public int ZeroFreeCount { get; private set; }
        public IntPtr LastBstr { get; private set; }

        public SpyMarshaller(string plaintext) => _plaintext = plaintext;

        public IntPtr ToBstr(SecureString value)
        {
            LastBstr = new IntPtr(0xABCD);
            return LastBstr;
        }

        public string ToPlaintext(IntPtr bstr) => _plaintext;

        public void ZeroFree(IntPtr bstr) => ZeroFreeCount++;
    }

    [Fact]
    public void Use_InvokesBodyWithPlaintextEqualToOriginal()
    {
        using var ss = Make("hunter2");

        var observed = SecureStringMarshal.Use(ss, plain => plain);

        Assert.Equal("hunter2", observed);
    }

    [Fact]
    public void Use_ZeroesBstrAfterBody_EvenOnException()
    {
        using var ss = Make("secret");
        var spy = new SpyMarshaller("secret");

        Assert.Throws<InvalidOperationException>(() =>
            SecureStringMarshal.Use<int>(ss, _ => throw new InvalidOperationException("boom"), spy));

        Assert.Equal(1, spy.ZeroFreeCount);
    }

    [Fact]
    public void Use_ZeroesBstrAfterSuccessfulBody()
    {
        using var ss = Make("secret");
        var spy = new SpyMarshaller("secret");

        var observed = SecureStringMarshal.Use(ss, plain => plain, spy);

        Assert.Equal("secret", observed);
        Assert.Equal(1, spy.ZeroFreeCount);
    }

    [Fact]
    public async Task UseAsync_ZeroesBstrAfterAwaitedTaskCompletes()
    {
        using var ss = Make("secret");
        var spy = new SpyMarshaller("secret");

        var observed = await SecureStringMarshal.UseAsync(ss, async plain =>
        {
            await Task.Yield();
            return plain;
        }, spy);

        Assert.Equal("secret", observed);
        Assert.Equal(1, spy.ZeroFreeCount);
    }

    [Fact]
    public async Task UseAsync_ZeroesBstrAfterAwaitedTaskFaults()
    {
        using var ss = Make("secret");
        var spy = new SpyMarshaller("secret");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await SecureStringMarshal.UseAsync<int>(ss, async _ =>
            {
                await Task.Yield();
                throw new InvalidOperationException("boom");
            }, spy));

        Assert.Equal(1, spy.ZeroFreeCount);
    }

    [Fact]
    public async Task UseAsync_InvokesBodyWithPlaintextEqualToOriginal()
    {
        using var ss = Make("hunter2");

        var observed = await SecureStringMarshal.UseAsync(ss, plain => Task.FromResult(plain));

        Assert.Equal("hunter2", observed);
    }
}
