using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Llm.Processes;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
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
        Stream? standardError = null;
        try
        {
            standardError = Console.OpenStandardError();
            return await RunMainAsync(
                args,
                Console.OpenStandardInput(),
                Console.OpenStandardOutput(),
                standardError,
                new ProcessExecutor(),
                CopilotLaunchPolicyStore.GetDefaultLaunchRoot,
                ParentProcess.GetParentProcessId,
                () => Environment.ProcessPath,
                () => Process.GetCurrentProcess().MainModule?.FileName,
                cancellation.Token).ConfigureAwait(false);
        }
        catch
        {
            return standardError is null
                ? CopilotCliWrapper.InternalFailureExitCode
                : await CopilotCliWrapper.ReturnFailureAsync(
                    standardError,
                    CopilotCliWrapper.InternalFailureExitCode,
                    "Copilot wrapper startup failed.").ConfigureAwait(false);
        }
    }

    internal static async Task<int> RunMainAsync(
        IReadOnlyList<string> args,
        Stream standardInput,
        Stream standardOutput,
        Stream standardError,
        IProcessExecutor executor,
        Func<string> launchRootProvider,
        Func<int> parentProcessIdProvider,
        Func<string?> processPathProvider,
        Func<string?> fallbackProcessPathProvider,
        CancellationToken cancellationToken)
    {
        string wrapperPath;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            wrapperPath = ResolveWrapperProcessPath(
                processPathProvider,
                fallbackProcessPathProvider);
        }
        catch (OperationCanceledException)
        {
            return await CopilotCliWrapper.ReturnFailureAsync(
                standardError,
                CopilotCliWrapper.InternalFailureExitCode,
                "Copilot wrapper cancelled.").ConfigureAwait(false);
        }
        catch
        {
            return await CopilotCliWrapper.ReturnFailureAsync(
                standardError,
                CopilotCliWrapper.UnsafePathExitCode,
                "Unsafe Copilot wrapper path.").ConfigureAwait(false);
        }

        string launchRoot;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            launchRoot = launchRootProvider();
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            return await CopilotCliWrapper.ReturnFailureAsync(
                standardError,
                CopilotCliWrapper.InternalFailureExitCode,
                "Copilot wrapper cancelled.").ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
                or IOException
                or ArgumentException
                or SecurityException
                or NotSupportedException)
        {
            return await CopilotCliWrapper.ReturnFailureAsync(
                standardError,
                CopilotCliWrapper.UnsafePathExitCode,
                "Unsafe Copilot policy path.").ConfigureAwait(false);
        }
        catch
        {
            return await CopilotCliWrapper.ReturnFailureAsync(
                standardError,
                CopilotCliWrapper.InternalFailureExitCode,
                "Copilot wrapper startup failed.").ConfigureAwait(false);
        }

        int parentProcessId;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            parentProcessId = parentProcessIdProvider();
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            return await CopilotCliWrapper.ReturnFailureAsync(
                standardError,
                CopilotCliWrapper.InternalFailureExitCode,
                "Copilot wrapper cancelled.").ConfigureAwait(false);
        }
        catch
        {
            return await CopilotCliWrapper.ReturnFailureAsync(
                standardError,
                CopilotCliWrapper.InternalFailureExitCode,
                "Copilot wrapper startup failed.").ConfigureAwait(false);
        }

        return await CopilotCliWrapper.RunAsync(
            args,
            standardInput,
            standardOutput,
            standardError,
            executor,
            launchRoot,
            parentProcessId,
            wrapperPath,
            cancellationToken).ConfigureAwait(false);
    }

    internal static string ResolveWrapperProcessPath(
        Func<string?> processPathProvider,
        Func<string?> fallbackProcessPathProvider)
    {
        string? path = null;
        try
        {
            path = processPathProvider();
        }
        catch
        {
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            try
            {
                path = fallbackProcessPathProvider();
            }
            catch
            {
            }
        }
        return string.IsNullOrWhiteSpace(path)
            ? throw new UnauthorizedAccessException()
            : path;
    }
}

