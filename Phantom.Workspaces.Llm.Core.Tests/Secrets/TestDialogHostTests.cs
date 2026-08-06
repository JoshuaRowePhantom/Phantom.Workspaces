using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Llm.Core.Tests.Secrets;

public sealed class TestDialogHostTests
{
    private static SecretRequest SampleRequest()
        => new(
            "MySecret",
            "definition.instructions",
            new List<SecretUseMemory>(),
            DefaultSecretSource: null,
            new List<SecretSource>());

    [Fact]
    public async Task ShowAsync_ReturnsScriptedResult()
    {
        var row = new SecretUseDialogRow(
            SampleRequest(),
            new SecretUseMemory(SecretUseScope.AllUses, "All Uses", "hash"),
            new GitHubLoginSecretSource());
        var scripted = new SecretUseDialogResult(Accepted: true, new List<SecretUseDialogRow> { row });
        var host = new TestDialogHost(scripted);

        var result = await host.ShowAsync(
            new SecretUseDialogInput(new List<SecretRequest> { SampleRequest() }),
            CancellationToken.None);

        Assert.Same(scripted, result);
    }

    [Fact]
    public async Task ShowAsync_RecordsLastInputAndCount()
    {
        var scripted = new SecretUseDialogResult(Accepted: false, Array.Empty<SecretUseDialogRow>());
        var host = new TestDialogHost(scripted);
        var input = new SecretUseDialogInput(new List<SecretRequest> { SampleRequest() });

        await host.ShowAsync(input, CancellationToken.None);
        await host.ShowAsync(input, CancellationToken.None);

        Assert.Same(input, host.LastInput);
        Assert.Equal(2, host.ShowCount);
    }
}
