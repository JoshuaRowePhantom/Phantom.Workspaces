namespace Phantom.Workspaces;

/// <summary>
/// Issue #1186: Centralises the "initialize the view-model behind the splash" run so
/// that the loading window is ALWAYS dismissed via <see langword="finally"/>, no matter
/// how the initialize path exits. Before #1186 the splash was dismissed only on the
/// success line (<c>loadingWindow.Close()</c> just after <c>mainWindow.Show()</c>);
/// a fault inside <c>viewModel.InitializeAsync()</c> — or, per the diagnosed bug, an
/// unobserved fault buried inside <c>RestoreSubAgentsAsync</c> — could leave the
/// splash stuck in front of every other window indefinitely.
/// </summary>
internal static class StartupSplashRunner
{
    /// <summary>
    /// Runs <paramref name="initializeAsync"/>. On success invokes
    /// <paramref name="postInitialize"/> and returns <see langword="true"/>. On
    /// exception invokes <paramref name="setStatus"/> with the failure message,
    /// awaits <paramref name="onFaultDelay"/>, invokes <paramref name="shutdown"/>
    /// and returns <see langword="false"/>. In every case, <paramref name="closeSplash"/>
    /// runs from a <see langword="finally"/> block so the loading window is always
    /// dismissed.
    /// </summary>
    internal static async Task<bool> RunWithSplashDismissAsync(
        Func<Task> initializeAsync,
        Action<string> setStatus,
        Func<Task> onFaultDelay,
        Action shutdown,
        Action postInitialize,
        Action closeSplash)
    {
        try
        {
            try
            {
                await initializeAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
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
