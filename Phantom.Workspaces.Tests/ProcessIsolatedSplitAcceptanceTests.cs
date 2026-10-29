using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;
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
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Services.Logging;

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
        => await RunSplitScenarioAsync(startupFailure: false);

    [Fact]
    public async Task SplitMode_WorkerStartupFails_DisplaysDetailedCauseOnCaller()
        => await RunSplitScenarioAsync(startupFailure: true);

    [Fact]
    public async Task SplitMode_ProductionShortcut_RemoteChildTranscriptAndViewerReleaseCrossAuthenticatedProcesses()
        => await RunSplitScenarioAsync(startupFailure: false, childAttachment: true);

    [Fact]
    public async Task SplitMode_ProductionShortcut_ChildTransportLossShowsRetryAndPreservesOwnerAcrossProcesses()
        => await RunSplitScenarioAsync(startupFailure: false, childAttachment: true, childTransportFailure: true);

    private static async Task RunSplitScenarioAsync(
        bool startupFailure, bool childAttachment = false, bool childTransportFailure = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(childAttachment ? 180 : 90));
        var ct = timeout.Token;
        // Git's indexed entity paths must not inherit the deep test binary output path.
        var directory = ProcessStageDiagnostics.GetFixtureDirectory(AppContext.BaseDirectory);
        Directory.CreateDirectory(directory);
        var hubLogs = Path.Combine(directory, "hub-logs");
        var callerLogs = Path.Combine(directory, "caller-logs");
        var workerLogs = Path.Combine(directory, "worker-logs");
        var roles = new List<RoleProcess>();
        var sensitiveValues = new List<string>
        {
            ProcessAcceptanceCredentials.Token, Environment.UserName, Environment.MachineName,
            SessionId, "fixture-prompt", "worker-response", directory,
            User.ToString(), Caller.ToString(), Worker.ToString(), Manifest.ToString(), Hub.ToString(),
        };
        try
        {
            using var hubLoggerFactory = LoggingBootstrap.CreateLoggerFactory(
                new LogDirectoryProvider(new WorkspacesConfiguration { LogDirectory = hubLogs }, null));
            var data = new MergeProcessingDataAccessLayer(
                new GitDataAccessLayer(Path.Combine(directory, "entities")));
            var sessionEntityId = await SeedAsync(data, ct);
            var status = new ReverseConnectionStatusRegistry();
            await using var reverse = new ReverseHttpServerTransportFactory(status,
                requireAuthenticatedRelays: true, hubProfileEntityId: Hub,
                loggerFactory: hubLoggerFactory);
            var registry = new TransportRegistry();
            registry.Register(reverse);
            await using var server = new HttpServerTransportFactory(registry);
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new ForwardingLoggerProvider(hubLoggerFactory));
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
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
            sensitiveValues.Add(url);
            sensitiveValues.Add(callerUrl);
            await using var caller = StartRole(childAttachment ? "caller-child"
                    : startupFailure ? "caller-failure" : "caller",
                url, User, Caller, callerUrl, Worker,
                Path.Combine(directory, "entities"), callerLogs);
            roles.Add(caller);
            Assert.Equal("READY caller", await caller.NextLineAsync(ct, "READY caller"));
            await using var worker = StartRole(childAttachment ? "worker-child"
                    : startupFailure ? "worker-failure" : "worker",
                url, User, Worker, callerUrl, Worker,
                Path.Combine(directory, "entities"), workerLogs);
            roles.Add(worker);
            Assert.Equal("READY worker", await worker.NextLineAsync(ct, "READY worker"));
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
            Assert.Equal("RENEWED", await worker.NextLineAsync(ct, "RENEWED"));
            route = Assert.Single(await store.GetRoutesAsync(Worker, ct),
                item => item.Descriptor.GetProperty("hub-urls")[0].GetString()?.TrimEnd('/') == url.TrimEnd('/'));
            Assert.True(route.ExpiresAt > before);
            Assert.True(reverse.IsRegistered(Worker.ToString()));

            await caller.SendAsync("DROP", ct);
            Assert.Equal("DROPPED live", await caller.NextLineAsync(ct, "DROPPED live"));
            Assert.Equal("TAB Loading", await caller.NextLineAsync(ct, "TAB Loading"));
            if (!childAttachment)
                Assert.Equal("TAB Failed transport-choice", await caller.NextLineAsync(ct, "TAB Failed transport-choice"));
            Assert.Equal("TAB Ready remote", await caller.NextLineAsync(ct, "TAB Ready remote"));
            if (childAttachment)
            {
                await worker.SendAsync("PREPARE-CHILD", ct);
                var childReady = await worker.NextLineAsync(ct);
                Assert.StartsWith("CHILD ready ", childReady, StringComparison.Ordinal);
                var childSessionId = childReady["CHILD ready ".Length..];
                Assert.NotEqual(SessionId, childSessionId);
                Assert.NotEqual("fixture-child", childSessionId);
                Assert.True(Guid.TryParse(childSessionId, out _));
                await caller.SendAsync(childTransportFailure ? "OPEN-CHILD-FAILURE" : "OPEN-CHILD", ct);
                if (childTransportFailure)
                {
                    Assert.Equal($"CHILD failed {childSessionId} retryable no-slot no-parent-transcript",
                        await caller.NextLineAsync(ct, "caller result"));
                    await worker.SendAsync("VERIFY-CHILD " + childSessionId, ct);
                    Assert.Equal($"CHILD owner {childSessionId} unchanged", await worker.NextLineAsync(ct));
                    await caller.SendAsync("RETRY-CHILD", ct);
                }
                Assert.Equal($"CHILD rendered {childSessionId} viewer-1", await caller.NextLineAsync(ct));
                Assert.Equal($"CHILD reopened {childSessionId} viewer-1", await caller.NextLineAsync(ct));
                Assert.Equal("CHILD parent-alive", await caller.NextLineAsync(ct));
            }
            Assert.Equal(startupFailure
                    ? "RESULT caller-ui startup-type-message-stack-inner"
                    : "RESULT persisted worker-response",
                await caller.NextLineAsync(ct));
            Assert.Equal(0, await caller.WaitForExitAsync(ct));
            Assert.DoesNotContain(ProcessAcceptanceCredentials.Token, caller.CapturedError, StringComparison.Ordinal);
            Assert.True(reverse.IsRegistered(Worker.ToString()));
            if (!startupFailure)
            {
                await worker.SendAsync("VERIFY", ct);
                Assert.Equal("TURN worker", await worker.NextLineAsync(ct));
            }
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

            var callerFile = ReadProcessLog(callerLogs);
            var hubFile = ReadProcessLog(hubLogs);
            var workerFile = ReadProcessLog(workerLogs);
            var choices = Regex.Matches(callerFile,
                @"Session loading; attempt ([0-9a-f]{32}); stage transport-choice; [^\r\n]*outcome started\.");
            Assert.Equal(childAttachment ? 1 : 2, choices.Count);
            foreach (Match choice in choices)
            {
                var attempt = choice.Groups[1].Value;
                var loadingRecords = Regex.Matches(
                    callerFile,
                    $@"Session loading; attempt {attempt}; [^\r\n]*");
                Assert.NotEmpty(loadingRecords);
                Assert.All(loadingRecords.Cast<Match>(), record => Assert.EndsWith(
                    $"session-entity-id {sessionEntityId}; agent-session-id {SessionId}; owner-profile-entity-id {Worker}.",
                    record.Value,
                    StringComparison.Ordinal));
            }
            var retryAttempt = choices[^1].Groups[1].Value;
            if (!childAttachment)
            {
                var failedAttempt = choices[0].Groups[1].Value;
                Assert.NotEqual(failedAttempt, retryAttempt);
                AssertOrdered(callerFile,
                    $"Session loading; attempt {failedAttempt}; stage service-initialization;",
                    $"Session loading; attempt {failedAttempt}; stage transport-choice;",
                    $"Session loading; attempt {failedAttempt}; stage transport-choice; elapsed-ms");
                Assert.Matches(
                    $@"Session loading; attempt {failedAttempt}; stage transport-choice; elapsed-ms [^\r\n]*; reason stage-timeout\.",
                    callerFile);
            }
            AssertOrdered(callerFile,
                $"Session loading; attempt {retryAttempt}; stage service-initialization;",
                $"Session loading; attempt {retryAttempt}; stage transport-choice;",
                $"Remote profile transport; attempt {retryAttempt}; stage transport-choice; route persisted; outcome selected.",
                $"Session loading; attempt {retryAttempt}; stage ready-publication;",
                $"Session loading; attempt {retryAttempt}; stage ready-publication; outcome ready.");
            Assert.Contains($"Reverse relay; attempt {retryAttempt}; stage relay-acceptance; outcome attached.", hubFile);
            Assert.Contains($"Reverse worker; attempt {retryAttempt}; stage worker-channel-open; outcome received.", workerFile);
            Assert.Contains($"Reverse worker; attempt {retryAttempt}; stage worker-listener; outcome accepted.", workerFile);
            if (startupFailure)
            {
                Assert.Contains("Remote Copilot worker startup failed;", workerFile);
                Assert.Contains("System.InvalidOperationException: Synthetic worker CLI start failed.", workerFile);
                Assert.Contains("System.IO.IOException: Synthetic worker inner cause.", workerFile);
                Assert.Contains("FailingFixtureCopilotClient.StartAsync", workerFile);
                var sdkAttempt = Regex.Match(callerFile,
                    @"Remote Copilot lifecycle ([0-9a-f]{32}) caller open-start");
                Assert.True(sdkAttempt.Success, "Caller did not start a split SDK session.");
                Assert.Contains($"Remote Copilot lifecycle {sdkAttempt.Groups[1].Value} worker request-received", workerFile);
                Assert.Contains($"Remote Copilot lifecycle {sdkAttempt.Groups[1].Value} worker cli-start-failed", workerFile);
                Assert.DoesNotContain("stage worker-turn; outcome request-verified.", workerFile);
            }
            else
            {
                Assert.Contains("Worker turn; stage worker-turn; outcome request-verified.", workerFile);
            }
            foreach (var log in new[] { callerFile, hubFile, workerFile })
            {
                Assert.DoesNotContain(ProcessAcceptanceCredentials.Token, log, StringComparison.Ordinal);
                Assert.DoesNotContain(url, log, StringComparison.Ordinal);
                Assert.DoesNotContain(callerUrl, log, StringComparison.Ordinal);
                Assert.DoesNotContain("fixture-prompt", log, StringComparison.Ordinal);
                Assert.DoesNotContain("worker-response", log, StringComparison.Ordinal);
            }
            foreach (var role in roles)
                TestContext.Current.TestOutputHelper?.WriteLine(ProcessStageDiagnostics.Sanitize(
                    role.DiagnosticOutput, sensitiveValues.ToArray()));
        }
        catch (Exception error)
        {
            var artifactDirectory = Path.Combine(Path.GetDirectoryName(directory)!,
                "diagnostics-" + Guid.NewGuid().ToString("N"));
            ProcessStageDiagnostics.RetainRoleLogs(directory, artifactDirectory, sensitiveValues.ToArray());
            foreach (var role in roles)
                File.WriteAllText(Path.Combine(artifactDirectory, role.Role + "-protocol.log"),
                    ProcessStageDiagnostics.Sanitize(role.DiagnosticOutput, sensitiveValues.ToArray()));
            throw new InvalidOperationException(
                $"Split process scenario failed; sanitized diagnostic files only: {artifactDirectory}\n"
                + ProcessStageDiagnostics.Sanitize(
                    string.Join("\n", roles.Select(role => role.DiagnosticOutput))
                    + "\n" + error, sensitiveValues.ToArray()));
        }
    }

    private static string ReadProcessLog(string directory)
    {
        var path = Assert.Single(Directory.GetFiles(directory, "phantom-workspaces-*.log"));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void AssertOrdered(string log, params string[] stages)
    {
        var offset = 0;
        foreach (var stage in stages)
        {
            var found = log.IndexOf(stage, offset, StringComparison.Ordinal);
            Assert.True(found >= 0, $"Missing or out-of-order diagnostic stage: {stage}");
            offset = found + stage.Length;
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
                {"entity-id":"{{User}}","entity-types":["entity","user"],"names":[["users","username",{{JsonSerializer.Serialize(Environment.UserName)}}]]
                }
                """),
            Parse($$"""
                {"entity-id":"{{Caller}}","entity-types":["entity","user-computer-profile"],
                 "names":[["computer-user-profiles","users","username",{{JsonSerializer.Serialize(Environment.UserName)}},"computers","hostname","fixture-caller"]],
                 "user-reference":["users","username",{{JsonSerializer.Serialize(Environment.UserName)}}],
                 "computer-reference":["computers","hostname",{{JsonSerializer.Serialize(Environment.MachineName)}}]
                }
                """),
            Parse($$"""
                {"entity-id":"{{Worker}}","entity-types":["entity","user-computer-profile"],"names":[["profiles","worker"]],"user-reference":["users","username",{{JsonSerializer.Serialize(Environment.UserName)}}]
                }
                """),
            Parse($$"""
                {"entity-id":"{{Hub}}","entity-types":["entity","user-computer-profile"],"names":[["profiles","hub"]],"user-reference":["users","username",{{JsonSerializer.Serialize(Environment.UserName)}}]
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
        string role, string url, EntityId user, EntityId profile, string callerUrl, EntityId worker,
        string repositoryPath, string logDirectory)
        => new(role, url, user.ToString(), profile.ToString(), SessionId, callerUrl, worker.ToString(),
            repositoryPath, logDirectory);

    private sealed class RoleProcess : IAsyncDisposable
    {
        private readonly Process process;
        private readonly List<string> output = [];
        private readonly List<string> errors = [];
        private readonly Task drainError;
        private string waiting = "none";
        private string lastCommand = "none";
        private long waitStarted;
        internal string Role { get; }
        internal string DiagnosticOutput
        {
            get
            {
                lock (this.errors)
                {
                    var lastStage = this.errors.LastOrDefault(line => line.StartsWith("STAGE ", StringComparison.Ordinal));
                    var fields = lastStage?.Split(' ');
                    var activeStage = fields is { Length: >= 4 } && fields[2] == "started"
                        && DateTimeOffset.TryParse(fields[1], out var start)
                        ? $"{fields[3]}; active elapsed-ms {(DateTimeOffset.UtcNow - start).TotalMilliseconds:F0}"
                        : "none";
                    return $"Role {this.Role}; awaiting {this.waiting}; last completed protocol marker "
                        + $"{this.output.LastOrDefault() ?? "none"}\n"
                        + $"Active child stage {activeStage}\n"
                        + $"Active repository startup stage {ProcessStageDiagnostics.DescribeActiveStartup(this.errors, DateTimeOffset.UtcNow)}\n"
                        + $"Last sent command {this.lastCommand}; wait elapsed-ms "
                        + $"{(this.waiting == "none" ? 0 : Stopwatch.GetElapsedTime(this.waitStarted).TotalMilliseconds):F0}\n"
                        + string.Join("\n", this.errors);
                }
            }
        }
        internal string CapturedOutput => string.Join("\n", this.output);
        internal string CapturedError { get; private set; } = "";

        internal RoleProcess(string role, params string[] args)
        {
            this.Role = role;
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
            this.drainError = this.DrainErrorAsync();
        }

        private async Task DrainErrorAsync()
        {
            while (await this.process.StandardError.ReadLineAsync() is { } line)
            {
                lock (this.errors)
                    this.errors.Add(line);
            }
        }

        internal async Task<string> NextLineAsync(CancellationToken ct, string expected = "next stdout protocol line")
        {
            this.waiting = expected;
            this.waitStarted = Stopwatch.GetTimestamp();
            var line = await this.process.StandardOutput.ReadLineAsync(ct);
            if (line is null)
            {
                await this.drainError;
                throw new InvalidOperationException($"Child exited before expected stage: {this.DiagnosticOutput}");
            }
            this.output.Add(line);
            this.waiting = "none";
            return line;
        }

        internal async Task SendAsync(string command, CancellationToken ct)
        {
            this.waiting = "stdin command write";
            this.waitStarted = Stopwatch.GetTimestamp();
            await this.process.StandardInput.WriteLineAsync(command.AsMemory(), ct);
            await this.process.StandardInput.FlushAsync(ct);
            this.lastCommand = command;
            this.waiting = "none";
        }

        internal async Task<int> WaitForExitAsync(CancellationToken ct)
        {
            this.waiting = "process shutdown";
            this.waitStarted = Stopwatch.GetTimestamp();
            await this.process.WaitForExitAsync(ct);
            await this.drainError;
            this.CapturedError = string.Join("\n", this.errors);
            if (this.process.ExitCode != 0)
                throw new InvalidOperationException("Child failed at " + this.CapturedError);
            this.waiting = "none";
            return this.process.ExitCode;
        }

        public async ValueTask DisposeAsync()
        {
            if (!this.process.HasExited)
            {
                this.process.Kill(entireProcessTree: true);
                await this.process.WaitForExitAsync();
            }
            await this.drainError;
            this.process.Dispose();
        }
    }
}
