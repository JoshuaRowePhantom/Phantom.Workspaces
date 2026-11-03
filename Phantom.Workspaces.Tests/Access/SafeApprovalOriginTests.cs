using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Phantom.Workspaces.Controls;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests.Access;

public sealed class SafeApprovalOriginTests
{
    [Theory]
    [InlineData("${SECRET:Key}")]
    [InlineData("https://provider.test/token")]
    [InlineData("Bearer private")]
    [InlineData("ghp_private")]
    [InlineData("github_pat_private")]
    [InlineData("Name\r\nprivate")]
    [InlineData("Name\u202Eprivate")]
    public void Labels_RejectMaterialSyntaxAndPayloads(string value)
    {
        Assert.Null(SafeApprovalLabel.FromName(value));
        Assert.Throws<JsonException>(() => new SafeApprovalLabel(value));
        Assert.Null(SafeApprovalLabel.FromName(new string('a', 121)));
        Assert.Null(SafeWorkspaceEntityReference.FromEntity(SafeWorkspaceEntityKind.Manifest, new string('a', 64), "Name"));
    }

    [Theory]
    [InlineData(ReleaseMemoryScopeKind.AllUses)]
    [InlineData(ReleaseMemoryScopeKind.ExactUse)]
    [InlineData(ReleaseMemoryScopeKind.SessionIdentity)]
    [InlineData(ReleaseMemoryScopeKind.ManifestIdentity)]
    public async Task StoreReload_PreservesOriginAndBoundScope_WithoutChangingMatching(ReleaseMemoryScopeKind scope)
    {
        var directory = Path.Combine(Environment.CurrentDirectory, ".access-origin-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "memory.json");
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var origin = Origin("Build", "Review", "GitHub MCP");
            var request = Request(origin);
            await using (var policy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(path)))
                await policy.CommitAsync(new() { Request = request, Scope = scope }, ct);
            await using var reloaded = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(path));
            var row = Assert.Single(await reloaded.ListAsync(ct));
            Assert.Equal(origin, row.SafeContext!.Details);
            Assert.Equal(scope, row.Scope);
            Assert.Contains("Credential: Build API key", row.SafeContext.ToString());
            Assert.Contains("Originally requested by:", row.SafeContext.ToString());
            if (scope == ReleaseMemoryScopeKind.SessionIdentity) Assert.Contains(origin.Session!.ToString(), row.SafeDisplayName);
            if (scope == ReleaseMemoryScopeKind.ManifestIdentity) Assert.Contains(origin.Manifest!.ToString(), row.SafeDisplayName);
            var renamed = request with { Use = request.Use with
            {
                Lineage = request.Use.Lineage with { ApprovalOrigin = Origin("Renamed", "Other session", "Other MCP") },
            } };
            Assert.IsType<ReleasePolicyMatched>(await reloaded.EvaluateAsync(renamed, ct));
            var changedConsumer = renamed with
            {
                RequestorIdentifier = CredentialIdentityFactory.CreateRequestorIdentifier(AccessRequestorKind.Manifest, "different"),
                Use = renamed.Use with { Fingerprint = new() { Digest = new string('e', 64) } },
            };
            Assert.Equal(scope == ReleaseMemoryScopeKind.AllUses, await reloaded.EvaluateAsync(changedConsumer, ct) is ReleasePolicyMatched);
            Assert.DoesNotContain("UNTRUSTED-PAYLOAD", await File.ReadAllTextAsync(path, ct));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ProvisionalAccountFinalization_PreservesTypedOriginAndOriginalScope()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, ".access-origin-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "memory.json");
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var request = Request(Origin("Tools", "Review", "GitHub MCP"));
            request = request with { Credential = request.Credential with { Provider = CredentialProviderKind.OAuth, AccountIdentifier = null } };
            await using var policy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(path));
            var authorization = await policy.CommitAsync(new() { Request = request, Scope = ReleaseMemoryScopeKind.AllUses }, ct);
            Assert.False(File.Exists(path));
            var account = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.OAuth, "private-account");
            await policy.FinalizeCredentialIdentityAsync(authorization.Identifier, new ResolvedCredentialIdentity
            {
                LogicalIdentifier = request.Credential.LogicalIdentifier, SourceIdentifier = request.SourceIdentifier,
                AccountIdentifier = account, RequestIdentifier = request.RequestIdentifier, RequestGeneration = request.RequestGeneration,
            }, ct);
            await using var reloaded = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(path));
            var row = Assert.Single(await reloaded.ListAsync(ct));
            Assert.Equal(request.Use.Lineage.ApprovalOrigin, row.SafeContext!.Details);
            Assert.Equal(ReleaseMemoryScopeKind.AllUses, row.Scope);
            Assert.Equal(account, row.AccountIdentifier);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenderedStoreReload_DistinguishesEqualNames_NavigatesRealIds_AndForgetsExactRow(bool malformed)
    {
        var directory = Path.Combine(Environment.CurrentDirectory, ".access-origin-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "memory.json");
        var ct = TestContext.Current.CancellationToken;
        var window = new Window();
        try
        {
            var first = Request(Origin("Tools", "Review", "GitHub MCP"));
            var second = Request(Origin("Tools", "Review", "GitHub MCP"));
            second = second with { Use = second.Use with { Fingerprint = new() { Digest = new string('e', 64) } } };
            await using (var policy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(path)))
            {
                await policy.CommitAsync(new() { Request = first, Scope = ReleaseMemoryScopeKind.ExactUse }, ct);
                await policy.CommitAsync(new() { Request = second, Scope = ReleaseMemoryScopeKind.ExactUse }, ct);
            }
            if (malformed)
            {
                var json = JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!;
                json["records"]![0]!["safeContext"]!["details"]!["manifest"] = JsonValue.Create("https://provider.test/private");
                json["records"]![0]!["safeContext"]!["details"]!["requester"] = JsonNode.Parse("""{"value":"${SECRET:PRIVATE}"}""");
                json["records"]![0]!["safeContext"]!["details"]!["credential"] = new JsonObject { ["value"] = new string('x', 121) };
                await File.WriteAllTextAsync(path, json.ToJsonString(), ct);
            }
            await using var reloaded = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(path));
            await using var model = new ReleaseMemoryAdministrationViewModel
            {
                Administration = new ReleaseMemoryAdministration { Policy = reloaded },
            };
            await model.RefreshAsync(ct);
            Assert.Equal(2, model.Items.Count);
            var row = model.Items[0];
            Assert.Equal(CredentialSourceKind.SecretStore, row.SafeContext!.Source);
            Assert.Equal(first.Use.Lineage.ApprovalOrigin!.Session, row.SafeContext.Details!.Session);
            if (malformed) Assert.Null(row.SafeContext.Details.Manifest);
            var opened = new List<Guid>();
            var control = new ReleaseMemoryAdministrationControl
            {
                DataContext = model,
                NavigateEntityAsync = reference => { opened.Add(reference.Identifier); return Task.CompletedTask; },
            };
            window.Content = control;
            window.Show();
            window.UpdateLayout();
            var text = string.Join("\n", control.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text));
            Assert.Contains("Credential: Build API key", text);
            Assert.Contains("GitHub MCP", text);
            Assert.Contains("Applies to: This exact use", text);
            Assert.Contains("Source: Secret store", text);
            Assert.DoesNotContain("PRIVATE", text);
            Assert.DoesNotContain("provider.test", text);
            var forgets = control.GetVisualDescendants().OfType<Button>()
                .Where(button => button.CommandParameter is ReleaseMemoryDescriptor).ToArray();
            Assert.Equal(2, forgets.Length);
            Assert.NotEqual(AutomationProperties.GetName(forgets[0]), AutomationProperties.GetName(forgets[1]));
            Assert.All(forgets, button => Assert.Contains("Review", AutomationProperties.GetName(button)));
            var link = control.GetVisualDescendants().OfType<Button>()
                .First(button => button.CommandParameter is SafeWorkspaceEntityReference { Kind: SafeWorkspaceEntityKind.Session });
            link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(first.Use.Lineage.ApprovalOrigin.Session!.Identifier, Assert.Single(opened));
            await model.RemoveCommand.ExecuteAsync(model.Items[1]);
            Assert.Equal(row.Identifier, Assert.Single(await reloaded.ListAsync(ct)).Identifier);
            Assert.IsType<ReleasePolicyMatched>(await reloaded.EvaluateAsync(first, ct));
            Assert.IsType<ReleasePolicyRequired>(await reloaded.EvaluateAsync(second, ct));
        }
        finally { window.Close(); if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    internal static SafeApprovalOrigin Origin(string manifest, string session, string requester) => new()
    {
        Manifest = new(SafeWorkspaceEntityKind.Manifest, Guid.NewGuid(), new(manifest)),
        Session = new(SafeWorkspaceEntityKind.Session, Guid.NewGuid(), new(session)),
        Credential = new("Build API key"), Requester = new(requester),
    };

    private static ReleasePolicyRequest Request(SafeApprovalOrigin origin)
    {
        var request = ReleaseAuthorizationContractTests.Request();
        return request with
        {
            Credential = request.Credential with { Provider = CredentialProviderKind.StoredSecret, Kind = CredentialKind.Secret },
            SourceKind = CredentialSourceKind.SecretStore,
            Use = request.Use with
            {
                SafeDisplayName = "UNTRUSTED-PAYLOAD",
                Lineage = request.Use.Lineage with { ApprovalOrigin = origin },
            },
        };
    }
}
