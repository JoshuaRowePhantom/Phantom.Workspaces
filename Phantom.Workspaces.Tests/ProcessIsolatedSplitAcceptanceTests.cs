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

public sealed partial class ProcessIsolatedSplitAcceptanceTests
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
        SplitScenarioPhase? phase = null;
        CancellationToken EnterPhase(string name, bool cleanup = false)
        {
            phase?.Dispose();
            phase = new SplitScenarioPhase(name, TimeSpan.FromSeconds(cleanup ? 30 : 90),
                cleanup ? default : TestContext.Current.CancellationToken);
            return phase.Token;
        }
        var ct = EnterPhase("preparation");
        // Git's indexed entity paths must not inherit the deep test binary output path.
        var directory = ProcessStageDiagnostics.GetFixtureDirectory(AppContext.BaseDirectory);
        Directory.CreateDirectory(directory);
        var hubLogs = Path.Combine(directory, "hub-logs");
        var callerLogs = Path.Combine(directory, "caller-logs");
        var workerLogs = Path.Combine(directory, "worker-logs");
        var roles = new List<RoleProcess>();
        var resources = new SplitScenarioResources();
        Exception? primaryFailure = null;
        using var hubLoggerFactory = LoggingBootstrap.CreateLoggerFactory(
            new LogDirectoryProvider(new WorkspacesConfiguration { LogDirectory = hubLogs }, null));
        var sensitiveValues = new List<string>
        {
            ProcessAcceptanceCredentials.Token, Environment.UserName, Environment.MachineName,
            SessionId, "fixture-prompt", "worker-response", directory,
            User.ToString(), Caller.ToString(), Worker.ToString(), Manifest.ToString(), Hub.ToString(),
        };
        try
        {
            var data = new MergeProcessingDataAccessLayer(
                new GitDataAccessLayer(Path.Combine(directory, "entities")));
            var sessionEntityId = await SeedAsync(data, ct);
            WorkspaceEntitySession? preparedSession = null;
            await phase!.RunAsync(async token =>
            {
                await using var prepared = await EntityRepository.CreateAsync(
                    new EntityRepositoryInitializationOptions
                    {
                        RepositorySource = new LocalGitRepositorySource(Path.Combine(directory, "entities")),
                        UserComputerProfileOverride = "fixture-caller",
                    }, token);
                Assert.Equal(Caller, prepared.WorkspaceEntitySession.UserComputerProfileEntityId);
                preparedSession = prepared.WorkspaceEntitySession;
            });
            ct = EnterPhase("hub-startup");
            var status = new ReverseConnectionStatusRegistry();
            var reverse = resources.Add(new ReverseHttpServerTransportFactory(status,
                requireAuthenticatedRelays: true, hubProfileEntityId: Hub,
                loggerFactory: hubLoggerFactory));
            var registry = new TransportRegistry();
            registry.Register(reverse);
            var server = resources.Add(new HttpServerTransportFactory(registry));
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new ForwardingLoggerProvider(hubLoggerFactory));
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
            builder.WebHost.UseUrls($"http://127.0.0.1:{FreePort()}");
            builder.Services.AddSingleton<IDataAccessLayer>(data);
            var app = resources.Add(builder.Build());
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
            ct = EnterPhase("role-startup");
            var caller = resources.Add(StartRole(childAttachment ? "caller-child"
                    : startupFailure ? "caller-failure" : "caller",
                url, User, Caller, callerUrl, Worker,
                Path.Combine(directory, "entities"), callerLogs, preparedSession!.ComputerEntityId));
            roles.Add(caller);
            Assert.Equal("READY caller", await caller.NextLineAsync(ct, "READY caller"));
            ct = EnterPhase("worker-startup");
            var worker = resources.Add(StartRole(childAttachment ? "worker-child"
                    : startupFailure ? "worker-failure" : "worker",
                url, User, Worker, callerUrl, Worker,
                Path.Combine(directory, "entities"), workerLogs, Worker));
            roles.Add(worker);
            Assert.Equal("READY worker", await worker.NextLineAsync(ct, "READY worker"));
            Assert.True(reverse.IsRegistered(Worker.ToString()));
            ct = EnterPhase("route-verification");
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

            ct = EnterPhase("gui-startup");
            await caller.SendAsync("DROP", ct);
            Assert.Equal("DROPPED live", await caller.NextLineAsync(ct, "DROPPED live"));
            Assert.Equal("READY gui", await caller.NextLineAsync(ct, "READY gui"));
            ct = EnterPhase("operation");
            await caller.SendAsync("RUN", ct);
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
            ct = EnterPhase("caller-exit", cleanup: true);
            Assert.Equal(0, await caller.WaitForExitAsync(ct));
            Assert.DoesNotContain(ProcessAcceptanceCredentials.Token, caller.CapturedError, StringComparison.Ordinal);
            Assert.True(reverse.IsRegistered(Worker.ToString()));
            ct = EnterPhase("worker-verification");
            if (!startupFailure)
            {
                await worker.SendAsync("VERIFY", ct);
                Assert.Equal("TURN worker", await worker.NextLineAsync(ct));
            }
            Assert.DoesNotContain(ProcessAcceptanceCredentials.Token, worker.CapturedOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(ProcessAcceptanceCredentials.Token, caller.CapturedOutput, StringComparison.Ordinal);
            ct = EnterPhase("worker-stop", cleanup: true);
            await worker.SendAsync("STOP", ct);
            Assert.Equal("STOP accepted", await worker.NextLineAsync(ct, "STOP accepted"));
            ct = EnterPhase("worker-exit", cleanup: true);
            Assert.Equal(0, await worker.WaitForExitAsync(ct));
            Assert.False(reverse.IsRegistered(Worker.ToString()));
            Assert.DoesNotContain(ProcessAcceptanceCredentials.Token, worker.CapturedError, StringComparison.Ordinal);
            ct = EnterPhase("hub-stop", cleanup: true);
            await app.StopAsync(ct);

            // Git persists the published session and profile independently of either application process.
            ct = EnterPhase("persistence-verification");
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
            primaryFailure = new InvalidOperationException(
                $"Split process scenario failed in {phase?.Name}; sanitized diagnostic files only: {artifactDirectory}\n"
                + ProcessStageDiagnostics.Sanitize(
                    string.Join("\n", roles.Select(role => role.DiagnosticOutput))
                    + "\n" + error, sensitiveValues.ToArray()));
        }
        finally
        {
            phase?.Dispose();
        }
        try
        {
            await resources.CompleteAsync(primaryFailure);
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
                $"Split scenario operation/cleanup failed; sanitized artifacts: {artifactDirectory}\n"
                + ProcessStageDiagnostics.Sanitize(error.ToString(), sensitiveValues.ToArray()));
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
        string repositoryPath, string logDirectory, EntityId computer)
        => new(role, url, user.ToString(), profile.ToString(), SessionId, callerUrl, worker.ToString(),
            repositoryPath, logDirectory, computer.ToString());

    private sealed class RoleProcess : IAsyncDisposable
    {
        private readonly Process process;
        private readonly List<string> output = [];
        private readonly List<string> errors = [];
        private readonly Task drainError;
        private readonly CancellationTokenSource drainCancellation = new();
        private readonly TimeProvider clock;
        internal TaskCompletionSource CleanupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool WasTerminated { get; private set; }
        private string waiting = "none";
        private string lastCommand = "none";
        private long waitStarted;
        private bool exitObserved;
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
            : this(TimeProvider.System, role, args)
        {
        }

        internal RoleProcess(TimeProvider clock, string role, params string[] args)
        {
            this.clock = clock;
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
            try
            {
                while (await this.process.StandardError.ReadLineAsync(this.drainCancellation.Token) is { } line)
                {
                    lock (this.errors)
                        this.errors.Add(line);
                }
            }
            catch (OperationCanceledException) when (this.drainCancellation.IsCancellationRequested) { }
        }

        internal async Task<string> NextLineAsync(CancellationToken ct, string expected = "next stdout protocol line")
        {
            this.waiting = expected;
            this.waitStarted = Stopwatch.GetTimestamp();
            var line = await this.process.StandardOutput.ReadLineAsync(ct);
            if (line is null)
            {
                await this.drainError.WaitAsync(ct);
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
            await this.drainError.WaitAsync(ct);
            this.CapturedError = string.Join("\n", this.errors);
            this.exitObserved = true;
            if (this.process.ExitCode != 0)
                throw new InvalidOperationException("Child failed at " + this.CapturedError);
            this.waiting = "none";
            return this.process.ExitCode;
        }

        internal Task WaitForTerminationAsync(CancellationToken cancellationToken)
            => this.process.WaitForExitAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            Exception? gracefulFailure = null;
            try
            {
                if (!this.process.HasExited)
                {
                    using var graceful = new CancellationTokenSource(TimeSpan.FromSeconds(10), this.clock);
                    try
                    {
                        await this.SendAsync("STOP", graceful.Token);
                        this.process.StandardInput.Close();
                        this.CleanupEntered.TrySetResult();
                        await this.process.WaitForExitAsync(graceful.Token);
                    }
                    catch (Exception error) when (error is OperationCanceledException or IOException)
                    {
                        gracefulFailure = error;
                        this.WasTerminated = !this.process.HasExited;
                        try
                        {
                            if (!this.process.HasExited)
                                this.process.Kill(entireProcessTree: true);
                        }
                        catch (InvalidOperationException) when (this.process.HasExited) { }
                        using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await this.process.WaitForExitAsync(reap.Token);
                    }
                }
                using var drain = new SplitScenarioPhase(
                    "stderr-drain", TimeSpan.FromSeconds(5), timeProvider: this.clock);
                await drain.RunAsync(token => this.drainError.WaitAsync(token));
                if (this.WasTerminated)
                {
                    if (gracefulFailure is OperationCanceledException)
                        throw new TimeoutException("Role required owned-process termination during cleanup.", gracefulFailure);
                    throw new IOException("Role graceful STOP failed; owned process was terminated.", gracefulFailure);
                }
                if (!this.exitObserved && this.process.ExitCode != 0)
                    throw new InvalidOperationException($"Role exited with code {this.process.ExitCode} during cleanup.");
            }
            finally
            {
                this.drainCancellation.Cancel();
                this.process.StandardInput.Dispose();
                this.process.StandardOutput.Dispose();
                this.process.StandardError.Dispose();
                this.process.Dispose();
                this.drainCancellation.Dispose();
            }
        }
    }
}
