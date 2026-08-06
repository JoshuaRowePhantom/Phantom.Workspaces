using System;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Llm.Core.Tests.Secrets;

public class SecretRetrieverTests
{
    [Fact]
    public void Secret_ReturnType_IsSecureString()
    {
        var property = typeof(SecretRetriever).GetProperty(nameof(SecretRetriever.Secret));

        Assert.NotNull(property);
        Assert.Equal(typeof(Func<CancellationToken, Task<SecureString>>), property!.PropertyType);
    }

    [Fact]
    public async Task Secret_IsLazy_OnlyInvokedWhenAwaited()
    {
        var invoked = false;
        var retriever = new SecretRetriever
        {
            SecretName = "SECRET",
            Secret = _ =>
            {
                invoked = true;
                return Task.FromResult(new SecureString());
            },
        };

        Assert.False(invoked);

        using var result = await retriever.Secret(CancellationToken.None);

        Assert.True(invoked);
        Assert.NotNull(result);
    }
}
