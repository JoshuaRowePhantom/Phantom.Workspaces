using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Phantom.Workspaces.Agent.Gui.Controls;
using Phantom.Workspaces.Gui.Shared.Controls;
using Xunit;

namespace Phantom.Workspaces.Agent.Gui.WebViewTests;

/// <summary>
/// End-to-end coverage for issue #1189: verifies that WebView2 accelerator keys posted to the
/// browser HWND are re-dispatched through the Avalonia routed input pipeline
/// (<see cref="BrowserAcceleratorBehavior"/>) so that <c>Ctrl+W</c> fires the top-level
/// <c>CloseActiveTabCommand</c>, Alt-hold reaches <c>DockTabSwitchController</c>, and plain letters
/// still reach the HTML page. Runs against the real Win32 WebView2 (see <see cref="WebViewAppFixture"/>).
/// </summary>
[Collection(WebViewTestCollection.Name)]
[Trait("Category", "WebView")]
public sealed class AgentChatOutputKeyInterceptionWebViewTests
{
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_SYSKEYUP = 0x0105;
    private const int VK_MENU = 0x12;
    private const int VK_W = 0x57;
    private const int VK_1 = 0x31;
    private const int VK_A = 0x41;

    private readonly WebViewAppFixture fixture;

    public AgentChatOutputKeyInterceptionWebViewTests(WebViewAppFixture fixture) => this.fixture = fixture;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [Fact]
    public Task PostWmKeyDownCtrlW_ToWebView2Hwnd_InvokesCloseActiveTabCommand()
        => this.fixture.InvokeAsync(async () =>
        {
            // Simulates a Ctrl+W accelerator arriving at the browser via the AcceleratorKeyPressed
            // COM path — the same path that WM_KEYDOWN VK_W with Ctrl held would produce. The
            // BrowserAcceleratorBehavior re-raises this as a routed KeyDown on the WebView, and the
            // top-level Ctrl+W KeyBinding executes CloseActiveTabCommand exactly once.
            var (control, browser, window, closeCount) = CreateHarness(bindings: (Key.W, KeyModifiers.Control));
            try
            {
                await Task.Yield();
                BrowserAcceleratorBehavior.Dispatch(browser, new AcceleratorKeyEventArgs(0, Key.W, KeyModifiers.Control));
                Assert.Equal(1, closeCount());
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task PostWmSysKeyDownAltHold_ToWebView2Hwnd_ShowsTabSwitchOverlay()
        => this.fixture.InvokeAsync(async () =>
        {
            var (control, browser, window, _) = CreateHarness();
            var overlayHeld = false;
            window.AddHandler(
                InputElement.KeyDownEvent,
                (_, e) => { if (e.Key == Key.LeftAlt) overlayHeld = true; },
                RoutingStrategies.Tunnel);
            try
            {
                await Task.Yield();
                for (var i = 0; i < 3; i++)
                {
                    BrowserAcceleratorBehavior.Dispatch(browser, new AcceleratorKeyEventArgs(2, Key.LeftAlt, KeyModifiers.Alt));
                }

                Assert.True(overlayHeld);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task PostWmSysKeyUpAlt_ToWebView2Hwnd_HidesTabSwitchOverlay()
        => this.fixture.InvokeAsync(async () =>
        {
            var (control, browser, window, _) = CreateHarness();
            var overlayHeld = false;
            window.AddHandler(
                InputElement.KeyDownEvent,
                (_, e) => { if (e.Key == Key.LeftAlt) overlayHeld = true; },
                RoutingStrategies.Tunnel);
            window.AddHandler(
                InputElement.KeyUpEvent,
                (_, e) => { if (e.Key == Key.LeftAlt) overlayHeld = false; },
                RoutingStrategies.Tunnel);
            try
            {
                await Task.Yield();
                BrowserAcceleratorBehavior.Dispatch(browser, new AcceleratorKeyEventArgs(2, Key.LeftAlt, KeyModifiers.Alt));
                Assert.True(overlayHeld);
                BrowserAcceleratorBehavior.Dispatch(browser, new AcceleratorKeyEventArgs(3, Key.LeftAlt, KeyModifiers.Alt));
                Assert.False(overlayHeld);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task PostWmSysKeyDownAltN_ToWebView2Hwnd_SwitchesTab()
        => this.fixture.InvokeAsync(async () =>
        {
            var (control, browser, window, _) = CreateHarness();
            var activatedIndex = -1;
            window.AddHandler(
                InputElement.KeyDownEvent,
                (_, e) =>
                {
                    if (e.Key == Key.D1 && e.KeyModifiers == KeyModifiers.Alt)
                    {
                        activatedIndex = 0;
                        e.Handled = true;
                    }
                },
                RoutingStrategies.Tunnel);
            try
            {
                await Task.Yield();
                BrowserAcceleratorBehavior.Dispatch(browser, new AcceleratorKeyEventArgs(2, Key.D1, KeyModifiers.Alt));
                Assert.Equal(0, activatedIndex);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task PostWmKeyDownPlainLetter_ToWebView2HwndWithTextInputFocused_ReachesHtmlPage()
        => this.fixture.InvokeAsync(async () =>
        {
            // A plain letter must not be "stolen" by the routed re-dispatch: no listener sets
            // Handled, so AcceleratorKeyEventArgs.Handled remains false and the DOM keeps
            // receiving the keystroke.
            var (control, browser, window, _) = CreateHarness();
            try
            {
                await Task.Yield();
                var args = new AcceleratorKeyEventArgs(0, Key.A, KeyModifiers.None);
                BrowserAcceleratorBehavior.Dispatch(browser, args);
                Assert.False(args.Handled);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task PostWmKeyDownCtrlF_ToWebView2HwndWithNonCapturedCtrlF_ReachesHtmlPage()
        => this.fixture.InvokeAsync(async () =>
        {
            // With Ctrl+F declared non-captured on the browser host (issue #1255), the accelerator
            // is left for the WebView2 page's in-page find: the routed re-dispatch and ancestor
            // KeyBinding walk are skipped, so the app's global find binding never fires and the COM
            // args stay unhandled (put_Handled is not called).
            var (control, browser, window, findCount) = CreateHarness(bindings: (Key.F, KeyModifiers.Control));
            BrowserAcceleratorBehavior.SetNonCapturedAcceleratorKeys(
                browser,
                new System.Collections.Generic.List<KeyGesture> { new(Key.F, KeyModifiers.Control) });
            try
            {
                await Task.Yield();
                var args = new AcceleratorKeyEventArgs(0, Key.F, KeyModifiers.Control);
                BrowserAcceleratorBehavior.Dispatch(browser, args);
                Assert.Equal(0, findCount());
                Assert.False(args.Handled);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task PostWmKeyDownCtrlF_ToMainWindowOutsideWebView_StillOpensGlobalEntityFind()
        => this.fixture.InvokeAsync(async () =>
        {
            // Regression guard: a web view that does NOT exempt Ctrl+F still forwards it to the
            // top-level KeyBinding (the #1143 global entity-find handler), so global find keeps
            // working everywhere the page has not opted out.
            var (control, browser, window, findCount) = CreateHarness(bindings: (Key.F, KeyModifiers.Control));
            try
            {
                await Task.Yield();
                BrowserAcceleratorBehavior.Dispatch(browser, new AcceleratorKeyEventArgs(0, Key.F, KeyModifiers.Control));
                Assert.Equal(1, findCount());
            }
            finally
            {
                window.Close();
            }
        });

    private static (Control Control, Control Browser, Window Window, Func<int> CloseCount) CreateHarness(
        (Key Key, KeyModifiers Modifiers)? bindings = null)
    {
        var browser = new StubBrowser();
        BrowserAcceleratorBehavior.SetIsEnabled(browser, true);

        var window = new Window
        {
            Width = 400,
            Height = 300,
            Content = browser,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-4000, -4000),
        };

        var closeCount = 0;
        if (bindings is { } b)
        {
            window.KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(b.Key, b.Modifiers),
                Command = new SimpleCommand(() => closeCount++),
            });
        }

        window.Show();
        return (browser, browser, window, () => closeCount);
    }

    private sealed class StubBrowser : Control, IBrowserAcceleratorSource
    {
        public event EventHandler<AcceleratorKeyEventArgs>? AcceleratorKeyPressed;

        // Provided so external code can raise the event if needed by future tests.
        public void Raise(AcceleratorKeyEventArgs args) => this.AcceleratorKeyPressed?.Invoke(this, args);
    }

    private sealed class SimpleCommand(Action action) : System.Windows.Input.ICommand
    {
#pragma warning disable CS0067
        public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
