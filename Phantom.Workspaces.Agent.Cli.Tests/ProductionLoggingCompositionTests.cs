using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Services.Logging;
using Phantom.Workspaces.Transport.ReverseHttp;
using Phantom.Workspaces.Web.Server;
using Microsoft.AspNetCore.Builder;

namespace Phantom.Workspaces.Agent.Cli.Tests;

public sealed class ProductionLoggingCompositionTests
{
    [Fact]
    public async Task ProductionLoggingComposition_WebServerAndCliEnabled_RouteEventsToFile()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "production-logging-tests", Guid.NewGuid().ToString("N"));
        var webDirectory = Path.Combine(root, "web");
        var cliDirectory = Path.Combine(root, "cli");
        try
        {
            var builder = WebApplication.CreateBuilder();
            StandaloneWebServerLoggingComposition.Configure(
                builder, webDirectory, new ReverseConnectionStatusRegistry());
            builder.Services.AddSingleton<IAgentPersistenceStore>(AgentPersistenceStoreFactory.CreateInMemory());
            await using (var web = builder.Build())
            {
                var cache = web.Services.GetRequiredService<AgentChatSessionCache>();
                var request = new AgentChatTurnRequest
                {
                    AgentDefinitionJson = EchoAgentJson,
                    AgentSessionId = "private-web-session-id",
                    Messages = [new ChatMessage(ChatRole.User, "private-web-prompt")],
                };
                var response = new List<ChatResponseUpdate>();
                await foreach (var update in cache.RunTurnAsync(request))
                    response.Add(update);
                Assert.Contains("private-web-prompt", string.Concat(response.Select(update => update.Text)));

                var contents = ReadLog(webDirectory);
                Assert.Equal(1, Count(contents, "Web agent session created; outcome success."));
                Assert.DoesNotContain("private-web-session-id", contents, StringComparison.Ordinal);
                Assert.DoesNotContain("private-web-prompt", contents, StringComparison.Ordinal);
                await cache.DisposeAsync();
            }

            using var process = HostFileLoggerFactory.Create(cliDirectory);
            var definition = AgentDefinitionLoader.LoadAgentFromJson(EchoAgentJson);
            using (var cli = new AgentCliApp(
                new AgentDefinitionParseResult(definition, null, null, true, true, []),
                process, cliDirectory))
            {
                var contents = ReadLog(cliDirectory);
                Assert.Equal(1, Count(contents, "CLI agent session initialized; outcome success."));
                Assert.DoesNotContain("private-web-session-id", contents, StringComparison.Ordinal);
                Assert.DoesNotContain("private-web-prompt", contents, StringComparison.Ordinal);
                Assert.DoesNotContain("Web agent session created", contents, StringComparison.Ordinal);
                await GetChat(cli).DisposeAsync();
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private const string EchoAgentJson =
        """{"kind":"prompt","name":"cli-logging","model":{"id":"echo","provider":"echo","apiType":"Echo"},"tools":[]}""";

    private static string ReadLog(string directory)
    {
        var path = Assert.Single(Directory.GetFiles(directory, "phantom-workspaces-*.log"));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static int Count(string content, string text)
        => content.Split(text, StringSplitOptions.None).Length - 1;

    private static AgentChat GetChat(AgentCliApp app)
        => (AgentChat)typeof(AgentCliApp)
            .GetField("agentChat", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;

    [Fact]
    public async Task ProductionLoggingComposition_CliWithoutLogFlags_StillWritesAgentLifecycleToFile()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "cli-logging-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var process = HostFileLoggerFactory.Create(directory);
            var definition = AgentDefinitionLoader.LoadAgentFromJson(EchoAgentJson);
            using var app = new AgentCliApp(
                new AgentDefinitionParseResult(definition, null, null, false, false, []),
                process, directory);
            var chat = GetChat(app);
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
