using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.ViewModels.Configuration;

using Phantom.Workspaces.Testing.Gui;

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

    [AvaloniaFact]
    public void RemoteAccessSettingsViewModel_WildcardStarListenUrl_WhenHostingEnabled_IsValid()
    {
        var viewModel = new RemoteAccessSettingsViewModel
        {
            HostingEnabled = true,
            ListenUrl = "http://*:5280",
        };

        Assert.True(viewModel.IsValid);
        Assert.Null(viewModel.ValidationMessage);
    }

    [AvaloniaFact]
    public void RemoteAccessSettingsViewModel_WildcardPlusListenUrl_WhenHostingEnabled_IsValid()
    {
        var viewModel = new RemoteAccessSettingsViewModel
        {
            HostingEnabled = true,
            ListenUrl = "http://+:5280",
        };

        Assert.True(viewModel.IsValid);
        Assert.Null(viewModel.ValidationMessage);
    }

    [AvaloniaFact]
    public void RemoteAccessSettingsViewModel_AbsoluteListenUrl_WhenHostingEnabled_RemainsValid()
    {
        foreach (var listenUrl in new[] { "http://[::]:5280", "http://0.0.0.0:5280", "http://localhost:5280" })
        {
            var viewModel = new RemoteAccessSettingsViewModel
            {
                HostingEnabled = true,
                ListenUrl = listenUrl,
            };

            Assert.True(viewModel.IsValid, $"Expected {listenUrl} to be valid.");
            Assert.Null(viewModel.ValidationMessage);
        }
    }

    [AvaloniaFact]
    public void RemoteAccessSettingsViewModel_MalformedListenUrl_WhenHostingEnabled_IsInvalid()
    {
        foreach (var listenUrl in new[] { "not a url", "*", "http://*:99999999" })
        {
            var viewModel = new RemoteAccessSettingsViewModel
            {
                HostingEnabled = true,
                ListenUrl = listenUrl,
            };

            Assert.False(viewModel.IsValid, $"Expected {listenUrl} to be invalid.");
            Assert.Equal(
                "Listen URL must be a valid absolute URL, or a wildcard binding such as http://*:5280 or http://+:5280, when hosting is enabled.",
                viewModel.ValidationMessage);
        }
    }

    [AvaloniaFact]
    public void RemoteAccessSettingsViewModel_WildcardListenUrl_WhenHostingDisabled_IsValid()
    {
        var viewModel = new RemoteAccessSettingsViewModel
        {
            HostingEnabled = false,
            ListenUrl = "not a url",
        };

        Assert.True(viewModel.IsValid);
        Assert.Null(viewModel.ValidationMessage);
    }

    [AvaloniaFact]
    public void RemoteAccessSettingsViewModel_WildcardListenUrl_RoundTripsThroughToRemoteHostingSettings()
    {
        var viewModel = new RemoteAccessSettingsViewModel
        {
            HostingEnabled = true,
            ListenUrl = "http://*:5280",
        };

        var settings = viewModel.ToRemoteHostingSettings();

        Assert.Equal("http://*:5280", settings.ListenUrl);
    }
}
