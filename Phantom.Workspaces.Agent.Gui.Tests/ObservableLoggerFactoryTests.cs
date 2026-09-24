using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class ObservableLoggerFactoryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScopedMetadataDebug_RespectsOffSwitchWithoutSuppressingLifecycle(bool enabled)
    {
        using var factory = new ObservableLoggerFactory(enabled);
        var trace = factory.CreateLogger("Phantom.Workspaces.Transport.Logging.LoggingMessageChannel");
        var unrelated = factory.CreateLogger("Phantom.Workspaces.Llm.CopilotSdkChatClient");

        trace.LogDebug("safe-metadata");
        trace.LogInformation("lifecycle-info");
        unrelated.LogDebug("unapproved-debug-secret");
        unrelated.LogTrace("unapproved-trace-secret");
        factory.CreateLogger("ModelContextProtocol.Client.McpClient")
            .LogInformation("private-sdk-server");
        factory.CreateLogger("GitHub.Copilot.CopilotClient")
            .LogInformation("private-sdk-prompt");
        factory.CreateLogger("Microsoft.Extensions.AI.LoggingChatClient")
            .LogInformation("private-sdk-credential");

        Assert.Equal(enabled, factory.Entries.Any(entry =>
            entry.Contains("safe-metadata", StringComparison.Ordinal)));
        Assert.Contains(factory.Entries, entry =>
            entry.Contains("lifecycle-info", StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Entries, entry =>
            entry.Contains("unapproved", StringComparison.Ordinal)
            || entry.Contains("private-sdk", StringComparison.Ordinal));
    }
}
