using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tests;

public sealed partial class ProcessIsolatedSplitAcceptanceTests
{
    private sealed class SplitScenarioProtocol(
        TimeProvider clock,
        CancellationToken cancellation,
        TimeSpan? budget = null)
    {
        internal async Task<WorkspaceEntitySession> PrepareAsync(RoleProcess preparation)
        {
            WorkspaceEntitySession? session = null;
            await RunAsync("preparation", async token =>
            {
                var line = await preparation.NextLineAsync(token, "PREPARED");
                Assert.StartsWith("PREPARED ", line, StringComparison.Ordinal);
                session = System.Text.Json.JsonSerializer.Deserialize<WorkspaceEntitySession>(
                    line["PREPARED ".Length..])!;
                Assert.Equal(0, await preparation.WaitForExitAsync(token));
            });
            return session!;
        }

        internal Task OperationAsync(RoleProcess caller, string result)
            => RunAsync("operation", async token =>
            {
                await caller.SendAsync("RUN", token);
                await ExpectResultAsync(caller, result, token);
            });

        internal Task RunOperationAsync(Func<CancellationToken, Task> action)
            => RunAsync("operation", action);

        internal Task ExpectResultAsync(RoleProcess caller, string result, CancellationToken token)
            => ExpectAsync(caller, result, token);

        internal Task CallerExitAsync(RoleProcess caller)
            => RunAsync("caller-exit", async token => Assert.Equal(0, await caller.WaitForExitAsync(token)), true);

        internal Task StopWorkerAsync(RoleProcess worker)
            => RunAsync("worker-stop", async token =>
            {
                await worker.SendAsync("STOP", token);
                await ExpectAsync(worker, "STOP accepted", token);
            }, true);

        internal Task WorkerExitAsync(RoleProcess worker)
            => RunAsync("worker-exit", async token => Assert.Equal(0, await worker.WaitForExitAsync(token)), true);

        private async Task RunAsync(string name, Func<CancellationToken, Task> action, bool cleanup = false)
        {
            using var phase = new ProcessAcceptance.SplitScenarioPhase(
                name, budget ?? TimeSpan.FromSeconds(cleanup ? 30 : 90),
                cleanup ? default : cancellation, clock);
            await phase.RunAsync(action);
        }

        private static async Task ExpectAsync(RoleProcess role, string expected, CancellationToken token)
        {
            string line;
            do { line = await role.NextLineAsync(token, expected); }
            while (line.StartsWith("INFO ", StringComparison.Ordinal));
            Assert.Equal(expected, line);
        }
    }

    private sealed class RoleProcessHooks
    {
        internal Func<string, CancellationToken, Task>? BeforeSend { get; init; }
        internal Func<string, CancellationToken, Task>? AfterLine { get; init; }
        internal Func<CancellationToken, ValueTask<string?>>? ReadError { get; init; }
        internal Func<CancellationToken, Task>? BeforeOutputRead { get; init; }
    }
}
