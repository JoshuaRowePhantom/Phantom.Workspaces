using System.Diagnostics;
using System.Text.Json;

namespace Phantom.Workspaces.Transport.Shell;

public sealed class ShellTransportListener : ITransportListener
{
    public Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default)
        => Task.FromResult<IAsyncDisposable?>(null);

    public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!IsType(request, "shell") || !request.TryGetProperty("command", out var commandElement))
        {
            return Task.FromResult<IAsyncDisposable?>(null);
        }

        var command = commandElement.GetString();
        if (string.IsNullOrWhiteSpace(command))
        {
            return Task.FromResult<IAsyncDisposable?>(null);
        }

        var startInfo = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (request.TryGetProperty("args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var arg in argsElement.EnumerateArray())
            {
                var value = arg.GetString();
                if (value is not null)
                {
                    startInfo.ArgumentList.Add(value);
                }
            }
        }

        if (request.TryGetProperty("working-directory", out var cwdElement))
        {
            var cwd = cwdElement.GetString();
            if (!string.IsNullOrWhiteSpace(cwd))
            {
                startInfo.WorkingDirectory = cwd;
            }
        }

        if (request.TryGetProperty("environment", out var envElement) && envElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in envElement.EnumerateObject())
            {
                startInfo.Environment[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException($"Failed to start shell command '{command}'.");
        }

        return Task.FromResult<IAsyncDisposable?>(new ShellSession(process, stream, ct));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal static bool IsType(JsonElement request, string type)
        => request.ValueKind == JsonValueKind.Object
           && request.TryGetProperty("type", out var typeElement)
           && string.Equals(typeElement.GetString(), type, StringComparison.OrdinalIgnoreCase);
}
