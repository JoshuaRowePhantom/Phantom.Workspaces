using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class UrlOpenerTests
{
    // Fake IWorkspaceTabService that records calls without touching Avalonia / dock plumbing.
    private sealed class FakeTabService : IWorkspaceTabService
    {
        public List<WorkspaceTabViewModel> OpenedTabs { get; } = new();
        public List<string> TryFocusUrls { get; } = new();
        public bool TryFocusResult { get; set; }
        public Func<Task>? OpenTabCallback { get; set; }

        public Task OpenTabAsync(WorkspaceTabViewModel tab, string? insertAfterTabId = null, bool focus = true, string? workspacePaneId = null)
        {
            this.OpenedTabs.Add(tab);
            return this.OpenTabCallback?.Invoke() ?? Task.CompletedTask;
        }

        public Task ReplaceTabAsync(WorkspaceTabViewModel oldTab, WorkspaceTabViewModel newTab) => Task.CompletedTask;

        public void CloseTab(WorkspaceTabViewModel tab) { }

        public Task<bool> TryFocusExistingWebTabAsync(string url)
        {
            this.TryFocusUrls.Add(url);
            return Task.FromResult(this.TryFocusResult);
        }
    }

    private static (UrlOpener Opener, FakeTabService Tabs, List<string> ExternalUrls) Create(bool alreadyOpen = false)
    {
        var tabs = new FakeTabService { TryFocusResult = alreadyOpen };
        var external = new List<string>();
        var opener = new UrlOpener(tabs, url => { external.Add(url); return Task.CompletedTask; });
        return (opener, tabs, external);
    }

    [Fact]
    public async Task OpenAsync_Auto_HttpUrl_OpensEmbeddedWebViewModelTab()
    {
        var (opener, tabs, external) = Create();
        await opener.OpenAsync(new OpenUrlRequest("http://example.com/"), TestContext.Current.CancellationToken);

        Assert.Single(tabs.OpenedTabs);
        Assert.IsType<WebViewModel>(tabs.OpenedTabs[0]);
        Assert.Equal("http://example.com/", ((WebViewModel)tabs.OpenedTabs[0]).AddressBarUrl);
        Assert.Empty(external);
    }

    [Fact]
    public async Task OpenAsync_Auto_HttpsUrl_OpensEmbeddedWebViewModelTab()
    {
        var (opener, tabs, external) = Create();
        await opener.OpenAsync(new OpenUrlRequest("https://example.com/"), TestContext.Current.CancellationToken);

        Assert.Single(tabs.OpenedTabs);
        Assert.IsType<WebViewModel>(tabs.OpenedTabs[0]);
        Assert.Empty(external);
    }

    [Fact]
    public async Task OpenAsync_Auto_MailtoScheme_UsesExternalLauncher()
    {
        var (opener, tabs, external) = Create();
        await opener.OpenAsync(new OpenUrlRequest("mailto:someone@example.com"), TestContext.Current.CancellationToken);

        Assert.Empty(tabs.OpenedTabs);
        Assert.Empty(tabs.TryFocusUrls);
        Assert.Single(external);
        Assert.Equal("mailto:someone@example.com", external[0]);
    }

    [Fact]
    public async Task OpenAsync_Auto_VsCodeScheme_UsesExternalLauncher()
    {
        var (opener, tabs, external) = Create();
        await opener.OpenAsync(new OpenUrlRequest("vscode://vscode-remote/tunnel+abc"), TestContext.Current.CancellationToken);

        Assert.Empty(tabs.OpenedTabs);
        Assert.Single(external);
    }

    [Fact]
    public async Task OpenAsync_External_HttpUrl_UsesExternalLauncherEvenForHttp()
    {
        var (opener, tabs, external) = Create();
        await opener.OpenAsync(new OpenUrlRequest("https://example.com/") { Preference = UrlOpenPreference.External }, TestContext.Current.CancellationToken);

        Assert.Empty(tabs.OpenedTabs);
        Assert.Empty(tabs.TryFocusUrls);
        Assert.Single(external);
    }

    [Fact]
    public async Task OpenAsync_Embedded_NonHttpScheme_FallsBackToExternal()
    {
        var (opener, tabs, external) = Create();
        await opener.OpenAsync(new OpenUrlRequest("mailto:x@y") { Preference = UrlOpenPreference.Embedded }, TestContext.Current.CancellationToken);

        Assert.Empty(tabs.OpenedTabs);
        Assert.Single(external);
    }

    [Fact]
    public async Task OpenAsync_NullOrEmptyUrl_DoesNothing()
    {
        var (opener, tabs, external) = Create();
        await opener.OpenAsync(new OpenUrlRequest(string.Empty), TestContext.Current.CancellationToken);

        Assert.Empty(tabs.OpenedTabs);
        Assert.Empty(tabs.TryFocusUrls);
        Assert.Empty(external);
    }

    [Fact]
    public async Task OpenAsync_NoActiveWorkspace_DropsSilently()
    {
        // Simulates the "no workspace loaded" case where OpenTabAsync silently returns.
        var (opener, _, external) = Create();
        // OpenTabCallback default returns CompletedTask (no-op) — same shape as OpenTabAsync when
        // there's no active workspace. OpenAsync should not throw or fall back to external.
        await opener.OpenAsync(new OpenUrlRequest("https://example.com/"), TestContext.Current.CancellationToken);

        Assert.Empty(external);
    }

    [Fact]
    public async Task OpenAsync_Embedded_CreatesWebViewModelWithGuidTabId()
    {
        var (opener, tabs, _) = Create();
        await opener.OpenAsync(new OpenUrlRequest("https://example.com/") { Preference = UrlOpenPreference.Embedded }, TestContext.Current.CancellationToken);

        var tab = Assert.Single(tabs.OpenedTabs);
        Assert.StartsWith("web-", tab.Id);
        Assert.True(tab.Id.Length > "web-".Length);
    }

    [Fact]
    public async Task OpenAsync_Auto_SameUrlAlreadyOpenInWorkspace_ActivatesExistingTabAndDoesNotOpenNew()
    {
        var (opener, tabs, external) = Create(alreadyOpen: true);
        await opener.OpenAsync(new OpenUrlRequest("https://example.com/"), TestContext.Current.CancellationToken);

        Assert.Single(tabs.TryFocusUrls);
        Assert.Empty(tabs.OpenedTabs);
        Assert.Empty(external);
    }

    [Fact]
    public async Task OpenAsync_Embedded_SameUrlAlreadyOpen_ActivatesExisting()
    {
        var (opener, tabs, external) = Create(alreadyOpen: true);
        await opener.OpenAsync(new OpenUrlRequest("https://example.com/") { Preference = UrlOpenPreference.Embedded }, TestContext.Current.CancellationToken);

        Assert.Single(tabs.TryFocusUrls);
        Assert.Empty(tabs.OpenedTabs);
        Assert.Empty(external);
    }

    [Fact]
    public async Task OpenAsync_Auto_DifferentUrl_OpensNewTab()
    {
        var (opener, tabs, _) = Create(alreadyOpen: false);
        await opener.OpenAsync(new OpenUrlRequest("https://different.example.com/"), TestContext.Current.CancellationToken);

        Assert.Single(tabs.TryFocusUrls);
        Assert.Single(tabs.OpenedTabs);
    }

    [Fact]
    public async Task OpenAsync_External_SameUrlAlreadyOpen_DoesNotActivateExistingTab()
    {
        // External preference must bypass dedup entirely.
        var (opener, tabs, external) = Create(alreadyOpen: true);
        await opener.OpenAsync(new OpenUrlRequest("https://example.com/") { Preference = UrlOpenPreference.External }, TestContext.Current.CancellationToken);

        Assert.Empty(tabs.TryFocusUrls);
        Assert.Empty(tabs.OpenedTabs);
        Assert.Single(external);
    }
}
