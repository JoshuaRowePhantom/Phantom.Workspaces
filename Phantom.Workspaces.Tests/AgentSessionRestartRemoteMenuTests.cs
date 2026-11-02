using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Services.AgentSessions;
using Phantom.Workspaces.ViewModels;
using AgentViewModel = Phantom.Workspaces.Agent.Gui.ViewModels.AgentViewModel;

namespace Phantom.Workspaces.Tests;

public sealed partial class MainWindowTabCommandsIntegrationTests
{
    [AvaloniaFact]
    public async Task RestartSession_RemoteNativeMenu_StoppedRetryTargetsOriginalOperation()
    {
        var foreground = TaskScheduler.FromCurrentSynchronizationContext();
        await using var environment = new DataAccessAgentSessionRuntimeHostFactoryTests.RestartEnvironment(
            foregroundScheduler: foreground);
        await using var owner = await environment.AcquireAsync();
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        Assert.IsType<AgentSessionRestartCoordinator>(viewModel.AgentSessionRestartService);
        viewModel.AgentSessionRestartConsentProvider = new DelegateConsentProvider((_, _) => Task.FromResult(true));
        var viewer = await environment.AcquireRemoteAsync(foreground);
        var remote = Assert.IsType<RemoteAgentChat>(viewer.AgentChat);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.RuntimeStateChanged += (_, _) =>
        {
            if (remote.IsTerminal && !remote.IsConnected)
                stopped.TrySetResult();
        };
        var logger = new ObservableLoggerFactory();
        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = "remote-native-retry", Title = "Remote retry", AgentSessionId = "session",
            Entity = CreateWorkspaceEntity("remote-native-retry"),
        };
        tab.SetLease(viewer);
        tab.SetReady(new AgentViewModel(remote, "remote", "", logger, foreground), logger);
        await viewModel.OpenTabAsync(tab);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tab.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(tab.IsRestartInProgress) && !tab.IsRestartInProgress)
                completed.TrySetResult();
        };
        environment.FailStartup = true;
        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            var strip = await WaitForContentTabStripAsync(window);
            var item = strip.GetVisualDescendants().OfType<DocumentTabStripItem>()
                .Single(value => value.DataContext is WorkspaceDocument { Id: "remote-native-retry" });
            await OpenNativeMenuAsync(window, item);
            var menu = Assert.IsType<ContextMenu>(item.DocumentContextMenu);
            var restart = Assert.Single(menu.Items.OfType<MenuItem>(), value => Equals(value.Header, "Restart agent session…"));
            Assert.True(restart.IsEnabled);
            restart.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            await completed.Task.WaitAsync(TestContext.Current.CancellationToken);
            await stopped.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Contains("Retry", tab.RestartError);
            var operation = Assert.IsType<AgentSessionOwnerRestartRequest>(viewer.RestartCommandState.Pending);
            Assert.Equal(AgentSessionOwnerRestartState.Stopped, viewer.RestartCommandState.Result!.State);
            Assert.Same(viewer.RestartCommandState, remote.RestartClient.RestartCommandState);
            completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            environment.FailStartup = false;
            menu.Close();
            await OpenNativeMenuAsync(window, item);
            restart = Assert.Single(menu.Items.OfType<MenuItem>(), value => Equals(value.Header, "Restart agent session…"));
            Assert.True(restart.IsEnabled);
            restart.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            await completed.Task.WaitAsync(TestContext.Current.CancellationToken);
            await remote.ReconnectNowAsync(TestContext.Current.CancellationToken);
            Assert.Null(tab.RestartError);
            Assert.Same(remote, tab.Lease!.AgentChat);
            Assert.True(remote.IsConnected);
            Assert.False(remote.IsTerminal);
            Assert.Equal(AgentSessionOwnerRestartState.Succeeded,
                (await viewer.RestartStatusAsync!(operation, TestContext.Current.CancellationToken))!.State);
            Assert.NotEqual(operation.ExpectedRuntime.RuntimeEpoch, remote.RestartClient.RestartIdentity!.RuntimeEpoch);
            Assert.Single(environment.Table.RunningSessions);
        }
        finally
        {
            await MainWindowIntegrationTests.CloseWindowAsync(window);
        }
    }
}
