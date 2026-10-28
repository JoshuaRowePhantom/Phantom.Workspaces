using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phantom.Workspaces.Controls;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class CredentialCacheAdministrationViewModelTests
{
    private sealed class Administration : ICredentialCacheAdministration
    {
        public CredentialCacheIdentifier? Deleted { get; private set; }
        public bool Fail { get; set; }
        public bool UnexpectedFailure { get; set; }
        public Func<CancellationToken, ValueTask<IReadOnlyList<CredentialCacheDescriptor>>>? ListOperation { get; set; }
        public CredentialCacheDescriptor Descriptor { get; set; } = new()
        {
            Identifier = new() { Value = Guid.NewGuid() },
            Provider = CredentialProviderKind.Entra,
            Kind = CredentialKind.AccessToken,
            Generation = new() { Value = Guid.NewGuid() },
            SourceIdentifier = new() { Digest = new string('a', 64) },
        };
        public ValueTask<IReadOnlyList<CredentialCacheDescriptor>> ListAsync(CancellationToken cancellationToken = default) =>
            this.ListOperation?.Invoke(cancellationToken) ??
            ValueTask.FromResult<IReadOnlyList<CredentialCacheDescriptor>>(this.Deleted is null ? [this.Descriptor] : []);
        public ValueTask DeleteAsync(CredentialCacheIdentifier identifier, CancellationToken cancellationToken = default)
        {
            if (this.Fail) { throw new CredentialAdministrationException(CredentialAdministrationFailure.Failed); }
            if (this.UnexpectedFailure) { throw new InvalidOperationException("Unexpected implementation bug"); }
            this.Deleted = identifier;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task DeletesExactIdentifierWithoutOtherServicesAndSanitizesFailure()
    {
        var administration = new Administration { Fail = true };
        var viewModel = new CredentialCacheAdministrationViewModel { Administration = administration };
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        var item = Assert.Single(viewModel.Items);
        await viewModel.RemoveAsync(item, TestContext.Current.CancellationToken);
        Assert.Null(administration.Deleted);
        Assert.DoesNotContain("provider-token", viewModel.SafeStatus);
        Assert.Equal(CredentialAdministrationFailure.Failed, viewModel.LastFailure);
        Assert.True(viewModel.RemoveCommand.CanExecute(item));
        administration.Fail = false;
        await viewModel.RemoveAsync(item, TestContext.Current.CancellationToken);
        Assert.Equal(item.Identifier, administration.Deleted);
        Assert.Empty(viewModel.Items);
        Assert.Null(viewModel.LastFailure);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task StaleRowCannotDeleteNewGenerationAfterRefresh()
    {
        var administration = new Administration();
        var viewModel = new CredentialCacheAdministrationViewModel { Administration = administration };
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        var old = Assert.Single(viewModel.Items);
        administration.Descriptor = old with
        {
            Identifier = new() { Value = Guid.NewGuid() },
            Generation = new() { Value = Guid.NewGuid() },
        };
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        await viewModel.RemoveAsync(old, TestContext.Current.CancellationToken);
        Assert.Null(administration.Deleted);
        Assert.Equal(administration.Descriptor, Assert.Single(viewModel.Items));
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task DisposalRejectsLateListAndCancellationRestoresCommands()
    {
        var completion = new TaskCompletionSource<IReadOnlyList<CredentialCacheDescriptor>>();
        var administration = new Administration { ListOperation = _ => new(completion.Task) };
        var viewModel = new CredentialCacheAdministrationViewModel { Administration = administration };
        var refresh = viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.RefreshCommand.CanExecute(null));
        await viewModel.DisposeAsync();
        completion.SetResult([administration.Descriptor]);
        await refresh;
        Assert.Empty(viewModel.Items);
        Assert.False(viewModel.RefreshCommand.CanExecute(null));

        var cancellationViewModel = new CredentialCacheAdministrationViewModel { Administration = new Administration() };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await cancellationViewModel.RefreshAsync(cancellation.Token);
        Assert.False(cancellationViewModel.IsBusy);
        Assert.True(cancellationViewModel.RefreshCommand.CanExecute(null));
        Assert.Equal("Operation cancelled.", cancellationViewModel.SafeStatus);
        await cancellationViewModel.DisposeAsync();
    }

    [Fact]
    public void DescriptorHasNoArbitraryWritableDisplayStrings()
    {
        Assert.Equal(new[]
        {
            "ExpiresOn", "Generation", "Identifier", "Kind", "Provider", "SafeDisplayName", "SourceIdentifier",
        }, typeof(CredentialCacheDescriptor).GetProperties().Select(property => property.Name).Order());
        Assert.Null(typeof(CredentialCacheDescriptor).GetProperty("SafeDisplayName")!.SetMethod);
    }

    [Fact]
    public async Task UnexpectedImplementationBugsAreNotSwallowedAsAdministrationFailures()
    {
        var administration = new Administration { UnexpectedFailure = true };
        var viewModel = new CredentialCacheAdministrationViewModel { Administration = administration };
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        var descriptor = Assert.Single(viewModel.Items);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            viewModel.RemoveAsync(descriptor, TestContext.Current.CancellationToken));
        Assert.False(viewModel.IsBusy);
        Assert.DoesNotContain("Unexpected implementation bug", viewModel.SafeStatus);
        Assert.NotEqual("Operation completed.", viewModel.SafeStatus);
        await viewModel.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task ControlBindsSafeMetadataAndTargetedDelete()
    {
        var viewModel = new CredentialCacheAdministrationViewModel { Administration = new Administration() };
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        var control = new CredentialCacheAdministrationControl { DataContext = viewModel };
        var window = new Window { Content = control };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(control.GetVisualDescendants().OfType<Button>(), button =>
            button.Content?.ToString() == "Delete cached credential" && button.Command?.CanExecute(button.CommandParameter) == true);
        Assert.Contains(control.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Cached credentials");
        Assert.Contains(control.GetVisualDescendants().OfType<TextBlock>(), block =>
            block.Text == "Deleting removes only the cached credential. Saved approvals and provider access stay unchanged.");
        window.Close();
        await viewModel.DisposeAsync();
    }
}
