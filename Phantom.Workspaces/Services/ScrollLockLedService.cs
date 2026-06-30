using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Phantom.Workspaces.Agent.Gui.ViewModels;

namespace Phantom.Workspaces.Services;

public sealed class ScrollLockLedService : IDisposable
{
    private readonly IScrollLockLedHost host;
    private readonly bool originalLedState;
    private readonly Action<bool> applyLedState;
    private IAutoScrollViewModel? subscribedAgent;

    public ScrollLockLedService(ViewModels.MainWindowViewModel viewModel)
        : this(viewModel, initialLedState: GetWindowsScrollLockState(), applyLedState: ApplyWindowsScrollLockState)
    {
    }

    internal ScrollLockLedService(IScrollLockLedHost host, bool initialLedState, Action<bool> applyLedState)
    {
        this.host = host;
        this.originalLedState = initialLedState;
        this.applyLedState = applyLedState;
        host.PropertyChanged += this.OnHostPropertyChanged;
        this.AttachToAgent(host.ActiveAgentViewModel);
    }

    private void OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IScrollLockLedHost.ActiveAgentViewModel))
        {
            this.AttachToAgent(this.host.ActiveAgentViewModel);
        }
    }

    private void AttachToAgent(IAutoScrollViewModel? agent)
    {
        if (this.subscribedAgent is not null)
        {
            this.subscribedAgent.PropertyChanged -= this.OnAgentPropertyChanged;
        }

        this.subscribedAgent = agent;

        if (agent is not null)
        {
            agent.PropertyChanged += this.OnAgentPropertyChanged;
        }

        this.applyLedState(agent?.AutoScrollEnabled ?? false);
    }

    private void OnAgentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IAutoScrollViewModel.AutoScrollEnabled))
        {
            this.applyLedState(this.subscribedAgent?.AutoScrollEnabled ?? false);
        }
    }

    public void Dispose()
    {
        this.host.PropertyChanged -= this.OnHostPropertyChanged;

        if (this.subscribedAgent is not null)
        {
            this.subscribedAgent.PropertyChanged -= this.OnAgentPropertyChanged;
            this.subscribedAgent = null;
        }

        this.applyLedState(this.originalLedState);
    }

    private static bool GetWindowsScrollLockState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return (GetKeyState(VK_SCROLL) & 1) != 0;
    }

    private static void ApplyWindowsScrollLockState(bool on)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        bool currentState = (GetKeyState(VK_SCROLL) & 1) != 0;
        if (currentState == on)
        {
            return;
        }

        keybd_event(VK_SCROLL, 0x45, 0, 0);
        keybd_event(VK_SCROLL, 0x45, KEYEVENTF_KEYUP, 0);
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

    private const byte VK_SCROLL = 0x91;
    private const uint KEYEVENTF_KEYUP = 0x0002;
}
