namespace Phantom.Workspaces;

using Microsoft.Extensions.Logging;

/// <summary>
/// Issue #1186: Centralises the "initialize the view-model behind the splash" run so
/// that the loading window is ALWAYS dismissed via <see langword="finally"/>, no matter
/// how the initialize path exits. Before #1186 the splash was dismissed only on the
/// success line (<c>loadingWindow.Close()</c> just after <c>mainWindow.Show()</c>);
/// a fault inside <c>viewModel.InitializeAsync()</c> — or, per the diagnosed bug, an
/// unobserved fault buried inside <c>RestoreSubAgentsAsync</c> — could leave the
/// splash stuck in front of every other window indefinitely.
/// Issue #1294: The runner now also owns startup-connect logging so exceptions that would
/// otherwise be shown only in the splash <c>StatusText</c> are written to the rolling
/// file sink via the injected <see cref="ILoggerFactory"/>.
/// </summary>
internal sealed class StartupSplashRunner
{
    private StartupSplashRunner() { }

    /// <summary>
    /// Runs <paramref name="initializeAsync"/>. On success invokes
    /// <paramref name="postInitialize"/> and returns <see langword="true"/>. On
    /// exception logs the exception via <paramref name="loggerFactory"/> BEFORE invoking
    /// <paramref name="setStatus"/> with the failure message, awaiting
    /// <paramref name="onFaultDelay"/>, invoking <paramref name="shutdown"/>
    /// and returning <see langword="false"/>. In every case, <paramref name="closeSplash"/>
    /// runs from a <see langword="finally"/> block so the loading window is always
    /// dismissed.
    /// </summary>
    internal static async Task<bool> RunWithSplashDismissAsync(
        ILoggerFactory loggerFactory,
        Func<Task> initializeAsync,
        Action<string> setStatus,
        Func<Task> onFaultDelay,
        Action shutdown,
        Action postInitialize,
        Action closeSplash)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        var logger = loggerFactory.CreateLogger<StartupSplashRunner>();
        try
        {
            try
            {
                logger.LogInformation("Startup connect: beginning initialize.");
                await initializeAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // Issue #1294: Log the full exception (type + message + stack) to the rolling
                // file sink BEFORE showing the splash message, awaiting the fault delay, or
                // shutting down. Doing it first keeps the entry flushable even if shutdown
                // races the file sink and gives users a diagnostic trail beyond ex.Message.
                logger.LogError(ex, "Startup connect failed.");
                setStatus($"Failed to connect: {ex.Message}");
                await onFaultDelay().ConfigureAwait(true);
                shutdown();
                return false;
            }

            postInitialize();
            return true;
        }
        finally
        {
            closeSplash();
        }
    }
}
