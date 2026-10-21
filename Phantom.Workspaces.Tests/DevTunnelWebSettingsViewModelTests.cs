using Avalonia.Headless.XUnit;
using Phantom.Workspaces;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Llm.Interfaces.Access;
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
        viewModel.AuthScheme.OAuth.Authority = "https://provider.example/";
        viewModel.AuthScheme.OAuth.AccountEndpoint = "https://accounts.example/principal";
        viewModel.AuthScheme.OAuth.AccountIdentifierClaim = OAuthAccountIdentifierClaim.Identifier;
        viewModel.AuthScheme.OAuth.Audience = "configured-resource";

        var projected = viewModel.ToProfile();

        Assert.NotNull(projected.Authentication);
        Assert.Equal(RemoteAuthentication.OAuthScheme, projected.Authentication!.Scheme);
        Assert.Equal("client-1", projected.Authentication.ClientId);
        Assert.Equal("https://example.com/authorize", projected.Authentication.AuthorizationEndpoint);
        Assert.Equal("https://example.com/token", projected.Authentication.TokenEndpoint);
        Assert.Equal("https://provider.example/", projected.Authentication.Authority);
        Assert.Equal("https://accounts.example/principal", projected.Authentication.AccountEndpoint);
        Assert.Equal(OAuthAccountIdentifierClaim.Identifier, projected.Authentication.AccountIdentifierClaim);
        Assert.Equal("configured-resource", projected.Authentication.Audience);
        Assert.True(viewModel.IsValid);
    }

    [AvaloniaFact]
    public void OAuthSettings_ExistingExplicitMetadata_IsPreservedByEditing()
    {
        var authentication = CreateExplicitOAuthAuthentication();
        var viewModel = new OAuthAuthSchemeViewModel(authentication);

        Assert.True(viewModel.IsValid);
        Assert.Equal(authentication, viewModel.ToRemoteAuthentication());
    }

    [AvaloniaFact]
    public void OAuthSettings_LegacyConfiguration_CannotSaveWithoutExplicitAccountMetadata()
    {
        var viewModel = new OAuthAuthSchemeViewModel(CreateExplicitOAuthAuthentication() with
        {
            AccountEndpoint = null,
            AccountIdentifierClaim = null,
        });

        Assert.False(viewModel.IsValid);
        Assert.Contains("AccountEndpoint", viewModel.ValidationMessage);
        viewModel.AccountEndpoint = "https://accounts.example/principal";
        Assert.False(viewModel.IsValid);
        Assert.Contains("account claim", viewModel.ValidationMessage);
        viewModel.AccountIdentifierClaim = OAuthAccountIdentifierClaim.Subject;
        Assert.True(viewModel.IsValid);
    }

    [AvaloniaTheory]
    [InlineData("http://provider.example/principal")]
    [InlineData("https://user:password@provider.example/principal")]
    [InlineData("https://provider.example/principal#fragment")]
    [InlineData("/principal")]
    public void OAuthSettings_RejectsUnsafeExplicitAccountEndpoints(string endpoint)
    {
        var viewModel = new OAuthAuthSchemeViewModel(CreateExplicitOAuthAuthentication() with
        {
            AccountEndpoint = endpoint,
        });

        Assert.False(viewModel.IsValid);
        Assert.Contains("AccountEndpoint", viewModel.ValidationMessage);
    }

    [AvaloniaFact]
    public void OAuthSettings_RequiresDefinedClaimAndExplicitAudience()
    {
        var viewModel = new OAuthAuthSchemeViewModel(CreateExplicitOAuthAuthentication());
        viewModel.AccountIdentifierClaim = (OAuthAccountIdentifierClaim)999;
        Assert.False(viewModel.IsValid);
        viewModel.AccountIdentifierClaim = OAuthAccountIdentifierClaim.Subject;
        viewModel.Audience = " ";
        Assert.False(viewModel.IsValid);
        Assert.Contains("Audience", viewModel.ValidationMessage);
    }

    private static RemoteAuthentication CreateExplicitOAuthAuthentication() => new(
        Scheme: RemoteAuthentication.OAuthScheme,
        Authority: "https://provider.example/",
        ClientId: "client-id",
        AuthorizationEndpoint: "https://provider.example/authorize",
        TokenEndpoint: "https://provider.example/token",
        AccountEndpoint: "https://accounts.example/principal",
        AccountIdentifierClaim: OAuthAccountIdentifierClaim.Subject,
        Audience: "configured-resource");

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
