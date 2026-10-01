using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Data.Web.Server;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.ProcessAcceptance;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Http;
using Phantom.Workspaces.Transport.ReverseHttp;
using Phantom.Workspaces.Web.Server;

namespace Phantom.Workspaces.Tests;

public sealed class ProcessIsolatedSplitAcceptanceTests
{
    private static readonly EntityId User = new("10101010-1010-4010-8010-000000001611");
    private static readonly EntityId Caller = new("10101010-1010-4010-8010-000000001612");
    private static readonly EntityId Worker = new("10101010-1010-4010-8010-000000001613");
    private static readonly EntityId Manifest = new("10101010-1010-4010-8010-000000001614");
    private static readonly EntityId Hub = new("10101010-1010-4010-8010-000000001616");
    private const string SessionId = "process-isolated-1611";

    [Fact]
    public async Task SplitMode_ExistingPersistedSessionOpenedAcrossProcesses_CompletesRealWorkerTurnWithoutLocalFallback()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var ct = timeout.Token;
        var directory = Path.Combine(Path.GetTempPath(), "split-acceptance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var data = new MergeProcessingDataAccessLayer(
                new GitDataAccessLayer(Path.Combine(directory, "entities")));
            var sessionEntityId = await SeedAsync(data, ct);
            var status = new ReverseConnectionStatusRegistry();
            await using var reverse = new ReverseHttpServerTransportFactory(status,
                requireAuthenticatedRelays: true, hubProfileEntityId: Hub);
            var registry = new TransportRegistry();
            registry.Register(reverse);
            await using var server = new HttpServerTransportFactory(registry);
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls($"http://127.0.0.1:{FreePort()}");
            builder.Services.AddSingleton<IDataAccessLayer>(data);
            await using var app = builder.Build();
            app.UseWebSockets();
            app.Use(async (context, next) =>
            {
                if (!string.Equals(context.Request.Headers["X-Tunnel-Authorization"],
                    "tunnel " + ProcessAcceptanceCredentials.Token, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                await next(context);
            });
            app.MapWebDataAccessEndpoints();
            server.Map(app);
            app.MapTransportReverseEndpoints(reverse, status);
            await app.StartAsync(ct);
            var url = app.Urls.Single();
            using var unauthorized = new HttpClient();
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await unauthorized.PostAsync(url + "/data/query", new StringContent("{}"), ct)).StatusCode);
            using var unauthenticatedSocket = new ClientWebSocket();
            var websocketError = await Assert.ThrowsAsync<WebSocketException>(() =>
                unauthenticatedSocket.ConnectAsync(
                    new Uri(url.Replace("http:", "ws:", StringComparison.Ordinal) + "/transport/connect"), ct));
            Assert.Contains("401", websocketError.Message, StringComparison.Ordinal);

            var callerUrl = $"http://127.0.0.1:{FreePort()}";
            await using var caller = StartRole("caller", url, User, Caller, callerUrl, Worker);
            Assert.Equal("READY caller", await caller.NextLineAsync(ct));
            await using var worker = StartRole("worker", url, User, Worker, callerUrl, Worker);
            Assert.Equal("READY worker", await worker.NextLineAsync(ct));
            Assert.True(reverse.IsRegistered(Worker.ToString()));
            var store = new DataAccessReachabilityRouteStore(data);
            var routes = await store.GetRoutesAsync(Worker, ct);
            Assert.Equal(2, routes.Count);
            var route = Assert.Single(routes, item => item.Descriptor.GetProperty("hub-urls")[0]
                .GetString()?.TrimEnd('/') == url.TrimEnd('/'));
            Assert.Equal(url.TrimEnd('/'),
                route.Descriptor.GetProperty("hub-urls")[0].GetString()?.TrimEnd('/'));
            Assert.DoesNotContain(ProcessAcceptanceCredentials.Token, route.Descriptor.GetRawText(), StringComparison.Ordinal);
            var before = route.ExpiresAt;

            // Advance the worker's lease timer; the production renewal loop must update Git.
            await worker.SendAsync("RENEW", ct);
            Assert.Equal("RENEWED", await worker.NextLineAsync(ct));
            route = Assert.Single(await store.GetRoutesAsync(Worker, ct),
                item => item.Descriptor.GetProperty("hub-urls")[0].GetString()?.TrimEnd('/') == url.TrimEnd('/'));
            Assert.True(route.ExpiresAt > before);
            Assert.True(reverse.IsRegistered(Worker.ToString()));

