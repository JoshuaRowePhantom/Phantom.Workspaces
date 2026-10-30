namespace Phantom.Workspaces.Tests;

public sealed partial class ProcessIsolatedSplitAcceptanceTests
{
    [Theory]
    [InlineData("attach")]
    [InlineData("retry")]
    [InlineData("gui-disposal")]
    public async Task ActiveAuthenticatedCallerCancellationKeepsDispatcherUntilOwnedCleanupCompletes(string stage)
        => await RunSplitScenarioAsync(startupFailure: false, cancellationStage: stage);
}
