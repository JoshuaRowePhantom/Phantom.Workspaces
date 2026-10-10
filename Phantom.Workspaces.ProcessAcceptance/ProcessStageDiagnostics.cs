using System.Text.RegularExpressions;

namespace Phantom.Workspaces.ProcessAcceptance;

public sealed class ProcessStageDiagnostics(
    Action<string> write, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private string? stage;
    private long started;

    public void Report(string nextStage)
    {
        if (!Regex.IsMatch(nextStage, @"\A[a-zA-Z0-9.-]+\z"))
            throw new ArgumentException("Stage must be a diagnostic identifier.", nameof(nextStage));
        Complete();
        this.stage = nextStage;
        this.started = this.clock.GetTimestamp();
        write($"STAGE {this.clock.GetUtcNow():O} started {nextStage}");
    }

    public void Complete()
    {
        if (this.stage is null)
            return;
        write($"STAGE {this.clock.GetUtcNow():O} completed {this.stage} elapsed-ms "
            + $"{this.clock.GetElapsedTime(this.started).TotalMilliseconds:F0}");
        this.stage = null;
    }

    public void Fail(Exception error)
        => write($"STAGE {this.clock.GetUtcNow():O} failed {this.stage ?? "none"} elapsed-ms "
            + $"{this.clock.GetElapsedTime(this.started).TotalMilliseconds:F0} exception-type {error.GetType().Name}");

    public static string Sanitize(string text, params string[] sensitiveValues)
    {
        foreach (var value in sensitiveValues.Where(value => !string.IsNullOrEmpty(value))
                     .OrderByDescending(value => value.Length))
        {
            text = text.Replace(value, "[redacted]", StringComparison.OrdinalIgnoreCase);
            if (Guid.TryParse(value, out var identifier))
                text = text.Replace(identifier.ToString("N"), "[entity-id]", StringComparison.OrdinalIgnoreCase);
        }
        text = Regex.Replace(text, @"\b(?:https?|wss?)://[^\s""<>]+", "[endpoint]",
            RegexOptions.IgnoreCase);
        return Regex.Replace(text,
            @"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b",
            "[entity-id]", RegexOptions.IgnoreCase);
    }

    public static string GetFixtureDirectory(string baseDirectory)
    {
        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Phantom.Workspaces.slnx")))
                return Path.Combine(directory.FullName, "TestResults", "split-" + Guid.NewGuid().ToString("N"));
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Fixture repository root was not found.");
    }

    public static string DescribeActiveStartup(IEnumerable<string> lines, DateTimeOffset now)
    {
        var active = new List<(string Stage, DateTimeOffset Started)>();
        foreach (var line in lines)
        {
            var fields = line.Split(' ');
            if (fields.Length < 4 || fields[0] != "STARTUP"
                || !DateTimeOffset.TryParse(fields[1], out var timestamp)
                || !Regex.IsMatch(fields[3], @"\A[a-zA-Z0-9.-]+\z"))
                continue;
            if (fields[2] == "started")
                active.Add((fields[3], timestamp));
            else if (fields[2] == "completed")
            {
                var index = active.FindLastIndex(stage => stage.Stage == fields[3]);
                if (index >= 0)
                    active.RemoveAt(index);
            }
        }
        return active.Count == 0 ? "none"
            : $"{active[^1].Stage}; active elapsed-ms {(now - active[^1].Started).TotalMilliseconds:F0}";
    }
}

internal sealed class DiagnosticAsyncLifetime(
    IAsyncDisposable inner, Action<string> reportStage, string stage) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        reportStage(stage);
        await inner.DisposeAsync();
        reportStage(stage + "-complete");
    }
}