            await caller.SendAsync("DROP", ct);
            Assert.Equal("DROPPED live", await caller.NextLineAsync(ct));
            Assert.Equal("RESULT persisted worker-response", await caller.NextLineAsync(ct));
            Assert.Equal(0, await caller.WaitForExitAsync(ct));
            Assert.DoesNotContain(ProcessAcceptanceCredentials.Token, caller.CapturedError, StringComparison.Ordinal);
            Assert.True(reverse.IsRegistered(Worker.ToString()));
            await worker.SendAsync("VERIFY", ct);
            Assert.Equal("TURN worker", await worker.NextLineAsync(ct));
            Assert.DoesNotContain(ProcessAcceptanceCredentials.Token, worker.CapturedOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(ProcessAcceptanceCredentials.Token, caller.CapturedOutput, StringComparison.Ordinal);
            await worker.SendAsync("STOP", ct);
            Assert.Equal(0, await worker.WaitForExitAsync(ct));
            Assert.DoesNotContain(ProcessAcceptanceCredentials.Token, worker.CapturedError, StringComparison.Ordinal);
            await app.StopAsync(ct);

            // Git persists the published session and profile independently of either application process.
            var reopened = new GitDataAccessLayer(Path.Combine(directory, "entities"));
            var persisted = await reopened.GetAsync(new GetRequest
            {
                Entities = [new GetEntityRequest { EntityId = sessionEntityId }],
                Timestamps = [null],
            }, ct);
            Assert.Contains(persisted.Batches.SelectMany(batch => batch.Entities),
                entity => entity.EntityId == sessionEntityId);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<EntityId> SeedAsync(IDataAccessLayer data, CancellationToken ct)
    {
        static JsonElement Parse(string text) => JsonDocument.Parse(text).RootElement.Clone();
        var now = DateTimeOffset.UtcNow;
        var session = AgentSessionEntityFactory.CreateEntityData(new CreateAgentSessionEntityDataRequest
        {
            AgentDefinitionEntityId = Manifest,
            AgentDisplayName = "Static worker",
            AgentSessionId = SessionId,
            AgentSessionNames = [new EntityName("tests", "sessions", SessionId)],
            CurrentTime = now,
            ComputerName = "worker",
            HostProfileEntityId = Worker,
            ParameterValues = new Dictionary<string, string>(),
        });
        var documents = new[]
        {
            Parse($$"""
                {"entity-id":"{{User}}","entity-types":["entity","user"],"names":[["users","username","split-test"]]
                }
                """),
            Parse($$"""
                {"entity-id":"{{Caller}}","entity-types":["entity","user-computer-profile"],"names":[["profiles","caller"]],"user-reference":["users","username","split-test"]
                }
                """),
            Parse($$"""
                {"entity-id":"{{Worker}}","entity-types":["entity","user-computer-profile"],"names":[["profiles","worker"]],"user-reference":["users","username","split-test"]
                }
                """),
            Parse($$"""
                {"entity-id":"{{Hub}}","entity-types":["entity","user-computer-profile"],"names":[["profiles","hub"]],"user-reference":["users","username","split-test"]
                }
                """),
            Parse($$"""
                {"entity-id":"{{Manifest}}","entity-types":["entity","agent-manifest"],"names":[["tests","manifests","static"]],
                 "display-name":{"default":"Static worker"},"manifest":{"name":"static-worker","displayName":"Static worker",
                 "template":{"kind":"prompt","name":"static-worker","model":{"id":"echo","provider":"echo","apiType":"Echo"},
                 "instructions":"Respond deterministically.","tools":[]
                 }
                 }
                }
                """),
            session,
        };
        var result = await data.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Seed process acceptance." } },
            Changes = documents.Select(document => new EntityChange
            {
                EntityId = new EntityId(document.GetProperty("entity-id").GetString()!),
                EntityChangeMode = EntityChangeMode.Replace,
                Data = document,
            }).ToArray(),
        }, ct);
        Assert.All(result.EntityResults, entity => Assert.NotEqual(UpdateState.Failed, entity.UpdateState));
        return new EntityId(session.GetProperty("entity-id").GetString()!);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static RoleProcess StartRole(
        string role, string url, EntityId user, EntityId profile, string callerUrl, EntityId worker)
        => new(role, url, user.ToString(), profile.ToString(), SessionId, callerUrl, worker.ToString());

    private sealed class RoleProcess : IAsyncDisposable
    {
        private readonly Process process;
        private readonly List<string> output = [];
        internal string CapturedOutput => string.Join("\n", this.output);
        internal string CapturedError { get; private set; } = "";

        internal RoleProcess(string role, params string[] args)
        {
            var info = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add(typeof(ProcessAcceptanceCredentials).Assembly.Location);
            info.ArgumentList.Add(role);
            foreach (var arg in args)
                info.ArgumentList.Add(arg);
            this.process = Process.Start(info) ?? throw new InvalidOperationException("Child process did not start.");
        }

        internal async Task<string> NextLineAsync(CancellationToken ct)
        {
            var line = await this.process.StandardOutput.ReadLineAsync(ct);
            if (line is null)
                throw new InvalidOperationException($"Child exited before expected stage: {await this.process.StandardError.ReadToEndAsync(ct)}");
            this.output.Add(line);
            return line;
        }

        internal async Task SendAsync(string command, CancellationToken ct)
        {
            await this.process.StandardInput.WriteLineAsync(command.AsMemory(), ct);
            await this.process.StandardInput.FlushAsync(ct);
        }

        internal async Task<int> WaitForExitAsync(CancellationToken ct)
        {
            await this.process.WaitForExitAsync(ct);
            this.CapturedError = await this.process.StandardError.ReadToEndAsync(ct);
            if (this.process.ExitCode != 0)
                throw new InvalidOperationException("Child failed at " + this.CapturedError);
            return this.process.ExitCode;
        }

        public async ValueTask DisposeAsync()
        {
            if (!this.process.HasExited)
            {
                this.process.Kill(entireProcessTree: true);
                await this.process.WaitForExitAsync();
            }
            this.process.Dispose();
        }
    }
}
