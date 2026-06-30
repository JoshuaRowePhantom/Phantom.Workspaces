using System.Collections.Generic;
using System.ComponentModel;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Services;

namespace Phantom.Workspaces.Tests;

public sealed class ScrollLockLedServiceTests
{
    // ── Constructor ────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_WithActiveAgentAutoScrollEnabled_SetsLedTrue()
    {
        var agent = new FakeAutoScrollViewModel { AutoScrollEnabled = true };
        var host = new FakeScrollLockLedHost(agent);
        var ledCalls = new List<bool>();

        using var _ = new ScrollLockLedService(host, initialLedState: false, ledCalls.Add);

        Assert.Equal([true], ledCalls);
    }

    [Fact]
    public void Constructor_WithActiveAgentAutoScrollDisabled_SetsLedFalse()
    {
        var agent = new FakeAutoScrollViewModel { AutoScrollEnabled = false };
        var host = new FakeScrollLockLedHost(agent);
        var ledCalls = new List<bool>();

        using var _ = new ScrollLockLedService(host, initialLedState: false, ledCalls.Add);

        Assert.Equal([false], ledCalls);
    }

    [Fact]
    public void Constructor_WithNoActiveAgent_SetsLedFalse()
    {
        var host = new FakeScrollLockLedHost(activeAgent: null);
        var ledCalls = new List<bool>();

        using var _ = new ScrollLockLedService(host, initialLedState: false, ledCalls.Add);

        Assert.Equal([false], ledCalls);
    }

    // ── Active agent switching ─────────────────────────────────────────────────

    [Fact]
    public void ActiveAgentViewModelChanged_ToAgentWithAutoScrollEnabled_SetsLedTrue()
    {
        var host = new FakeScrollLockLedHost(activeAgent: null);
        var ledCalls = new List<bool>();
        using var _ = new ScrollLockLedService(host, initialLedState: false, ledCalls.Add);
        ledCalls.Clear();

        var agent = new FakeAutoScrollViewModel { AutoScrollEnabled = true };
        host.ActiveAgent = agent;

        Assert.Equal([true], ledCalls);
    }

    [Fact]
    public void ActiveAgentViewModelChanged_ToNull_SetsLedFalse()
    {
        var agent = new FakeAutoScrollViewModel { AutoScrollEnabled = true };
        var host = new FakeScrollLockLedHost(agent);
        var ledCalls = new List<bool>();
        using var _ = new ScrollLockLedService(host, initialLedState: false, ledCalls.Add);
        ledCalls.Clear();

        host.ActiveAgent = null;

        Assert.Equal([false], ledCalls);
    }

    // ── AutoScrollEnabled changes on active agent ─────────────────────────────

    [Fact]
    public void AutoScrollEnabled_Changed_UpdatesLed()
    {
        var agent = new FakeAutoScrollViewModel { AutoScrollEnabled = true };
        var host = new FakeScrollLockLedHost(agent);
        var ledCalls = new List<bool>();
        using var _ = new ScrollLockLedService(host, initialLedState: false, ledCalls.Add);
        ledCalls.Clear();

        agent.AutoScrollEnabled = false;

        Assert.Equal([false], ledCalls);
    }

    [Fact]
    public void AutoScrollEnabled_ToggledTwice_UpdatesLedTwice()
    {
        var agent = new FakeAutoScrollViewModel { AutoScrollEnabled = true };
        var host = new FakeScrollLockLedHost(agent);
        var ledCalls = new List<bool>();
        using var _ = new ScrollLockLedService(host, initialLedState: false, ledCalls.Add);
        ledCalls.Clear();

        agent.AutoScrollEnabled = false;
        agent.AutoScrollEnabled = true;

        Assert.Equal([false, true], ledCalls);
    }

    // ── Unsubscribe from previous agent ───────────────────────────────────────

    [Fact]
    public void PreviousAgent_PropertyChanged_NoLongerAffectsLed()
    {
        var oldAgent = new FakeAutoScrollViewModel { AutoScrollEnabled = true };
        var host = new FakeScrollLockLedHost(oldAgent);
        var ledCalls = new List<bool>();
        using var _ = new ScrollLockLedService(host, initialLedState: false, ledCalls.Add);

        var newAgent = new FakeAutoScrollViewModel { AutoScrollEnabled = false };
        host.ActiveAgent = newAgent;
        ledCalls.Clear();

        oldAgent.AutoScrollEnabled = false; // change old agent — should be ignored

        Assert.Empty(ledCalls);
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_RestoresOriginalLedState()
    {
        var agent = new FakeAutoScrollViewModel { AutoScrollEnabled = true };
        var host = new FakeScrollLockLedHost(agent);
        var ledCalls = new List<bool>();
        var service = new ScrollLockLedService(host, initialLedState: false, ledCalls.Add);
        ledCalls.Clear();

        service.Dispose();

        Assert.Equal([false], ledCalls); // original was false
    }

    [Fact]
    public void Dispose_WithOriginalLedTrue_RestoresTrue()
    {
        var agent = new FakeAutoScrollViewModel { AutoScrollEnabled = false };
        var host = new FakeScrollLockLedHost(agent);
        var ledCalls = new List<bool>();
        var service = new ScrollLockLedService(host, initialLedState: true, ledCalls.Add);
        ledCalls.Clear();

        service.Dispose();

        Assert.Equal([true], ledCalls);
    }

    [Fact]
    public void Dispose_AgentPropertyChangesAfterDispose_NotForwarded()
    {
        var agent = new FakeAutoScrollViewModel { AutoScrollEnabled = true };
        var host = new FakeScrollLockLedHost(agent);
        var ledCalls = new List<bool>();
        var service = new ScrollLockLedService(host, initialLedState: false, ledCalls.Add);
        service.Dispose();
        ledCalls.Clear();

        agent.AutoScrollEnabled = false;

        Assert.Empty(ledCalls);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class FakeAutoScrollViewModel : IAutoScrollViewModel
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private bool autoScrollEnabled;

        public bool AutoScrollEnabled
        {
            get => this.autoScrollEnabled;
            set
            {
                if (this.autoScrollEnabled == value) return;
                this.autoScrollEnabled = value;
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.AutoScrollEnabled)));
            }
        }
    }

    private sealed class FakeScrollLockLedHost : IScrollLockLedHost
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private FakeAutoScrollViewModel? activeAgent;

        public FakeScrollLockLedHost(FakeAutoScrollViewModel? activeAgent)
        {
            this.activeAgent = activeAgent;
        }

        public FakeAutoScrollViewModel? ActiveAgent
        {
            get => this.activeAgent;
            set
            {
                this.activeAgent = value;
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IScrollLockLedHost.ActiveAgentViewModel)));
            }
        }

        IAutoScrollViewModel? IScrollLockLedHost.ActiveAgentViewModel => this.activeAgent;
    }
}
