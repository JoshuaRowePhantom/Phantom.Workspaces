using Avalonia.Headless.XUnit;
using Phantom.Workspaces;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.ViewModels.Configuration;

namespace Phantom.Workspaces.Tests;

public sealed class DevTunnelWebSettingsViewModelTests
{
    [AvaloniaFact]
    public void DevTunnelWebSettingsViewModel_ToProfile_ProjectsSelectedRemoteAuthentication()
    {
        var profile = new DataAccessConnectionProfile
        {
            Mode = DataAccessMode.DevTunnelWeb,
            Authentication = new RemoteAuthentication(RemoteAuthentication.OAuthScheme),
        };
        var viewModel = new DevTunnelWebSettingsViewModel(profile);

        viewModel.AuthScheme.OAuth.ClientId = "client-1";
        viewModel.AuthScheme.OAuth.AuthorizationEndpoint = "https://example.com/authorize";
        viewModel.AuthScheme.OAuth.TokenEndpoint = "https://example.com/token";

        var projected = viewModel.ToProfile();

        Assert.NotNull(projected.Authentication);
        Assert.Equal(RemoteAuthentication.OAuthScheme, projected.Authentication!.Scheme);
        Assert.Equal("client-1", projected.Authentication.ClientId);
        Assert.Equal("https://example.com/authorize", projected.Authentication.AuthorizationEndpoint);
        Assert.Equal("https://example.com/token", projected.Authentication.TokenEndpoint);
    }

    [AvaloniaFact]
    public void AuthSchemeSelector_SwitchingScheme_SwapsSchemeSpecificViewModel()
    {
        var viewModel = new DevTunnelWebSettingsViewModel(
            new DataAccessConnectionProfile { Mode = DataAccessMode.DevTunnelWeb });

        // Default legacy config selects github: valid, no required fields.
        Assert.IsType<GitHubAuthSchemeViewModel>(viewModel.AuthScheme.ActiveScheme);
        Assert.True(viewModel.IsValid);

        // Switching to entra swaps the active sub-view-model and makes the section invalid until
        // the required fields are supplied, which must disable Save.
        viewModel.AuthScheme.Scheme = RemoteAuthentication.EntraScheme;
        Assert.IsType<EntraAuthSchemeViewModel>(viewModel.AuthScheme.ActiveScheme);
        Assert.False(viewModel.IsValid);
        Assert.NotNull(viewModel.ValidationMessage);

        viewModel.AuthScheme.Entra.ClientId = "client-1";
        viewModel.AuthScheme.Entra.Authority = "https://login.microsoftonline.com/tenant";
        Assert.True(viewModel.IsValid);
        Assert.Null(viewModel.ValidationMessage);
    }
}
