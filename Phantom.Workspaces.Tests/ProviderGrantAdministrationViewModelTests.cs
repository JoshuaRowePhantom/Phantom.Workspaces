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
        public CredentialAdministrationFailure Failure { get; set; } = CredentialAdministrationFailure.Denied;
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
            if (this.Fail) { throw new CredentialAdministrationException(this.Failure); }
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

    [AvaloniaTheory(Timeout = 15_000)]
    [InlineData(CredentialAdministrationFailure.Denied, "Access revocation was denied. Provider access and cached credentials are unchanged.")]
    [InlineData(CredentialAdministrationFailure.Failed, "Access revocation did not complete. Refresh and try again.")]
    public async Task ProviderGrantAdministration_RevocationFails_RendersAccurateStatusAndKeepsActionableRow(
        CredentialAdministrationFailure failure, string expected)
    {
        var administration = new Administration { Fail = true, Failure = failure };
        await using var model = new ProviderGrantAdministrationViewModel { Administration = administration };
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        var control = new ProviderGrantAdministrationControl { DataContext = model };
        var window = new Window { Content = control };
        try
        {
            window.Show();
            window.UpdateLayout();
            var descriptor = Assert.Single(model.Items);
            await model.RemoveCommand.ExecuteAsync(descriptor);
            window.UpdateLayout();
            Assert.Null(administration.Revoked);
            Assert.Same(descriptor, Assert.Single(model.Items));
            Assert.Equal(failure, model.LastFailure);
            Assert.Contains(control.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == expected);
            var action = Assert.Single(control.GetVisualDescendants().OfType<Button>(),
                button => button.Content?.ToString() == "Revoke access");
            Assert.True(action.IsEffectivelyEnabled);
            Assert.True(action.Command!.CanExecute(action.CommandParameter));
            administration.Fail = false;
            await model.RemoveCommand.ExecuteAsync(action.CommandParameter);
            window.UpdateLayout();
            Assert.Equal(descriptor.Identifier, administration.Revoked);
            Assert.Empty(model.Items);
            Assert.Null(model.LastFailure);
            Assert.Contains(control.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Operation completed.");
        }
        finally { window.Close(); }
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
            button.Content?.ToString() == "Revoke access" && button.Command?.CanExecute(button.CommandParameter) == true);
        Assert.Contains(control.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Provider access");
        Assert.Contains(control.GetVisualDescendants().OfType<TextBlock>(), block =>
            block.Text == "Revoking contacts the provider and clears affected cached credentials only on success. Saved approvals stay unchanged.");
        window.Close();
        await viewModel.DisposeAsync();
    }
}
