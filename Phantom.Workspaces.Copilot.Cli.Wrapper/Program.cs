using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Llm.Processes;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Phantom.Workspaces.Copilot.Cli.Wrapper;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        return await CopilotCliWrapper.RunAsync(
            args,
            Console.OpenStandardInput(),
            Console.OpenStandardOutput(),
            Console.OpenStandardError(),
            new ProcessExecutor(),
            CopilotLaunchPolicyStore.GetDefaultLaunchRoot(),
            ParentProcess.GetParentProcessId(),
            Environment.ProcessPath ?? string.Empty,
            cancellation.Token).ConfigureAwait(false);
    }
}

internal static class CopilotCliWrapper
{
    internal const int InvalidArgumentsExitCode = 64;
    internal const int InvalidEnvelopeExitCode = 65;
    internal const int UnsafePathExitCode = 66;
    internal const int InternalFailureExitCode = 70;
    internal const int ExecutorFailureExitCode = 71;

    internal static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        Stream standardInput,
        Stream standardOutput,
        Stream standardError,
        IProcessExecutor executor,
        string launchRoot,
        int parentProcessId,
        string wrapperPath,
        CancellationToken cancellationToken = default)
    {
        if (args.Count < 4
            || !string.Equals(args[0], "--policy", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(args[1])
            || !string.Equals(args[2], "--copilot", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(args[3]))
        {
            await WriteDiagnosticAsync(
                standardError,
                "Invalid wrapper arguments.",
                cancellationToken).ConfigureAwait(false);
            return InvalidArgumentsExitCode;
        }

        CopilotLaunchPolicyEnvelope envelope;
        try
        {
            envelope = new CopilotLaunchPolicyStore(launchRoot, TimeProvider.System)
                .Consume(args[1], parentProcessId);
        }
        catch (UnauthorizedAccessException)
        {
            await WriteDiagnosticAsync(
                standardError,
                "Unsafe Copilot policy path.",
                cancellationToken).ConfigureAwait(false);
            return UnsafePathExitCode;
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or IOException
                or JsonException)
        {
            await WriteDiagnosticAsync(
                standardError,
                "Invalid or expired Copilot policy.",
                cancellationToken).ConfigureAwait(false);
            return InvalidEnvelopeExitCode;
        }

        string copilotPath;
        try
        {
            copilotPath = ValidateExecutablePath(args[3]);
            var canonicalWrapperPath = System.IO.Path.GetFullPath(wrapperPath);
            if (string.Equals(copilotPath, canonicalWrapperPath, PathComparison))
                throw new UnauthorizedAccessException();
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
                or IOException
                or ArgumentException
                or NotSupportedException)
        {
            await WriteDiagnosticAsync(
                standardError,
                "Unsafe Copilot executable path.",
                cancellationToken).ConfigureAwait(false);
            return UnsafePathExitCode;
        }

        var policy = AddRuntimeBootstrapGrant(
            envelope.Policy,
            System.IO.Path.GetDirectoryName(copilotPath)!);
        var request = new ProcessExecutionRequest(copilotPath, args.Skip(4).ToArray())
        {
            WorkingDirectory = Environment.CurrentDirectory,
            Environment = SnapshotEnvironment(),
            MxcPolicy = policy,
        };

        IProcessHandle process;
        try
        {
            process = executor.Start(request);
        }
        catch
        {
            await WriteDiagnosticAsync(
                standardError,
                "MXC containment launch failed.",
                cancellationToken).ConfigureAwait(false);
            return ExecutorFailureExitCode;
        }

        await using (process.ConfigureAwait(false))
        using (var inputCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            try
            {
                foreach (var warning in process.LaunchInfo.Warnings)
                {
                    await WriteDiagnosticAsync(standardError, warning, cancellationToken)
                        .ConfigureAwait(false);
                }

                var inputPump = PumpInputAsync(
                    standardInput,
                    process.StandardInput,
                    inputCancellation.Token);
                var outputPump = process.StandardOutput.CopyToAsync(
                    standardOutput,
                    cancellationToken);
                var errorPump = process.StandardError.CopyToAsync(
                    standardError,
                    cancellationToken);
                var waitForExit = process.WaitAsync(cancellationToken);
                var firstCompletion = await Task.WhenAny(inputPump, waitForExit).ConfigureAwait(false);
                if (firstCompletion == inputPump && !waitForExit.IsCompleted)
                    process.Kill();
                var result = await waitForExit.ConfigureAwait(false);
                inputCancellation.Cancel();
                await process.StandardInput.DisposeAsync().ConfigureAwait(false);
                await IgnoreInputCancellationAsync(inputPump).ConfigureAwait(false);
                await Task.WhenAll(outputPump, errorPump).ConfigureAwait(false);
                await standardOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
                await standardError.FlushAsync(cancellationToken).ConfigureAwait(false);
                return result.ExitCode;
            }
            catch (OperationCanceledException)
            {
                process.Kill();
                return InternalFailureExitCode;
            }
            catch
            {
                process.Kill();
                await WriteDiagnosticAsync(
                    standardError,
                    "Copilot wrapper stream relay failed.",
                    CancellationToken.None).ConfigureAwait(false);
                return InternalFailureExitCode;
            }
        }
    }

    private static async Task PumpInputAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        await destination.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task IgnoreInputCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async Task WriteDiagnosticAsync(
        Stream standardError,
        string message,
        CancellationToken cancellationToken)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(message + Environment.NewLine);
        await standardError.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await standardError.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ValidateExecutablePath(string path)
    {
        var canonical = System.IO.Path.GetFullPath(path);
        if (!System.IO.Path.IsPathFullyQualified(canonical)
            || !File.Exists(canonical)
            || Directory.Exists(canonical)
            || (File.GetAttributes(canonical) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(System.IO.Path.GetDirectoryName(canonical)!)
                & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException();
        }
        return canonical;
    }

    private static MxcProcessPolicy AddRuntimeBootstrapGrant(
        MxcProcessPolicy policy,
        string runtimeDirectory)
    {
        var canonicalRuntime = System.IO.Path.TrimEndingDirectorySeparator(
            System.IO.Path.GetFullPath(runtimeDirectory));
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var readonlyPaths = policy.ReadonlyPaths.ToList();
        if (!readonlyPaths.Contains(canonicalRuntime, comparer)
            && !policy.ReadwritePaths.Contains(canonicalRuntime, comparer))
        {
            readonlyPaths.Add(canonicalRuntime);
        }

        return new MxcProcessPolicy(
            policy.SchemaVersion,
            readonlyPaths,
            policy.ReadwritePaths,
            policy.NetworkCapabilities,
            policy.EnvironmentOverrides,
            policy.Containment);
    }

    private static IReadOnlyDictionary<string, string> SnapshotEnvironment()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
                result[name] = value;
        }
        return result;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}

internal static class ParentProcess
{
    internal static int GetParentProcessId()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Copilot containment wrapper requires Windows.");

        var status = NtQueryInformationProcess(
            Process.GetCurrentProcess().Handle,
            0,
            out var information,
            Marshal.SizeOf<ProcessBasicInformation>(),
            out _);
        if (status != 0)
            throw new InvalidOperationException("Unable to identify the wrapper parent process.");
        return checked((int)information.InheritedFromUniqueProcessId);
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        out ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        internal IntPtr Reserved1;
        internal IntPtr PebBaseAddress;
        internal IntPtr Reserved2_0;
        internal IntPtr Reserved2_1;
        internal IntPtr UniqueProcessId;
        internal IntPtr InheritedFromUniqueProcessId;
    }
}
