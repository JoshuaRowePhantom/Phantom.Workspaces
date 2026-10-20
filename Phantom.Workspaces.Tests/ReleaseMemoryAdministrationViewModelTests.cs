using System.IO;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phantom.Workspaces.Controls;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class ReleaseMemoryAdministrationViewModelTests
{
    private sealed class Administration : IReleaseMemoryAdministration
    {
        public List<ReleaseMemoryDescriptor> Items { get; } = [new()
        {
            Identifier = new() { Value = Guid.NewGuid() },
            LogicalIdentifier = new() { Digest = new string('a', 64) },
            SourceIdentifier = new() { Digest = new string('b', 64) },
            Scope = ReleaseMemoryScopeKind.AllUses,
            SafeDisplayName = "All uses",
        }];
        public ReleaseMemoryIdentifier? Forgotten { get; private set; }
        public ValueTask<IReadOnlyList<ReleaseMemoryDescriptor>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<ReleaseMemoryDescriptor>>(this.Items.ToArray());
        public ValueTask ForgetAsync(ReleaseMemoryIdentifier identifier, CancellationToken cancellationToken = default)
        {
            this.Forgotten = identifier;
            this.Items.RemoveAll(item => item.Identifier == identifier);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ListsAndForgetsOnlySelectedPolicyAndRejectsStaleSelection()
    {
        var administration = new Administration();
        var viewModel = new ReleaseMemoryAdministrationViewModel { Administration = administration };
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        var item = Assert.Single(viewModel.Items);
        await viewModel.RemoveAsync(item, TestContext.Current.CancellationToken);
        Assert.Equal(item.Identifier, administration.Forgotten);
        Assert.Empty(viewModel.Items);
        Assert.False(viewModel.RemoveCommand.CanExecute(item));
        await viewModel.DisposeAsync();
        Assert.False(viewModel.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task RealPolicyAdapterForgetsOnlyTargetedMemory()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, ".access-ui-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var policy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(Path.Combine(directory, "memory.json")));
            var request = Access.ReleaseAuthorizationContractTests.Request();
            request = request with { Credential = request.Credential with { Provider = CredentialProviderKind.StoredSecret } };
            var authorization = await policy.CommitAsync(
                new() { Request = request, Scope = ReleaseMemoryScopeKind.AllUses },
                TestContext.Current.CancellationToken);
            var viewModel = new ReleaseMemoryAdministrationViewModel
            {
                Administration = new ReleaseMemoryAdministration { Policy = policy },
            };
            await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
            await viewModel.RemoveAsync(Assert.Single(viewModel.Items), TestContext.Current.CancellationToken);
            Assert.Empty(await policy.ListAsync(TestContext.Current.CancellationToken));
            Assert.IsType<ReleasePolicyRequired>(await policy.EvaluateAsync(request, TestContext.Current.CancellationToken));
            Assert.Equal(ReleaseAuthorizationValidation.AuthorizationRequired,
                await policy.ValidateAsync(authorization.Identifier, request, TestContext.Current.CancellationToken));
            await viewModel.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
        }
    }

    [AvaloniaFact]
    public async Task ControlBindsOnlySafeDescriptorAndForgetCommand()
    {
        var viewModel = new ReleaseMemoryAdministrationViewModel { Administration = new Administration() };
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        var control = new ReleaseMemoryAdministrationControl { DataContext = viewModel };
        var window = new Window { Content = control };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(control.GetVisualDescendants().OfType<Button>(), button =>
            button.Content?.ToString() == "Forget" && button.Command?.CanExecute(button.CommandParameter) == true);
        window.Close();
        await viewModel.DisposeAsync();
    }
}
