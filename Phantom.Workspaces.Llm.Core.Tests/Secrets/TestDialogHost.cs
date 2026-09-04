using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Llm.Core.Tests.Secrets;

/// <summary>
/// A test-support <see cref="ISecretUseDialogHost"/> that returns a scripted
/// <see cref="SecretUseDialogResult"/> and records the input it was shown, for assertions in
/// <c>SecretProviderTests</c> (Commit 6) and the end-to-end tests (Commit 12).
/// </summary>
public sealed class TestDialogHost : ISecretUseDialogHost
{
    private readonly SecretUseDialogResult scriptedResult;

    public TestDialogHost(SecretUseDialogResult scriptedResult)
    {
        this.scriptedResult = scriptedResult;
    }

    /// <summary>The most recent input passed to <see cref="ShowAsync"/>, or <see langword="null"/>.</summary>
    public SecretUseDialogInput? LastInput { get; private set; }

    /// <summary>The number of times <see cref="ShowAsync"/> has been called.</summary>
    public int ShowCount { get; private set; }

    public Task<SecretUseDialogResult> ShowAsync(SecretUseDialogInput input, CancellationToken ct)
    {
        this.LastInput = input;
        this.ShowCount++;
        return Task.FromResult(this.scriptedResult);
    }
}
