using Avalonia;
using Avalonia.Controls;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces;

public enum CredentialAccessPage { Requests, SavedApprovals, CachedCredentials, ProviderAccess }

public sealed class AccessAdministrationWindow : Window, IAsyncDisposable
{
    private readonly ReleaseMemoryAdministrationViewModel releaseMemory;
    private readonly CredentialCacheAdministrationViewModel caches;
    private readonly ProviderGrantAdministrationViewModel grants;
    private readonly TaskCompletionSource disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposalStarted;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TabControl pages;
    private Task? refreshTask;
    internal bool IsDisposed => Volatile.Read(ref this.disposalStarted) != 0;
    public AuthorizationRequestRowsViewModel RequestRows { get; }
    public CredentialAccessPage SelectedPage => (CredentialAccessPage)this.pages.SelectedIndex;

    public AccessAdministrationWindow(AccessHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        this.RequestRows = host.AuthorizationRows;
        this.Title = CredentialAccessText.Title;
        this.Width = 720;
        this.Height = 520;
        this.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.releaseMemory = new ReleaseMemoryAdministrationViewModel { Administration = host.ReleaseMemoryAdministration };
        this.caches = new CredentialCacheAdministrationViewModel { Administration = host.CredentialCacheAdministration };
        this.grants = new ProviderGrantAdministrationViewModel { Administration = host.ProviderGrantAdministration };
        this.pages = new TabControl
        {
            Margin = new Thickness(16),
            Items =
            {
                new TabItem
                {
                    Header = CredentialAccessText.Requests,
                    Content = new Controls.AuthorizationRequestRowsControl { DataContext = this.RequestRows },
                },
                new TabItem
                {
                    Header = CredentialAccessText.SavedApprovals,
                    Content = new Controls.ReleaseMemoryAdministrationControl { DataContext = this.releaseMemory },
                },
                new TabItem
                {
                    Header = CredentialAccessText.CachedCredentials,
                    Content = new Controls.CredentialCacheAdministrationControl { DataContext = this.caches },
                },
                new TabItem
                {
                    Header = CredentialAccessText.ProviderAccess,
                    Content = new Controls.ProviderGrantAdministrationControl { DataContext = this.grants },
                },
            },
        };
        this.Content = this.pages;
        this.SelectPage(CredentialAccessPage.SavedApprovals);
    }

    public void SelectPage(CredentialAccessPage page)
    {
        if (!Enum.IsDefined(page)) { throw new ArgumentOutOfRangeException(nameof(page)); }
        if (!this.IsDisposed) { this.pages.SelectedIndex = (int)page; }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (this.IsDisposed) { return Task.CompletedTask; }
        return this.refreshTask ??= this.RefreshCoreAsync(cancellationToken);
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(this.lifetime.Token, cancellationToken);
        await this.releaseMemory.RefreshAsync(linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        await this.caches.RefreshAsync(linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        await this.grants.RefreshAsync(linked.Token);
        linked.Token.ThrowIfCancellationRequested();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposalStarted, 1) != 0)
        {
            await this.disposal.Task;
            return;
        }
        var failures = new List<Exception>();
        try { await this.lifetime.CancelAsync(); }
        catch (Exception exception) { failures.Add(exception); }
        if (this.refreshTask is { } refresh)
        {
            try { await refresh; }
            catch (OperationCanceledException) when (this.lifetime.IsCancellationRequested) { }
            catch (Exception exception) { failures.Add(exception); }
        }
        foreach (var viewModel in new IAsyncDisposable[] { this.releaseMemory, this.caches, this.grants })
        {
            try { await viewModel.DisposeAsync(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        this.lifetime.Dispose();
        if (failures.Count > 0)
        {
            var failure = new AggregateException("Access administration shutdown did not complete cleanly.", failures);
            this.disposal.TrySetException(failure);
            throw failure;
        }
        this.disposal.TrySetResult();
    }
}
