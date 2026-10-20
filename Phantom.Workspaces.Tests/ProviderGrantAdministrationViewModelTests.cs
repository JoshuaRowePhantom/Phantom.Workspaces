using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phantom.Workspaces.Controls;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class ProviderGrantAdministrationViewModelTests
{
    private sealed class Administration : IProviderGrantAdministration
    {
        public ProviderGrantIdentifier? Revoked { get; private set; }
        public bool Fail { get; set; }
        public ProviderGrantDescriptor Descriptor { get; } = new()
        {
            Identifier = new() { Value = Guid.NewGuid() },
            Provider = CredentialProviderKind.McpOAuth,
            Kind = CredentialKind.AccessToken,
            SourceIdentifier = new() { Digest = new string('a', 64) },
        };
        public ValueTask<IReadOnlyList<ProviderGrantDescriptor>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<ProviderGrantDescriptor>>(this.Revoked is null ? [this.Descriptor] : []);
        public ValueTask RevokeAsync(ProviderGrantIdentifier identifier, CancellationToken cancellationToken = default)
        {
            if (this.Fail) { throw new CredentialAdministrationException(CredentialAdministrationFailure.Denied); }
            this.Revoked = identifier;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task RevokesSelectedGrantWithoutPolicyOrCacheUiCoupling()
    {
        var administration = new Administration { Fail = true };
        var viewModel = new ProviderGrantAdministrationViewModel { Administration = administration };
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        var descriptor = Assert.Single(viewModel.Items);
        await viewModel.RemoveAsync(descriptor, TestContext.Current.CancellationToken);
        Assert.Null(administration.Revoked);
        Assert.Single(viewModel.Items);
        Assert.Equal(CredentialAdministrationFailure.Denied, viewModel.LastFailure);
        administration.Fail = false;
        await viewModel.RemoveAsync(descriptor, TestContext.Current.CancellationToken);
        Assert.Equal(descriptor.Identifier, administration.Revoked);
        Assert.Empty(viewModel.Items);
        Assert.Null(viewModel.LastFailure);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public void DescriptorIsSemanticMetadataOnly()
    {
        Assert.Equal(new[]
        {
            "Identifier", "Kind", "Provider", "SafeDisplayName", "SourceIdentifier",
        }, typeof(ProviderGrantDescriptor).GetProperties().Select(property => property.Name).Order());
        Assert.Null(typeof(ProviderGrantDescriptor).GetProperty("SafeDisplayName")!.SetMethod);
    }

    [AvaloniaFact]
    public async Task ControlBindsSafeGrantAndIndependentRevoke()
    {
        var viewModel = new ProviderGrantAdministrationViewModel { Administration = new Administration() };
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        var control = new ProviderGrantAdministrationControl { DataContext = viewModel };
        var window = new Window { Content = control };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(control.GetVisualDescendants().OfType<Button>(), button =>
            button.Content?.ToString() == "Revoke" && button.Command?.CanExecute(button.CommandParameter) == true);
        window.Close();
        await viewModel.DisposeAsync();
    }
}
