using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Phantom.Workspaces.Gui.Shared.Controls;

/// <summary>
/// Small local mirror of <c>COREWEBVIEW2_KEY_EVENT_KIND</c>. Values match Microsoft's SDK:
/// <c>KEY_DOWN=0, KEY_UP=1, SYSTEM_KEY_DOWN=2, SYSTEM_KEY_UP=3</c>.
/// </summary>
internal static class CoreWebView2KeyEventKind
{
    public const int KeyDown = 0;
    public const int KeyUp = 1;
    public const int SystemKeyDown = 2;
    public const int SystemKeyUp = 3;
}

/// <summary>
/// COM interface for reading accelerator-key event args from WebView2 (IID
/// <c>9F760F8A-FB79-42BE-9990-7B56900FA9C7</c>). Locally re-declared as <c>[ComImport]</c> so
/// this project does not need to reference the SDK's Windows-only projection assembly.
/// </summary>
[ComImport]
[Guid("9F760F8A-FB79-42BE-9990-7B56900FA9C7")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICoreWebView2AcceleratorKeyPressedEventArgs
{
    [PreserveSig] int get_KeyEventKind(out int kind);
    [PreserveSig] int get_VirtualKey(out uint virtualKey);
    [PreserveSig] int get_KeyEventLParam(out int lParam);
    // COREWEBVIEW2_PHYSICAL_KEY_STATUS is a struct we never read; we occupy the vtable slot with
    // a 64-bit placeholder so the following methods map to the correct indices.
    [PreserveSig] int get_PhysicalKeyStatus(out long statusPlaceholder);
    [PreserveSig] int get_Handled(out int handled);
    [PreserveSig] int set_Handled(int handled);
}

/// <summary>
/// COM interface WebView2 calls back on for accelerator-key events (IID
/// <c>B29C7E28-FA79-41A8-8E44-65811C76DCB2</c>). Our managed handler implements this so we can be
/// registered directly via <see cref="ICoreWebView2Controller.add_AcceleratorKeyPressed"/> — no
/// reflection, no <c>Marshal.GetComInterfaceForObject</c> round-trip needed.
/// </summary>
[ComVisible(true)]
[Guid("B29C7E28-FA79-41A8-8E44-65811C76DCB2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICoreWebView2AcceleratorKeyPressedEventHandler
{
    [PreserveSig] int Invoke(nint sender, nint args);
}

/// <summary>
/// COM interface for <c>ICoreWebView2Controller</c> (IID <c>4D00C0D1-9434-4EB6-8078-8697A560334F</c>).
/// Only the two accelerator-key event slots are exposed — earlier vtable slots are stubbed with
/// <c>PreserveSig</c> placeholders that we never call, so the vtable indices for
/// <c>add_AcceleratorKeyPressed</c> and <c>remove_AcceleratorKeyPressed</c> line up with the real
/// interface (slots 15 and 16, following the ordering in <c>WebView2.idl</c>).
/// </summary>
[ComImport]
[Guid("4D00C0D1-9434-4EB6-8078-8697A560334F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICoreWebView2Controller
{
    [PreserveSig] int get_IsVisible(out int isVisible);
    [PreserveSig] int set_IsVisible(int isVisible);
    [PreserveSig] int get_Bounds(out RECT bounds);
    [PreserveSig] int set_Bounds(RECT bounds);
    [PreserveSig] int get_ZoomFactor(out double zoomFactor);
    [PreserveSig] int set_ZoomFactor(double zoomFactor);
    [PreserveSig] int add_ZoomFactorChanged(nint eventHandler, out long token);
    [PreserveSig] int remove_ZoomFactorChanged(long token);
    [PreserveSig] int SetBoundsAndZoomFactor(RECT bounds, double zoomFactor);
    [PreserveSig] int MoveFocus(int reason);
    [PreserveSig] int add_MoveFocusRequested(nint eventHandler, out long token);
    [PreserveSig] int remove_MoveFocusRequested(long token);
    [PreserveSig] int add_GotFocus(nint eventHandler, out long token);
    [PreserveSig] int remove_GotFocus(long token);
    [PreserveSig] int add_LostFocus(nint eventHandler, out long token);
    [PreserveSig] int remove_LostFocus(long token);
    [PreserveSig] int add_AcceleratorKeyPressed(
        [MarshalAs(UnmanagedType.Interface)] ICoreWebView2AcceleratorKeyPressedEventHandler eventHandler,
        out long token);
    [PreserveSig] int remove_AcceleratorKeyPressed(long token);
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

/// <summary>
/// Interop surface between the Avalonia <c>NativeWebView</c> and the WebView2 SDK's raw
/// <c>ICoreWebView2Controller::AcceleratorKeyPressed</c> event. Uses only public Avalonia surface
/// (<see cref="NativeWebView.TryGetPlatformHandle"/> + <see cref="IWindowsWebView2PlatformHandle"/>)
/// — no reflection into Avalonia internals, no <c>&lt;controller&gt;P</c> field probing, no
/// <c>add_AcceleratorKeyPressed</c> method probing. See #1208.
/// </summary>
internal static class WebView2AcceleratorInterop
{
    /// <summary>
    /// Testable subscribe seam: bridges an <see cref="IPlatformHandle"/> to the WebView2
    /// <see cref="ICoreWebView2Controller"/> and installs an accelerator-key handler that
    /// forwards every event through the supplied <paramref name="listener"/>.
    /// </summary>
    /// <returns>An <see cref="IDisposable"/> that unsubscribes and releases COM references, or
    /// <c>null</c> if <paramref name="handle"/> is not an <see cref="IWindowsWebView2PlatformHandle"/>
    /// or does not expose a controller pointer.</returns>
    [SupportedOSPlatform("windows")]
    public static IDisposable? Subscribe(
        IPlatformHandle? handle,
        Action<AcceleratorKeyEventArgs> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        if (handle is not IWindowsWebView2PlatformHandle webView2Handle)
        {
            Trace.TraceWarning(
                "WebView2AcceleratorInterop.Subscribe: platform handle is not IWindowsWebView2PlatformHandle (was {0}).",
                handle?.GetType().FullName ?? "<null>");
            return null;
        }

        var controllerPtr = webView2Handle.CoreWebView2Controller;
        if (controllerPtr == IntPtr.Zero)
        {
            Trace.TraceWarning("WebView2AcceleratorInterop.Subscribe: CoreWebView2Controller pointer is zero.");
            return null;
        }

        ICoreWebView2Controller controller;
        try
        {
            controller = (ICoreWebView2Controller)Marshal.GetObjectForIUnknown(controllerPtr);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "WebView2AcceleratorInterop.Subscribe: failed to marshal ICoreWebView2Controller: {0}", ex);
            return null;
        }

        var handler = new AcceleratorKeyPressedHandler(listener);
        int hr = controller.add_AcceleratorKeyPressed(handler, out var token);
        if (hr < 0)
        {
            Trace.TraceWarning(
                "WebView2AcceleratorInterop.Subscribe: add_AcceleratorKeyPressed returned 0x{0:X8}.", hr);
            Marshal.ReleaseComObject(controller);
            return null;
        }

        return new Subscription(controller, token, handler);
    }

    /// <summary>Convenience overload for callers holding a <see cref="NativeWebView"/>.</summary>
    [SupportedOSPlatform("windows")]
    public static IDisposable? Subscribe(
        NativeWebView webView,
        Action<AcceleratorKeyEventArgs> listener)
    {
        ArgumentNullException.ThrowIfNull(webView);
        return Subscribe(webView.TryGetPlatformHandle(), listener);
    }

    /// <summary>
    /// Maps a WebView2 <c>COREWEBVIEW2_KEY_EVENT_KIND</c> + virtual-key pair into an Avalonia
    /// (<c>Key</c>, <c>KeyModifiers</c>) accelerator payload for the routed re-dispatch layer.
    /// Alt is inferred from the SystemKey* kinds; Ctrl/Shift come from live keyboard state via
    /// <paramref name="isKeyDown"/>. Extracted so it can be unit-tested without a live controller.
    /// </summary>
    internal static AcceleratorKeyEventArgs BuildArgs(int kind, int vk, Func<int, bool> isKeyDown)
    {
        var (key, modifiers) = VirtualKeyMap.Map(kind, vk, isKeyDown);
        return new AcceleratorKeyEventArgs(kind, key, modifiers);
    }

    internal static bool IsKeyDown(int virtualKey) =>
        OperatingSystem.IsWindows() && (GetAsyncKeyState(virtualKey) & unchecked((short)0x8000)) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int nVirtKey);

    [SupportedOSPlatform("windows")]
    private sealed class Subscription : IDisposable
    {
        private readonly ICoreWebView2Controller controller;
        private readonly long token;
        // Keep the handler alive for the lifetime of the subscription so the CCW is not collected
        // while COM still holds a reference.
        private readonly AcceleratorKeyPressedHandler handler;
        private int disposed;

        public Subscription(
            ICoreWebView2Controller controller,
            long token,
            AcceleratorKeyPressedHandler handler)
        {
            this.controller = controller;
            this.token = token;
            this.handler = handler;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            {
                return;
            }

            try
            {
                var hr = this.controller.remove_AcceleratorKeyPressed(this.token);
                if (hr < 0)
                {
                    Trace.TraceWarning(
                        "WebView2AcceleratorInterop.Subscription.Dispose: remove_AcceleratorKeyPressed returned 0x{0:X8}.",
                        hr);
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    "WebView2AcceleratorInterop.Subscription.Dispose: remove_AcceleratorKeyPressed threw: {0}", ex);
            }

            try
            {
                Marshal.ReleaseComObject(this.controller);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    "WebView2AcceleratorInterop.Subscription.Dispose: ReleaseComObject failed: {0}", ex);
            }

            GC.KeepAlive(this.handler);
        }
    }

    /// <summary>
    /// COM-callable wrapper that WebView2 calls back on accelerator-key events. Marshals the SDK
    /// raw args into an <see cref="AcceleratorKeyEventArgs"/>, forwards to the listener, and
    /// propagates the listener's <see cref="AcceleratorKeyEventArgs.Handled"/> back into
    /// <see cref="ICoreWebView2AcceleratorKeyPressedEventArgs.set_Handled"/> so the hosted page
    /// does not also see the key.
    /// </summary>
    [SupportedOSPlatform("windows")]
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    internal sealed class AcceleratorKeyPressedHandler : ICoreWebView2AcceleratorKeyPressedEventHandler
    {
        private readonly Action<AcceleratorKeyEventArgs> listener;

        public AcceleratorKeyPressedHandler(Action<AcceleratorKeyEventArgs> listener)
        {
            this.listener = listener;
        }

        [PreserveSig]
        public int Invoke(nint sender, nint argsPtr)
        {
            try
            {
                if (argsPtr != nint.Zero)
                {
                    var args = (ICoreWebView2AcceleratorKeyPressedEventArgs)
                        Marshal.GetObjectForIUnknown(argsPtr);
                    args.get_KeyEventKind(out var kind);
                    args.get_VirtualKey(out var vk);

                    var accelArgs = BuildArgs(kind, (int)vk, IsKeyDown);
                    this.listener(accelArgs);

                    if (accelArgs.Handled)
                    {
                        args.set_Handled(1);
                    }
                }
            }
            catch (Exception ex)
            {
                // Never let an exception cross the COM boundary — that would crash WebView2. Log
                // and swallow at this narrow boundary only.
                Trace.TraceWarning(
                    "WebView2AcceleratorInterop.Handler.Invoke: listener threw: {0}", ex);
            }

            return 0; // S_OK
        }
    }
}
