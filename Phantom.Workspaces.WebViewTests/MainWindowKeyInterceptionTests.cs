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

    [Fact]
    public Task OnPreviewKeyDown_LoneCtrl_DoesNotOpenNavigationStackPopup()
        => this.fixture.InvokeAsync(async () =>
        {
            await using var viewModel = new MainWindowViewModel(
                new UnknownRepositorySource(),
                new WorkspacesConfiguration { SkipStartupWorkspace = true });
            await viewModel.InitializeAsync();

            var window = new MainWindow(viewModel);
            window.Show();
            try
            {
                await Task.Yield();

                var keyEventArgs = new KeyEventArgs
                {
                    Key = Key.LeftCtrl,
                    KeyModifiers = KeyModifiers.Control,
                    RoutedEvent = InputElement.KeyDownEvent,
                    Source = window
                };
                window.RaiseEvent(keyEventArgs);

                Assert.False(viewModel.NavStackPopup.IsOpen);
                Assert.False(keyEventArgs.Handled);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task OnPreviewKeyDown_CtrlC_DoesNotOpenNavigationStackPopupOrHandleC()
        => this.fixture.InvokeAsync(async () =>
        {
            await using var viewModel = new MainWindowViewModel(
                new UnknownRepositorySource(),
                new WorkspacesConfiguration { SkipStartupWorkspace = true });
            await viewModel.InitializeAsync();

            var window = new MainWindow(viewModel);
            window.Show();
            try
            {
                await Task.Yield();

                window.RaiseEvent(new KeyEventArgs
                {
                    Key = Key.LeftCtrl,
                    KeyModifiers = KeyModifiers.Control,
                    RoutedEvent = InputElement.KeyDownEvent,
                    Source = window
                });
                var cKeyDown = new KeyEventArgs
                {
                    Key = Key.C,
                    KeyModifiers = KeyModifiers.Control,
                    RoutedEvent = InputElement.KeyDownEvent,
                    Source = window
                };
                window.RaiseEvent(cKeyDown);
                window.RaiseEvent(new KeyEventArgs
                {
                    Key = Key.LeftCtrl,
                    RoutedEvent = InputElement.KeyUpEvent,
                    Source = window
                });

                Assert.False(viewModel.NavStackPopup.IsOpen);
                Assert.False(viewModel.NavStackPopup.IsAutoClosing);
                Assert.False(cKeyDown.Handled);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task OnPreviewKeyDown_CtrlTab_OpensNavigationStackPopup()
        => this.fixture.InvokeAsync(async () =>
        {
            await using var viewModel = new MainWindowViewModel(
                new UnknownRepositorySource(),
                new WorkspacesConfiguration { SkipStartupWorkspace = true });
            await viewModel.InitializeAsync();

            var window = new MainWindow(viewModel);
            window.Show();
            try
            {
                await Task.Yield();

                window.RaiseEvent(new KeyEventArgs
                {
                    Key = Key.LeftCtrl,
                    KeyModifiers = KeyModifiers.Control,
                    RoutedEvent = InputElement.KeyDownEvent,
                    Source = window
                });
                window.RaiseEvent(new KeyEventArgs
                {
                    Key = Key.Tab,
                    KeyModifiers = KeyModifiers.Control,
                    RoutedEvent = InputElement.KeyDownEvent,
                    Source = window
                });

                Assert.True(viewModel.NavStackPopup.IsOpen);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task NavStackPopup_CtrlDownArrow_OpensPopupAndCommitsOnCtrlRelease()
        => this.fixture.InvokeAsync(async () =>
        {
            await using var viewModel = new MainWindowViewModel(
                new UnknownRepositorySource(),
                new WorkspacesConfiguration { SkipStartupWorkspace = true });
            await viewModel.InitializeAsync();

            var tabA = new WebViewModel("https://nav-a.example.com") { Id = "nav-ctrl-a", Title = "A" };
            var tabB = new WebViewModel("https://nav-b.example.com") { Id = "nav-ctrl-b", Title = "B" };
            var tabC = new WebViewModel("https://nav-c.example.com") { Id = "nav-ctrl-c", Title = "C" };
            await viewModel.OpenTabAsync(tabA);
            await viewModel.OpenTabAsync(tabB);
            await viewModel.OpenTabAsync(tabC);

            var pane = viewModel.SelectedWorkspacePane;
            Assert.Equal("nav-ctrl-c", pane.SelectedTab?.Id);

            var window = new MainWindow(viewModel);
            window.Show();
            try
            {
                await Task.Yield();

                window.RaiseEvent(new KeyEventArgs
                {
                    Key = Key.LeftCtrl,
                    KeyModifiers = KeyModifiers.Control,
                    RoutedEvent = InputElement.KeyDownEvent,
                    Source = window
                });
                var downIntent = new KeyEventArgs
                {
                    Key = Key.Down,
                    KeyModifiers = KeyModifiers.Control,
                    RoutedEvent = InputElement.KeyDownEvent,
                    Source = window
                };
                window.RaiseEvent(downIntent);

                Assert.True(downIntent.Handled);
                Assert.True(viewModel.NavStackPopup.IsOpen);
                Assert.Equal(1, viewModel.NavStackPopup.SelectedIndex);

                var selectedTabChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnPanePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(WorkspacePaneViewModel.SelectedTab)
                        && pane.SelectedTab?.Id == "nav-ctrl-b")
                    {
                        selectedTabChanged.TrySetResult();
                    }
                }

                pane.PropertyChanged += OnPanePropertyChanged;
                try
                {
                    window.RaiseEvent(new KeyEventArgs
                    {
                        Key = Key.LeftCtrl,
                        RoutedEvent = InputElement.KeyUpEvent,
                        Source = window
                    });

                    if (pane.SelectedTab?.Id != "nav-ctrl-b")
                    {
                        await selectedTabChanged.Task;
                    }
                }
                finally
                {
                    pane.PropertyChanged -= OnPanePropertyChanged;
                }

                Assert.True(viewModel.NavStackPopup.IsAutoClosing);
                Assert.Equal("nav-ctrl-b", pane.SelectedTab?.Id);
            }
            finally
            {
                window.Close();
            }
        });
}
