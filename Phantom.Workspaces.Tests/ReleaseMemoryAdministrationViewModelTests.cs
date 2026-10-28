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
    [AvaloniaTheory(Timeout = 15_000)]
    [InlineData("""{"provider":"storedSecret","kind":"secret","source":"future-source","origin":"manifest"}""")]
    [InlineData("""{"provider":"storedSecret","kind":"secret","source":"secretStore"}""")]
    [InlineData("""{"provider":999,"kind":"secret","source":"secretStore","origin":"manifest"}""")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task ReleaseMemoryAdministration_MalformedOptionalContext_PreservesPolicyAndExactForget(string metadata)
    {
        var directory = Path.Combine(Environment.CurrentDirectory, ".access-ui-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "memory.json");
        var token = TestContext.Current.CancellationToken;
        var window = new Window();
        try
        {
            var request = Access.ReleaseAuthorizationContractTests.Request();
            request = request with { Credential = request.Credential with { Provider = CredentialProviderKind.StoredSecret } };
            await using (var original = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(path)))
            {
                await original.CommitAsync(new() { Request = request, Scope = ReleaseMemoryScopeKind.AllUses }, token);
            }
            var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, token))!;
            var identifier = json["records"]![0]!["identifier"]!.ToJsonString();
            json["records"]![0]!["safeContext"] = System.Text.Json.Nodes.JsonNode.Parse(metadata);
            await File.WriteAllTextAsync(path, json.ToJsonString(), token);
            await using var policy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(path));
            await using var model = new ReleaseMemoryAdministrationViewModel
            {
                Administration = new ReleaseMemoryAdministration { Policy = policy },
            };
            await model.RefreshAsync(token);
            var descriptor = Assert.Single(model.Items);
            Assert.Null(descriptor.SafeContext);
            Assert.Equal(identifier, System.Text.Json.JsonSerializer.SerializeToNode(descriptor.Identifier,
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase })!.ToJsonString());
            Assert.IsType<ReleasePolicyMatched>(await policy.EvaluateAsync(request, token));
            var control = new ReleaseMemoryAdministrationControl { DataContext = model };
            window.Content = control;
            window.Show();
            window.UpdateLayout();
            Assert.Contains(control.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Details unavailable");
            var forget = Assert.Single(control.GetVisualDescendants().OfType<Button>(),
                button => button.CommandParameter is ReleaseMemoryDescriptor);
            Assert.Equal("Forget approval: Details unavailable", Avalonia.Automation.AutomationProperties.GetName(forget));
            await model.RemoveCommand.ExecuteAsync(forget.CommandParameter);
            Assert.Empty(await policy.ListAsync(token));
            Assert.IsType<ReleasePolicyRequired>(await policy.EvaluateAsync(request, token));
            await using var reload = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(path));
            Assert.Empty(await reload.ListAsync(token));
        }
        finally
        {
            window.Close();
            if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
        }
    }

    [AvaloniaTheory(Timeout = 15_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleaseMemoryAdministration_SafeContexts_RendersDetailsAndForgetsExactIdentifier(bool duplicateLabels)
    {
        var administration = new Administration();
        var first = administration.Items[0] with
        {
            SafeDisplayName = "All uses of this credential",
            SafeContext = new()
            {
                Provider = CredentialProviderKind.StoredSecret, Kind = CredentialKind.Secret,
                Source = CredentialSourceKind.SecretStore, Origin = CredentialUseKind.Manifest,
            },
        };
        var second = first with
        {
            Identifier = new() { Value = Guid.NewGuid() },
            SafeContext = duplicateLabels ? first.SafeContext : first.SafeContext! with
            {
                Provider = CredentialProviderKind.GitHub, Kind = CredentialKind.AccessToken,
                Source = CredentialSourceKind.GitHubCli, Origin = CredentialUseKind.Repository,
            },
        };
        administration.Items.Clear();
        administration.Items.AddRange([first, second]);
        await using var viewModel = new ReleaseMemoryAdministrationViewModel { Administration = administration };
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        var control = new ReleaseMemoryAdministrationControl { DataContext = viewModel };
        var window = new Window { Content = control };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var texts = control.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToArray();
            Assert.Contains(first.SafeContext!.ToString(), texts);
            Assert.Contains(second.SafeContext!.ToString(), texts);
            Assert.Equal(2, texts.Count(text => text == "All uses of this credential"));
            var actions = control.GetVisualDescendants().OfType<Button>()
                .Where(button => button.CommandParameter is ReleaseMemoryDescriptor).ToArray();
            Assert.Equal(2, actions.Length);
            Assert.All(actions, button => Assert.Contains(
                ((ReleaseMemoryDescriptor)button.CommandParameter!).SafeContext!.ToString(),
                Avalonia.Automation.AutomationProperties.GetName(button)));
            await viewModel.RemoveCommand.ExecuteAsync(second);
            Assert.Equal(second.Identifier, administration.Forgotten);
            Assert.Equal(first.Identifier, Assert.Single(viewModel.Items).Identifier);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public async Task ReleaseMemoryAdministration_StoreReload_PreservesOnlyTypedSafeDetailsAndAllUsesSemantics()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, ".access-ui-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "memory.json");
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            var request = Access.ReleaseAuthorizationContractTests.Request();
            request = request with
            {
                Credential = request.Credential with { Provider = CredentialProviderKind.StoredSecret, Kind = CredentialKind.Secret },
                SourceKind = CredentialSourceKind.SecretStore,
                Use = request.Use with { SafeDisplayName = "UNTRUSTED-DISPLAY-MARKER", Kind = CredentialUseKind.Manifest },
            };
            await using (var policy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(path)))
            {
                await policy.CommitAsync(new() { Request = request, Scope = ReleaseMemoryScopeKind.AllUses }, cancellationToken);
            }
            await using var reloaded = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(path));
            var descriptor = Assert.Single(await reloaded.ListAsync(cancellationToken));
            Assert.Equal(CredentialSourceKind.SecretStore, descriptor.SafeContext!.Source);
            Assert.Contains("Originally approved for: manifest use", descriptor.SafeContext.ToString());
            Assert.DoesNotContain("UNTRUSTED-DISPLAY-MARKER", await File.ReadAllTextAsync(path, cancellationToken));
            var differentConsumer = request with
            {
                RequestIdentifier = new() { Value = Guid.NewGuid() },
                RequestorIdentifier = new() { Digest = new string('e', 64) },
                Use = request.Use with { Kind = CredentialUseKind.Repository },
            };
            Assert.IsType<ReleasePolicyMatched>(await reloaded.EvaluateAsync(differentConsumer, cancellationToken));
            var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))!;
            json["records"]![0]!["safeContext"]!["source"] = 999;
            await File.WriteAllTextAsync(path, json.ToJsonString(), cancellationToken);
            await using (var invalid = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(path)))
            {
                var invalidDescriptor = Assert.Single(await invalid.ListAsync(cancellationToken));
                Assert.Null(invalidDescriptor.SafeContext);
                Assert.Equal(descriptor.Identifier, invalidDescriptor.Identifier);
                Assert.IsType<ReleasePolicyMatched>(await invalid.EvaluateAsync(differentConsumer, cancellationToken));
            }
            json["records"]![0]!.AsObject().Remove("safeContext");
            await File.WriteAllTextAsync(path, json.ToJsonString(), cancellationToken);
            await using var legacy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(path));
            var legacyDescriptor = Assert.Single(await legacy.ListAsync(cancellationToken));
            Assert.Null(legacyDescriptor.SafeContext);
            Assert.Equal(descriptor.Identifier, legacyDescriptor.Identifier);
            await legacy.ForgetAsync(legacyDescriptor.Identifier, cancellationToken);
            Assert.Empty(await legacy.ListAsync(cancellationToken));
        }
        finally
        {
            if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
        }
    }

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

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ControlBindsOnlySafeDescriptorAndForgetCommand()
    {
        var viewModel = new ReleaseMemoryAdministrationViewModel { Administration = new Administration() };
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        var control = new ReleaseMemoryAdministrationControl { DataContext = viewModel };
        var window = new Window { Content = control };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(control.GetVisualDescendants().OfType<Button>(), button =>
            button.Content?.ToString() == "Forget approval" && button.Command?.CanExecute(button.CommandParameter) == true);
        Assert.Contains(control.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Saved approvals");
        Assert.Contains(control.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Details unavailable");
        Assert.Contains(control.GetVisualDescendants().OfType<Button>(), button =>
            Avalonia.Automation.AutomationProperties.GetName(button) == "Forget approval: Details unavailable");
        Assert.Contains(control.GetVisualDescendants().OfType<TextBlock>(), block =>
            block.Text == "Forgetting removes only this approval. Cached credentials and provider access stay unchanged.");
        window.Close();
        await viewModel.DisposeAsync();
    }
}