internal enum CopilotWrapperStartupPhase
{
    Envelope,
    Paths,
    Launch,
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
        CancellationToken cancellationToken = default,
        Action<CopilotWrapperStartupPhase>? startupObserver = null)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return await ReturnFailureAsync(
                standardError,
                InternalFailureExitCode,
                "Copilot wrapper cancelled.").ConfigureAwait(false);
        }

        if (args.Count < 4
            || !string.Equals(args[0], "--policy", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(args[1])
            || !string.Equals(args[2], "--copilot", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(args[3]))
        {
            return await ReturnFailureAsync(
                standardError,
                InvalidArgumentsExitCode,
                "Invalid wrapper arguments.").ConfigureAwait(false);
        }

        CopilotLaunchPolicyEnvelope envelope;
        try
        {
            envelope = new CopilotLaunchPolicyStore(launchRoot, TimeProvider.System)
                .Consume(args[1], parentProcessId);
            ObserveStartupPhase(
                CopilotWrapperStartupPhase.Envelope,
                startupObserver,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return await ReturnFailureAsync(
                standardError,
                InternalFailureExitCode,
                "Copilot wrapper cancelled.").ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            return await ReturnFailureAsync(
                standardError,
                UnsafePathExitCode,
                "Unsafe Copilot policy path.").ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or SecurityException
                or NotSupportedException)
        {
            return await ReturnFailureAsync(
                standardError,
                UnsafePathExitCode,
                "Unsafe Copilot policy path.").ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or IOException
                or JsonException)
        {
            return await ReturnFailureAsync(
                standardError,
                InvalidEnvelopeExitCode,
                "Invalid or expired Copilot policy.").ConfigureAwait(false);
        }
        catch
        {
            return await ReturnFailureAsync(
                standardError,
                InternalFailureExitCode,
                "Copilot wrapper startup failed.").ConfigureAwait(false);
        }

        string canonicalWrapperPath;
        try
        {
            canonicalWrapperPath = ValidateExecutablePath(wrapperPath);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            return await ReturnFailureAsync(
                standardError,
                InternalFailureExitCode,
                "Copilot wrapper cancelled.").ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
                or IOException
                or ArgumentException
                or SecurityException
                or NotSupportedException)
        {
            return await ReturnFailureAsync(
                standardError,
                UnsafePathExitCode,
                "Unsafe Copilot wrapper path.").ConfigureAwait(false);
        }
        catch
        {
            return await ReturnFailureAsync(
                standardError,
                InternalFailureExitCode,
                "Copilot wrapper startup failed.").ConfigureAwait(false);
        }

        string copilotPath;
        try
        {
            copilotPath = ValidateExecutablePath(args[3]);
            if (string.Equals(copilotPath, canonicalWrapperPath, PathComparison))
                throw new UnauthorizedAccessException();
            ObserveStartupPhase(
                CopilotWrapperStartupPhase.Paths,
                startupObserver,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return await ReturnFailureAsync(
                standardError,
                InternalFailureExitCode,
                "Copilot wrapper cancelled.").ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
                or IOException
                or ArgumentException
                or SecurityException
                or NotSupportedException)
        {
            return await ReturnFailureAsync(
                standardError,
                UnsafePathExitCode,
                "Unsafe Copilot executable path.").ConfigureAwait(false);
        }
        catch
        {
            return await ReturnFailureAsync(
                standardError,
                InternalFailureExitCode,
                "Copilot wrapper startup failed.").ConfigureAwait(false);
        }

        ProcessExecutionRequest request;
        try
        {
            var policy = AddRuntimeBootstrapGrant(
                envelope.Policy,
                System.IO.Path.GetDirectoryName(copilotPath)!);
            request = new ProcessExecutionRequest(copilotPath, args.Skip(4).ToArray())
            {
                WorkingDirectory = Environment.CurrentDirectory,
                Environment = SnapshotEnvironment(),
                MxcPolicy = policy,
            };
            ObserveStartupPhase(
                CopilotWrapperStartupPhase.Launch,
                startupObserver,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return await ReturnFailureAsync(
                standardError,
                InternalFailureExitCode,
                "Copilot wrapper cancelled.").ConfigureAwait(false);
        }
        catch
        {
            return await ReturnFailureAsync(
                standardError,
                InternalFailureExitCode,
                "Copilot wrapper startup failed.").ConfigureAwait(false);
        }

        IProcessHandle process;
        try
        {
            process = await executor.StartAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await ReturnFailureAsync(
                standardError,
                InternalFailureExitCode,
                "Copilot wrapper cancelled.").ConfigureAwait(false);
        }
        catch
        {
            return await ReturnFailureAsync(
                standardError,
                ExecutorFailureExitCode,
                "MXC containment launch failed.").ConfigureAwait(false);
        }

        var wrapperFailed = false;
        var exitCode = InternalFailureExitCode;
        string? failureDiagnostic = null;
        using (var inputCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            try
            {
                foreach (var warning in process.LaunchInfo.Warnings)
                {
                    await WriteStreamAsync(standardError, warning, cancellationToken)
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
                    await process.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
                var result = await waitForExit.ConfigureAwait(false);
                inputCancellation.Cancel();
                await process.StandardInput.DisposeAsync().ConfigureAwait(false);
                await IgnoreInputCancellationAsync(inputPump).ConfigureAwait(false);
                await Task.WhenAll(outputPump, errorPump).ConfigureAwait(false);
                await standardOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
                await standardError.FlushAsync(cancellationToken).ConfigureAwait(false);
                exitCode = result.ExitCode;
            }
            catch (OperationCanceledException)
            {
                wrapperFailed = true;
                failureDiagnostic = "Copilot wrapper cancelled.";
                await TryTerminateAsync(process).ConfigureAwait(false);
            }
            catch
            {
                wrapperFailed = true;
                failureDiagnostic = "Copilot wrapper stream relay failed.";
                await TryTerminateAsync(process).ConfigureAwait(false);
            }
        }

        try
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            if (!wrapperFailed)
            {
                wrapperFailed = true;
                failureDiagnostic = "Copilot wrapper stream relay failed.";
            }
        }

        return wrapperFailed
            ? await ReturnFailureAsync(
                standardError,
                InternalFailureExitCode,
                failureDiagnostic!).ConfigureAwait(false)
            : exitCode;
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

    internal static async Task<int> ReturnFailureAsync(
        Stream standardError,
        int exitCode,
        string message)
    {
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(message + Environment.NewLine);
            await standardError.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
            await standardError.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
        return exitCode;
    }

    private static async Task WriteStreamAsync(
        Stream stream,
        string message,
        CancellationToken cancellationToken)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(message + Environment.NewLine);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ValidateExecutablePath(string path)
    {
        var canonical = System.IO.Path.GetFullPath(path);
        if (!System.IO.Path.IsPathFullyQualified(canonical)
            || !File.Exists(canonical)
            || Directory.Exists(canonical)
            || (File.GetAttributes(canonical) & (FileAttributes.Directory | FileAttributes.Device))
                != 0)
        {
            throw new UnauthorizedAccessException();
        }
        CopilotPathSecurity.EnsureNoReparsePoints(canonical);
        return canonical;
    }

    private static void ObserveStartupPhase(
        CopilotWrapperStartupPhase phase,
        Action<CopilotWrapperStartupPhase>? observer,
        CancellationToken cancellationToken)
    {
        observer?.Invoke(phase);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task TryTerminateAsync(IProcessHandle process)
    {
        try
        {
            await process.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
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
