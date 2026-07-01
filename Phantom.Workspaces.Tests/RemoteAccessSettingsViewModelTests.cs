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
            AccessMode = DevTunnelAccessMode.Private,
        };

        var viewModel = new RemoteAccessSettingsViewModel(new RemoteHostingSettings(), existing)
        {
            TunnelName = "new-name",
            DevTunnelAccessMode = DevTunnelAccessMode.Anonymous,
        };

        var projected = viewModel.ToDevTunnelConfiguration(existing);

        // Preserved from the base configuration.
        Assert.Equal("tunnel-123", projected.TunnelId);
        Assert.Equal([5280, 5281], projected.HostedPorts);

        // Updated from the editable view-model state.
        Assert.Equal("new-name", projected.TunnelName);
        Assert.Equal(DevTunnelAccessMode.Anonymous, projected.AccessMode);
    }

    [AvaloniaFact]
    public void LegacyTokenMode_MigratedToPrivate_InConstructor()
    {
        // A DevTunnelConfiguration loaded from an old config file may have AccessMode=Token (1).
        // The view model must convert this to Private so the UI shows the correct mode.
#pragma warning disable CS0618 // Token is obsolete
        var existing = new DevTunnelConfiguration { AccessMode = DevTunnelAccessMode.Token };
#pragma warning restore CS0618
        var viewModel = new RemoteAccessSettingsViewModel(new RemoteHostingSettings(), existing);

        Assert.Equal(DevTunnelAccessMode.Private, viewModel.DevTunnelAccessMode);
    }

    [AvaloniaFact]
    public void AvailableAccessModes_DoesNotIncludeTokenMode()
    {
        // Token mode is retired and must not be offered to new users.
        Assert.DoesNotContain(
#pragma warning disable CS0618
            DevTunnelAccessMode.Token,
#pragma warning restore CS0618
            RemoteAccessSettingsViewModel.AvailableAccessModes);
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
    public void UserComputerProfileOverride_RoundTripsFromConstructor()
    {
        var viewModel = new RemoteAccessSettingsViewModel(
            new RemoteHostingSettings(),
            new DevTunnelConfiguration(),
            userComputerProfileOverride: "second-instance");

        Assert.Equal("second-instance", viewModel.UserComputerProfileOverride);
    }
}
