using System.Security;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Services.Logging;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Tests;

public sealed class SessionTeeLoggerFactoryTests
{
    [Fact]
    public async Task SessionTeeLoggerFactory_ChildAgentLogIsolation_WritesFileOnce()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "session-tee-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var process = HostFileLoggerFactory.Create(directory);
            using var parentMemory = new ObservableLoggerFactory();
            using var tee = new SessionTeeLoggerFactory(process, parentMemory);
            var definition = AgentDefinitionLoader.LoadAgentFromJson(
                """{"kind":"prompt","name":"safe-echo","model":{"id":"echo","provider":"echo","apiType":"Echo"},"tools":[]}""");
            await using var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
            {
                AgentDefinition = definition,
                AgentServices = new AgentServices { LoggerFactory = tee },
            });
            await chat.GetOrCreateAsync("child-scope", definition, "tool-call",
                TestContext.Current.CancellationToken);
            var child = Assert.IsType<AgentChat>(Assert.Single(chat.SubAgents));
            var childFactory = Assert.IsAssignableFrom<ISessionScopedLoggerFactory>(child.SessionLoggerFactory);
            var childMemory = Assert.IsType<ObservableLoggerFactory>(childFactory.SessionMemoryFactory);

            childFactory.CreateLogger("SafeLifecycle").LogInformation("child-approved-safe");

            Assert.Single(childMemory.Entries, entry => entry.Contains("child-approved-safe", StringComparison.Ordinal));
            Assert.DoesNotContain(parentMemory.Entries, entry => entry.Contains("child-approved-safe", StringComparison.Ordinal));
            Assert.Equal(1, Count(ProcessLogTestFile.ReadAll(directory), "child-approved-safe"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SessionTeeLoggerFactory_TwoConcurrentSessions_IsolatesUiEntriesAndWritesEachFileLineOnce()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "session-tee-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var process = HostFileLoggerFactory.Create(directory);
            using var firstMemory = new ObservableLoggerFactory();
            using var secondMemory = new ObservableLoggerFactory();
            using var first = new SessionTeeLoggerFactory(process, firstMemory);
            using var second = new SessionTeeLoggerFactory(process, secondMemory);

            first.CreateLogger("SafeLifecycle").LogInformation("first-safe-stage");
            second.CreateLogger("SafeLifecycle").LogInformation("second-safe-stage");
            first.Dispose();
            second.CreateLogger("SafeLifecycle").LogInformation("second-after-close");

            Assert.Single(firstMemory.Entries, entry => entry.Contains("first-safe-stage", StringComparison.Ordinal));
            Assert.DoesNotContain(firstMemory.Entries, entry => entry.Contains("second-safe-stage", StringComparison.Ordinal));
            Assert.Equal(2, secondMemory.Entries.Count);
            Assert.DoesNotContain(secondMemory.Entries, entry => entry.Contains("first-safe-stage", StringComparison.Ordinal));

            var contents = ProcessLogTestFile.ReadAll(directory);
            Assert.Equal(1, Count(contents, "first-safe-stage"));
            Assert.Equal(1, Count(contents, "second-safe-stage"));
            Assert.Equal(1, Count(contents, "second-after-close"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SessionTeeLoggerFactory_RepresentativeEvent_WritesMemoryAndActualRollingFileOnce()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "session-tee-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var process = HostFileLoggerFactory.Create(directory);
            using var memory = new ObservableLoggerFactory();
            using var session = new SessionTeeLoggerFactory(process, memory);

            session.CreateLogger("SafeLifecycle").LogInformation("role=caller stage=open-start");

            Assert.Single(memory.Entries);
            var contents = ProcessLogTestFile.ReadAll(directory);
            Assert.Equal(1, Count(contents, "role=caller stage=open-start"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SessionTeeLoggerFactory_ProtectedDiagnostics_ReachesBothSinksWithoutLeaks()
    {
        // #1670: identical protected-aware detail must reach both the process (durable) and the
        // session-memory (UI) sinks exactly once; neither sink may bypass redaction or leak a
        // protected/credential fragment.
        var directory = Path.Combine(AppContext.BaseDirectory, "session-tee-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var process = HostFileLoggerFactory.Create(directory);
            using var memory = new ObservableLoggerFactory();
            using var session = new SessionTeeLoggerFactory(process, memory);
            var cause = new InvalidOperationException("inner-ordinary-cause status=502");
            var error = new Exception(
                "attach failed https://user:topsecretpw@host/x?access_token=topsecrettoken ordinary-tail",
                cause);
            error.Data["secure"] = MakeSecure("canary-secure-material");

            session.CreateLogger("SafeLifecycle").LogError(error, "role=caller stage=host-open ordinary-context");

            var memoryEntry = Assert.Single(memory.Entries);
            var fileContents = ProcessLogTestFile.ReadAll(directory);
            foreach (var sink in new[] { memoryEntry, fileContents })
            {
                Assert.Contains("ordinary-context", sink, StringComparison.Ordinal);
                Assert.Contains("inner-ordinary-cause status=502", sink, StringComparison.Ordinal);
                Assert.Contains("ordinary-tail", sink, StringComparison.Ordinal);
                Assert.Contains("[protected:SecureString]", sink, StringComparison.Ordinal);
                Assert.DoesNotContain("topsecretpw", sink, StringComparison.Ordinal);
                Assert.DoesNotContain("topsecrettoken", sink, StringComparison.Ordinal);
                Assert.DoesNotContain("canary-secure-material", sink, StringComparison.Ordinal);
            }

            Assert.Equal(1, Count(fileContents, "ordinary-context"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
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

    private static int Count(string text, string value)
        => text.Split(value, StringSplitOptions.None).Length - 1;
}
