using System.Security;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Services.Logging;

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

    [Fact]
    public void ObservableLogger_ProtectedAwareDiagnostics_PreservesDetailWithoutMaterialization()
    {
        // #1670: the in-memory/subscriber sink follows the same protection semantics as the durable
        // sink — ordinary detail is retained, credential fragments are scrubbed, and no protected
        // getter/marshal/use callback runs merely to format or serialize a log entry.
        using var factory = new ObservableLoggerFactory(verboseMetadataEnabled: false);
        var logger = factory.CreateLogger("SafeLifecycle");
        var tracker = new MaterializationTrackingValue();
        var cause = new InvalidOperationException("inner-ordinary-cause status=404");
        var error = new Exception(
            "attach failed https://user:topsecretpw@host/x?access_token=topsecrettoken ordinary-tail",
            cause);
        error.Data["protected"] = tracker;
        error.Data["secure"] = MakeSecure("canary-secure-material");

        logger.LogError(error, "loading failed stage=remote-flight-chat-acquisition ordinary-context");

        var entry = Assert.Single(
            factory.Entries, text => text.Contains("ordinary-context", StringComparison.Ordinal));
        Assert.Contains("inner-ordinary-cause status=404", entry, StringComparison.Ordinal);
        Assert.Contains("ordinary-tail", entry, StringComparison.Ordinal);
        Assert.Contains("[redacted]", entry, StringComparison.Ordinal);
        Assert.Contains("[protected:SecureString]", entry, StringComparison.Ordinal);
        Assert.Contains("[protected:marker-only]", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("topsecretpw", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("topsecrettoken", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("canary-secure-material", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("released-plaintext", entry, StringComparison.Ordinal);
        Assert.False(tracker.Materialized, "A protected value must not be materialized to render a log entry.");
    }

    private static SecureString MakeSecure(string value)
    {
        var secure = new SecureString();
        foreach (var character in value)
        {
            secure.AppendChar(character);
        }

        secure.MakeReadOnly();
        return secure;
    }

    private sealed class MaterializationTrackingValue : ISensitiveDiagnosticValue
    {
        public bool Materialized { get; private set; }

        public string Plaintext
        {
            get
            {
                this.Materialized = true;
                return "released-plaintext";
            }
        }

        public string ToDiagnosticMarker() => "[protected:marker-only]";
    }
}
