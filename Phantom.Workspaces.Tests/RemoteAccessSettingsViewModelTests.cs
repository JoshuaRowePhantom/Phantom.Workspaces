using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.ViewModels.Configuration;

namespace Phantom.Workspaces.Tests;

public sealed class RemoteAccessSettingsViewModelTests
{
    [AvaloniaFact]
    public void ToDevTunnelConfiguration_PreservesBaseFields_AndUpdatesEditable()
    {
        var existing = new DevTunnelConfiguration
        {
            TunnelId = "tunnel-123",
            TunnelName = "old-name",
            HostedPorts = [5280, 5281],
            Protocol = "https",
            AccessMode = DevTunnelAccessMode.Private,
            AccessTokenSource = null,
        };

        var viewModel = new RemoteAccessSettingsViewModel(new RemoteHostingSettings(), existing)
        {
            TunnelName = "new-name",
            DevTunnelAccessMode = DevTunnelAccessMode.Token,
            DevTunnelAccessTokenSource = "DEVTUNNEL_TOKEN",
        };

        var projected = viewModel.ToDevTunnelConfiguration(existing);

        // Preserved from the base configuration.
        Assert.Equal("tunnel-123", projected.TunnelId);
        Assert.Equal([5280, 5281], projected.HostedPorts);
        Assert.Equal("https", projected.Protocol);

        // Updated from the editable view-model state.
        Assert.Equal("new-name", projected.TunnelName);
        Assert.Equal(DevTunnelAccessMode.Token, projected.AccessMode);
        Assert.Equal("DEVTUNNEL_TOKEN", projected.AccessTokenSource);
    }

    [AvaloniaFact]
    public void ToRemoteHostingSettings_ProjectsHostingState()
    {
        var viewModel = new RemoteAccessSettingsViewModel
        {
            HostingEnabled = true,
            ListenUrl = "http://localhost:6001",
            AcceptReverseExecution = true,
        };

        var settings = viewModel.ToRemoteHostingSettings();

        Assert.True(settings.Enabled);
        Assert.Equal("http://localhost:6001", settings.ListenUrl);
        Assert.True(settings.AcceptReverseExecution);
    }

    [AvaloniaFact]
    public void AcceptReverseExecution_DefaultsOff_AndRoundTripsFromSettings()
    {
        Assert.False(new RemoteAccessSettingsViewModel().AcceptReverseExecution);

        var viewModel = new RemoteAccessSettingsViewModel(
            new RemoteHostingSettings { AcceptReverseExecution = true },
            new DevTunnelConfiguration());

        Assert.True(viewModel.AcceptReverseExecution);
    }

    [AvaloniaFact]
    public void IsAccessTokenSourceVisible_OnlyForTokenMode()
    {
        var viewModel = new RemoteAccessSettingsViewModel
        {
            DevTunnelAccessMode = DevTunnelAccessMode.Private,
        };
        Assert.False(viewModel.IsAccessTokenSourceVisible);

        viewModel.DevTunnelAccessMode = DevTunnelAccessMode.Token;
        Assert.True(viewModel.IsAccessTokenSourceVisible);

        viewModel.DevTunnelAccessMode = DevTunnelAccessMode.Anonymous;
        Assert.False(viewModel.IsAccessTokenSourceVisible);
    }

    [AvaloniaFact]
    public void UserComputerProfileOverride_RoundTripsFromConstructor()
    {
        var viewModel = new RemoteAccessSettingsViewModel(
            new RemoteHostingSettings(),
            new DevTunnelConfiguration(),
            userComputerProfileOverride: "second-instance");

        Assert.Equal("second-instance", viewModel.UserComputerProfileOverride);
    }
}
