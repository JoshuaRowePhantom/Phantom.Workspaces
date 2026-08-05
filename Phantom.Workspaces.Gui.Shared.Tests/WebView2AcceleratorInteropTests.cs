using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Platform;
using Phantom.Workspaces.Gui.Shared.Controls;

namespace Phantom.Workspaces.Gui.Shared.Tests;

/// <summary>
/// Coverage for the transport half of the WebView accelerator pipeline. Exercises
/// <see cref="WebView2AcceleratorInterop.Subscribe(IPlatformHandle?, Action{AcceleratorKeyEventArgs})"/>
/// against a stub <see cref="IWindowsWebView2PlatformHandle"/> that hands back a COM pointer to
/// an in-process fake <see cref="Controls.ICoreWebView2Controller"/>, so we can assert the
/// SDK-typed <c>add_AcceleratorKeyPressed</c> / <c>remove_AcceleratorKeyPressed</c> path is
/// actually taken (issue #1208). Windows-only because the interop marshals COM pointers.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WebView2AcceleratorInteropTests
{
    [Fact]
    public void Interop_WhenPlatformHandleExposesController_SubscribesViaSdkRawInterface()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeController();
        var handle = new FakeHandle(fake.ComPointer);

        var subscription = WebView2AcceleratorInterop.Subscribe(handle, _ => { });

        try
        {
            Assert.NotNull(subscription);
            Assert.Equal(1, fake.AddCallCount);
            Assert.NotNull(fake.LastHandler);
        }
        finally
        {
            subscription?.Dispose();
            fake.Release();
        }
    }

    [Fact]
    public void Interop_WhenPlatformHandleMissing_ReturnsNullAndTraces()
    {
        if (!OperatingSystem.IsWindows()) return;

        var listener = new TraceListenerBuffer();
        Trace.Listeners.Add(listener);
        try
        {
            var subscription = WebView2AcceleratorInterop.Subscribe(handle: null, _ => { });
            Assert.Null(subscription);
            Assert.Contains(
                "not IWindowsWebView2PlatformHandle",
                listener.Text,
                StringComparison.Ordinal);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public void Interop_HandlerReleasesComReferencesOnDispose()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeController();
        var handle = new FakeHandle(fake.ComPointer);

        var subscription = WebView2AcceleratorInterop.Subscribe(handle, _ => { });
        Assert.NotNull(subscription);
        Assert.Equal(0, fake.RemoveCallCount);

        subscription!.Dispose();

        Assert.Equal(1, fake.RemoveCallCount);
        Assert.Equal(fake.LastAddToken, fake.LastRemoveToken);
        fake.Release();
    }

    // ---------------------------------------------------------------------------------------------
    // Test doubles
    // ---------------------------------------------------------------------------------------------

    private sealed class FakeHandle : IWindowsWebView2PlatformHandle
    {
        public FakeHandle(nint controllerPtr) { this.CoreWebView2Controller = controllerPtr; }
        public nint CoreWebView2 => nint.Zero;
        public nint CoreWebView2Controller { get; }
        public nint Handle => nint.Zero;
        public string HandleDescriptor => "WebView2ControllerForTest";
    }

    /// <summary>
    /// Managed CCW that stands in for a real <c>ICoreWebView2Controller</c>. We only need to
    /// answer <c>add_AcceleratorKeyPressed</c> and <c>remove_AcceleratorKeyPressed</c>; every
    /// earlier vtable slot returns S_OK so the layout matches the real interface.
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class FakeController : ICoreWebView2Controller
    {
        private nint pointer;

        public FakeController()
        {
            this.pointer = Marshal.GetIUnknownForObject(this);
        }

        public nint ComPointer => this.pointer;
        public int AddCallCount { get; private set; }
        public int RemoveCallCount { get; private set; }
        public long LastAddToken { get; private set; }
        public long LastRemoveToken { get; private set; }
        public ICoreWebView2AcceleratorKeyPressedEventHandler? LastHandler { get; private set; }

        public void Release()
        {
            if (this.pointer != nint.Zero)
            {
                Marshal.Release(this.pointer);
                this.pointer = nint.Zero;
            }
        }

        // Placeholder implementations for the vtable slots we never call.
        public int get_IsVisible(out int isVisible) { isVisible = 0; return 0; }
        public int set_IsVisible(int isVisible) => 0;
        public int get_Bounds(out RECT bounds) { bounds = default; return 0; }
        public int set_Bounds(RECT bounds) => 0;
        public int get_ZoomFactor(out double z) { z = 1.0; return 0; }
        public int set_ZoomFactor(double z) => 0;
        public int add_ZoomFactorChanged(nint h, out long t) { t = 0; return 0; }
        public int remove_ZoomFactorChanged(long t) => 0;
        public int SetBoundsAndZoomFactor(RECT b, double z) => 0;
        public int MoveFocus(int reason) => 0;
        public int add_MoveFocusRequested(nint h, out long t) { t = 0; return 0; }
        public int remove_MoveFocusRequested(long t) => 0;
        public int add_GotFocus(nint h, out long t) { t = 0; return 0; }
        public int remove_GotFocus(long t) => 0;
        public int add_LostFocus(nint h, out long t) { t = 0; return 0; }
        public int remove_LostFocus(long t) => 0;

        public int add_AcceleratorKeyPressed(
            ICoreWebView2AcceleratorKeyPressedEventHandler eventHandler,
            out long token)
        {
            this.AddCallCount++;
            this.LastHandler = eventHandler;
            token = 0x1234_5678L + this.AddCallCount;
            this.LastAddToken = token;
            return 0;
        }

        public int remove_AcceleratorKeyPressed(long token)
        {
            this.RemoveCallCount++;
            this.LastRemoveToken = token;
            return 0;
        }
    }

    private sealed class TraceListenerBuffer : TraceListener
    {
        private readonly System.Text.StringBuilder buffer = new();
        public string Text => this.buffer.ToString();
        public override void Write(string? message) => this.buffer.Append(message);
        public override void WriteLine(string? message) => this.buffer.AppendLine(message);
    }
}
