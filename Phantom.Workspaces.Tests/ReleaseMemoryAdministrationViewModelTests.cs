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

    private sealed class ControlledAdministration : IReleaseMemoryAdministration
    {
        private readonly TaskCompletionSource continuation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public required IReleaseMemoryAdministration Inner { get; init; }
        public bool HoldList { get; set; }
        public TaskCompletionSource ListEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IReadOnlyList<ReleaseMemoryDescriptor>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            if (this.HoldList)
            {
                this.ListEntered.TrySetResult();
                await this.continuation.Task.WaitAsync(cancellationToken);
            }
            return await this.Inner.ListAsync(cancellationToken);
        }

        public ValueTask ForgetAsync(
            ReleaseMemoryIdentifier identifier,
            CancellationToken cancellationToken = default) =>
            this.Inner.ForgetAsync(identifier, cancellationToken);
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

    [Theory]
    [InlineData("{\"version\":1,\"records\":[unexpected-json-marker]}")]
    [InlineData("{\"version\":99,\"records\":[]}")]
    public async Task RealMalformedOrUnsupportedStoreUsesSafeStatusAndCommandCanCancelAndRetry(
        string document)
    {
        var directory = Path.Combine(Environment.CurrentDirectory, ".access-ui-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "memory.json");
            await File.WriteAllTextAsync(path, document, TestContext.Current.CancellationToken);
            await using var policy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(path));
            var administration = new ControlledAdministration
            {
                Inner = new ReleaseMemoryAdministration { Policy = policy },
            };
            await using var viewModel = new ReleaseMemoryAdministrationViewModel { Administration = administration };
            await viewModel.RefreshCommand.ExecuteAsync(null);
            Assert.Equal("Operation unavailable. Refresh and try again.", viewModel.SafeStatus);
            Assert.False(viewModel.IsBusy);
            Assert.True(viewModel.RefreshCommand.CanExecute(null));
            Assert.Empty(viewModel.Items);

            await File.WriteAllTextAsync(path, "{\"version\":1,\"records\":[]}", TestContext.Current.CancellationToken);
            administration.HoldList = true;
            var pending = viewModel.RefreshCommand.ExecuteAsync(null);
            await administration.ListEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.True(viewModel.RefreshCommand.CanBeCanceled);
            viewModel.RefreshCommand.Cancel();
            await pending;
            Assert.Equal("Operation cancelled.", viewModel.SafeStatus);
            Assert.False(viewModel.IsBusy);
            Assert.True(viewModel.RefreshCommand.CanExecute(null));

            administration.HoldList = false;
            await viewModel.RefreshCommand.ExecuteAsync(null);
            Assert.Equal("List updated.", viewModel.SafeStatus);
            Assert.Empty(viewModel.Items);
            Assert.True(viewModel.RefreshCommand.CanExecute(null));
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
