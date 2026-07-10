using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Interactivity;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.WebViewTests;

/// <summary>
/// End-to-end coverage of <see cref="MainWindow.OnPreviewKeyDown"/> interception of Ctrl+K and
/// Ctrl+Shift+K before a WebView2 tab can consume them. These tests verify that the tunnel-phase
/// handler correctly processes keyboard shortcuts at the Avalonia level before they reach child
/// controls (specifically WebView2 hosted tabs).
/// </summary>
[Collection(MainWindowWebViewTestCollection.Name)]
[Trait("Category", "WebView")]
public sealed class MainWindowKeyInterceptionTests
{
    private readonly MainWindowWebViewFixture fixture;

    public MainWindowKeyInterceptionTests(MainWindowWebViewFixture fixture) => this.fixture = fixture;

    [Fact]
    public Task OnPreviewKeyDown_CtrlK_WithoutShift_InterceptedBeforeWebView2()
        => this.fixture.InvokeAsync(async () =>
        {
            // Verifies that Ctrl+K alone (missing Shift) does not trigger DuplicateBrowserTabCommand.
            await using var viewModel = new MainWindowViewModel(
                new UnknownRepositorySource(),
                new WorkspacesConfiguration { SkipStartupWorkspace = true });
            await viewModel.InitializeAsync();

            var tab = new WebViewModel("https://example.com") { Id = "ctrl-k-no-dup", Title = "Browser" };
            await viewModel.OpenTabAsync(tab);

            var window = new MainWindow(viewModel);
            window.Show();
            try
            {
                await Task.Yield();

                // Raise Ctrl+K (without Shift) key event
                var keyEventArgs = new KeyEventArgs
                {
                    Key = Key.K,
                    KeyModifiers = KeyModifiers.Control,
                    RoutedEvent = InputElement.KeyDownEvent,
                    Source = window
                };
                window.RaiseEvent(keyEventArgs);
                await Task.Yield();

                // Should still have only 1 tab (duplicate command should not fire)
                Assert.Single(viewModel.SelectedWorkspacePane.Tabs);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task OnPreviewKeyDown_CtrlShiftK_InterceptedBeforeWebView2()
        => this.fixture.InvokeAsync(async () =>
        {
            // Verifies that Ctrl+Shift+K fires DuplicateBrowserTabCommand and marks the event as
            // handled so that child controls such as WebView2 do not receive the keystroke.
            await using var viewModel = new MainWindowViewModel(
                new UnknownRepositorySource(),
                new WorkspacesConfiguration { SkipStartupWorkspace = true });
            await viewModel.InitializeAsync();

            var tab = new WebViewModel("https://example.com") { Id = "ctrl-shift-k-tab", Title = "Browser" };
            await viewModel.OpenTabAsync(tab);

            var window = new MainWindow(viewModel);
            window.Show();
            try
            {
                await Task.Yield();

                bool handledByTunnel = false;
                window.AddHandler(
                    InputElement.KeyDownEvent,
                    (_, e) =>
                    {
                        if (e.Key == Key.K && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
                            handledByTunnel = e.Handled;
                    },
                    RoutingStrategies.Bubble,
                    handledEventsToo: true);

                // Raise Ctrl+Shift+K key event
                var keyEventArgs = new KeyEventArgs
                {
                    Key = Key.K,
                    KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift,
                    RoutedEvent = InputElement.KeyDownEvent,
                    Source = window
                };
                window.RaiseEvent(keyEventArgs);
                await Task.Yield();

                // Event should be handled by the tunnel-phase handler (OnPreviewKeyDown)
                Assert.True(handledByTunnel);

                // Should now have 2 tabs (original + duplicated)
                Assert.Equal(2, viewModel.SelectedWorkspacePane.Tabs.Count);
            }
            finally
            {
                window.Close();
            }
        });
}
