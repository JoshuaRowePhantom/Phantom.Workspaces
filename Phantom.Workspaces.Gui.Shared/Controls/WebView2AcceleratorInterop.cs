using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Phantom.Workspaces.Gui.Shared.Controls;

internal static class CoreWebView2KeyEventKind
{
    public const int KeyDown = 0;
    public const int SystemKeyDown = 2;
    public const int SystemKeyUp   = 3;
}

// COM interface that our managed handler implements so WebView2 can call back.
// IID: B29C7E28-FA79-41A8-8E44-65811C76DCB2 (ICoreWebView2AcceleratorKeyPressedEventHandler)
[ComVisible(true)]
[Guid("B29C7E28-FA79-41A8-8E44-65811C76DCB2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICoreWebView2AcceleratorKeyPressedEventHandler
{
    [PreserveSig]
    int Invoke(nint sender, nint args);
}

// COM interface for reading accelerator key event args from WebView2.
// IID: 9F760F8A-FB79-42BE-9990-7B56900FA9C7 (ICoreWebView2AcceleratorKeyPressedEventArgs)
[ComImport]
[Guid("9F760F8A-FB79-42BE-9990-7B56900FA9C7")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICoreWebView2AcceleratorKeyPressedEventArgs
{
    [PreserveSig] int get_KeyEventKind(out int kind);
    [PreserveSig] int get_VirtualKey(out uint virtualKey);
    [PreserveSig] int get_KeyEventLParam(out int lParam);
    // COREWEBVIEW2_PHYSICAL_KEY_STATUS is a 6-field struct; we never read it but must
    // occupy the vtable slot so the subsequent methods map to the correct indices.
    [PreserveSig] int get_PhysicalKeyStatus(out long statusPlaceholder);
    [PreserveSig] int get_Handled(out int handled);
    [PreserveSig] int set_Handled(int handled);
}

internal static class WebView2AcceleratorInterop
{
    private const int VkMenu = 0x12; // VK_MENU (Alt)
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;
    private const int VkW = 0x57;
    private const int VkDigit0 = 0x30;
    private const int VkDigit9 = 0x39;

    /// <summary>
    /// Pure generic dispatch: translates the raw WebView2 key event to an
    /// <see cref="AcceleratorKeyEventArgs"/>, invokes <paramref name="listener"/>, and returns
    /// whether the listener marked the event as handled (so the caller can propagate that to the
    /// COM args). Called from the COM handler and directly in tests.
    /// </summary>
    internal static bool Dispatch(
        int kind,
        int vk,
        Action<AcceleratorKeyEventArgs>? listener,
        Func<int, bool>? isKeyDown = null)
    {
        if (listener is null)
        {
            return false;
        }

        isKeyDown ??= IsKeyDown;
        var (key, modifiers) = VirtualKeyMap.Map(kind, vk, isKeyDown);
        var args = new AcceleratorKeyEventArgs(kind, key, modifiers);
        listener(args);
        return args.Handled;
    }

    /// <summary>
    /// Legacy typed dispatch: maps a WebView2 key event to Alt/GoToTab/CloseTab/GoToWorkspacePane
    /// callbacks. Kept for back-compat with the existing bespoke event plumbing; new consumers
    /// should use the generic <see cref="AcceleratorKeyEventArgs"/> overload.
    /// </summary>
    internal static void Dispatch(
        int kind,
        int vk,
        Action<bool> onAltKeyState,
        Action<int> onGoToTab,
        Action? onCloseTab = null,
        Action<int>? onGoToWorkspacePane = null,
        Func<int, bool>? isKeyDown = null)
    {
        isKeyDown ??= IsKeyDown;

        if (vk == VkMenu)
        {
            if (kind == CoreWebView2KeyEventKind.SystemKeyDown)
                onAltKeyState(true);
            else if (kind == CoreWebView2KeyEventKind.SystemKeyUp)
                onAltKeyState(false);
            return;
        }

        if (kind == CoreWebView2KeyEventKind.SystemKeyDown && vk >= VkDigit0 && vk <= VkDigit9)
        {
            int index = vk == VkDigit0 ? 9 : vk - (VkDigit0 + 1);
            if (isKeyDown(VkShift) && onGoToWorkspacePane is not null)
                onGoToWorkspacePane(index);
            else
                onGoToTab(index);
            return;
        }

        if (kind == CoreWebView2KeyEventKind.KeyDown && vk == VkW && isKeyDown(VkControl))
        {
            onCloseTab?.Invoke();
        }
    }

    /// <summary>
    /// Subscribes to AcceleratorKeyPressed on the WebView2 controller backing
    /// <paramref name="adapter"/> using reflection. Windows-only. Returns the COM handler
    /// object that the caller must keep alive for the lifetime of the subscription.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static AcceleratorKeyPressedHandler? Subscribe(
        object? adapter,
        Action<bool> onAltKeyState,
        Action<int> onGoToTab,
        Action? onCloseTab = null,
        Action<int>? onGoToWorkspacePane = null,
        Action<AcceleratorKeyEventArgs>? onAcceleratorKeyPressed = null)
    {
        if (adapter is null)
            return null;

        try
        {
            // Walk up the type hierarchy to WebView2BaseAdapter, which holds the COM controller.
            var type = adapter.GetType();
            while (type is not null && type.Name != "WebView2BaseAdapter")
                type = type.BaseType;

            if (type is null)
                return null;

            // The primary constructor parameter `controller` is captured as a field named
            // "<controller>" by the C# 12 compiler.
            var controllerField =
                type.GetField("<controller>", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? type.GetField("controller",  BindingFlags.NonPublic | BindingFlags.Instance)
                ?? type.GetField("_controller", BindingFlags.NonPublic | BindingFlags.Instance);

            if (controllerField is null)
                return null;

            var controller = controllerField.GetValue(adapter);
            if (controller is null)
                return null;

            var addMethod = controller.GetType().GetMethod(
                "add_AcceleratorKeyPressed",
                BindingFlags.Public | BindingFlags.Instance);

            if (addMethod is null)
                return null;

            var handler = new AcceleratorKeyPressedHandler(onAltKeyState, onGoToTab, onCloseTab, onGoToWorkspacePane, onAcceleratorKeyPressed);
            var handlerPtr = Marshal.GetComInterfaceForObject(
                handler,
                typeof(ICoreWebView2AcceleratorKeyPressedEventHandler));
            try
            {
                // EventRegistrationToken is an opaque struct — create a default instance to
                // satisfy the out parameter; we do not need it for unsubscription within
                // the control's lifetime.
                var tokenType = addMethod.GetParameters()[1].ParameterType.GetElementType()!;
                var tokenInstance = Activator.CreateInstance(tokenType);
                addMethod.Invoke(controller, [handlerPtr, tokenInstance]);
            }
            finally
            {
                // Release our ref; the COM controller holds its own AddRef'd reference.
                Marshal.Release(handlerPtr);
            }

            return handler;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & unchecked((short)0x8000)) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int nVirtKey);
}

/// <summary>
/// COM-callable wrapper that WebView2 calls back on accelerator key events.
/// The caller must keep a reference to this object alive for the duration of the subscription
/// to prevent it from being garbage collected while the COM callback is registered.
/// </summary>
[SupportedOSPlatform("windows")]
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class AcceleratorKeyPressedHandler(
    Action<bool> onAltKeyState,
    Action<int> onGoToTab,
    Action? onCloseTab,
    Action<int>? onGoToWorkspacePane,
    Action<AcceleratorKeyEventArgs>? onAcceleratorKeyPressed)
    : ICoreWebView2AcceleratorKeyPressedEventHandler
{
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

                // Generic path: hand every accelerator key to the listener. If the listener
                // marks the event handled, propagate that to the COM args (so WebView2 stops
                // processing the key) and skip the legacy typed callbacks.
                var genericHandled = WebView2AcceleratorInterop.Dispatch(kind, (int)vk, onAcceleratorKeyPressed);
                if (genericHandled)
                {
                    args.set_Handled(1);
                    return 0;
                }

                // Legacy allowlist path (Alt state / Alt+digit / Ctrl+W) kept for back-compat
                // with the existing typed event plumbing on AcceleratorAwareWebView.
                if (ShouldHandle(kind, (int)vk))
                {
                    args.set_Handled(1);
                    WebView2AcceleratorInterop.Dispatch(kind, (int)vk, onAltKeyState, onGoToTab, onCloseTab, onGoToWorkspacePane);
                }
            }
        }
        catch (Exception)
        {
        }

        return 0; // S_OK
    }

    private static bool ShouldHandle(int kind, int vk)
    {
        if (kind == CoreWebView2KeyEventKind.SystemKeyDown || kind == CoreWebView2KeyEventKind.SystemKeyUp)
        {
            if (vk == 0x12)
                return true;
            if (kind == CoreWebView2KeyEventKind.SystemKeyDown && vk >= 0x30 && vk <= 0x39)
                return true;
        }

        if (kind == CoreWebView2KeyEventKind.KeyDown && vk == 0x57)
            return true;

        return false;
    }
}
