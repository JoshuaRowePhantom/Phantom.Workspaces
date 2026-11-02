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
    private readonly Grid presentation;
    private readonly TabItem requestsPage;
    private readonly Controls.AuthorizationRequestRowsControl requests;
    private Task? refreshTask;
    internal bool IsDisposed => Volatile.Read(ref this.disposalStarted) != 0;
    public AuthorizationRequestRowsViewModel RequestRows { get; }
    public CredentialAccessPage SelectedPage => (CredentialAccessPage)this.pages.SelectedIndex;
    internal TabControl Pages => this.pages;
    internal bool RequestsAlongsideManagement => this.presentation.Children.Contains(this.requests);

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
        this.requests = new Controls.AuthorizationRequestRowsControl { DataContext = this.RequestRows };
        this.requestsPage = new TabItem { Header = CredentialAccessText.Requests, Content = this.requests };
        this.pages = new TabControl
        {
            Margin = new Thickness(16),
            Items =
            {
                this.requestsPage,
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
        this.presentation = new Grid { ColumnDefinitions = new ColumnDefinitions("0,*"), Children = { this.pages } };
        this.pages.SelectionChanged += (_, arguments) =>
        {
            if (ReferenceEquals(arguments.Source, this.pages)) { this.RestoreRequestsTab(); }
        };
        Grid.SetColumn(this.pages, 1);
        this.Content = this.presentation;
        this.SelectPage(CredentialAccessPage.SavedApprovals);
    }

    public void SelectPage(CredentialAccessPage page)
    {
        if (!Enum.IsDefined(page)) { throw new ArgumentOutOfRangeException(nameof(page)); }
        if (!this.IsDisposed)
        {
            this.RestoreRequestsTab();
            this.pages.SelectedIndex = (int)page;
        }
    }

    private void RestoreRequestsTab()
    {
        if (!this.RequestsAlongsideManagement) { return; }
        this.presentation.Children.Remove(this.requests);
        this.requests.Margin = default;
        this.requestsPage.Content = this.requests;
        this.presentation.ColumnDefinitions[0].Width = new GridLength(0);
    }

    internal void PresentRequestsWithoutActivation()
    {
        if (this.IsDisposed || this.SelectedPage == CredentialAccessPage.Requests || this.RequestsAlongsideManagement) { return; }
        if (!this.pages.IsKeyboardFocusWithin)
        {
            this.SelectPage(CredentialAccessPage.Requests);
            return;
        }
        // Do not hide the focused management editor. Move the exact retained
        // Requests control beside it, rather than replacing the editing page.
        this.requestsPage.Content = null;
        this.presentation.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        this.requests.Margin = new Thickness(12);
        this.presentation.Children.Add(this.requests);
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
