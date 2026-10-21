using Avalonia;
using Avalonia.Controls;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces;

public sealed class AccessAdministrationWindow : Window, IAsyncDisposable
{
    private readonly ReleaseMemoryAdministrationViewModel releaseMemory;
    private readonly CredentialCacheAdministrationViewModel caches;
    private readonly ProviderGrantAdministrationViewModel grants;
    private readonly TaskCompletionSource disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposalStarted;
    internal bool IsDisposed => Volatile.Read(ref this.disposalStarted) != 0;

    public AccessAdministrationWindow(AccessHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        this.Title = "Access administration";
        this.Width = 720;
        this.Height = 520;
        this.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.releaseMemory = new ReleaseMemoryAdministrationViewModel { Administration = host.ReleaseMemoryAdministration };
        this.caches = new CredentialCacheAdministrationViewModel { Administration = host.CredentialCacheAdministration };
        this.grants = new ProviderGrantAdministrationViewModel { Administration = host.ProviderGrantAdministration };
        this.Content = new TabControl
        {
            Margin = new Thickness(16),
            Items =
            {
                new TabItem
                {
                    Header = "Release memory",
                    Content = new Controls.ReleaseMemoryAdministrationControl { DataContext = this.releaseMemory },
                },
                new TabItem
                {
                    Header = "Local caches",
                    Content = new Controls.CredentialCacheAdministrationControl { DataContext = this.caches },
                },
                new TabItem
                {
                    Header = "Provider grants",
                    Content = new Controls.ProviderGrantAdministrationControl { DataContext = this.grants },
                },
            },
        };
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (this.IsDisposed) { return; }
        await this.releaseMemory.RefreshAsync(cancellationToken);
        await this.caches.RefreshAsync(cancellationToken);
        await this.grants.RefreshAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposalStarted, 1) != 0)
        {
            await this.disposal.Task;
            return;
        }
        var failures = new List<Exception>();
        foreach (var viewModel in new IAsyncDisposable[] { this.releaseMemory, this.caches, this.grants })
        {
            try { await viewModel.DisposeAsync(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (failures.Count > 0)
        {
            var failure = new AggregateException("Access administration shutdown did not complete cleanly.", failures);
            this.disposal.TrySetException(failure);
            throw failure;
        }
        this.disposal.TrySetResult();
    }
}
