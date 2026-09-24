using System.Reflection;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Services.Logging;

namespace Phantom.Workspaces.Agent.Cli.Tests;

public sealed class ProductionLoggingCompositionTests
{
    [Fact]
    public async Task ProductionLoggingComposition_CliWithoutLogFlags_StillWritesAgentLifecycleToFile()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "cli-logging-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var process = HostFileLoggerFactory.Create(directory);
            var definition = AgentDefinitionLoader.LoadAgentFromJson(
                """{"kind":"prompt","name":"cli-logging","model":{"id":"echo","provider":"echo","apiType":"Echo"},"tools":[]}""");
            using var app = new AgentCliApp(
                new AgentDefinitionParseResult(definition, null, null, false, false, []),
                process);
            var chat = (AgentChat)typeof(AgentCliApp)
                .GetField("agentChat", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;
            try
            {
                var request = typeof(AgentChat)
                    .GetField("request", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chat)!;
                var services = (AgentServices)request.GetType().GetProperty("AgentServices")!.GetValue(request)!;
                Assert.Same(process, services.LoggerFactory);
                var sessionFactory = Assert.IsAssignableFrom<ILoggerFactory>(services.LoggerFactory);
                sessionFactory.CreateLogger("SafeLifecycle")
                    .LogInformation("cli-lifecycle-safe");

                var path = Assert.Single(Directory.GetFiles(directory, "phantom-workspaces-*.log"));
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                Assert.Equal(1, reader.ReadToEnd()
                    .Split("cli-lifecycle-safe", StringSplitOptions.None).Length - 1);
            }
            finally
            {
                await chat.DisposeAsync();
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
