using System.IO;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Controls;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class LegacyAuthorizationUiRemovalTests
{
    [Theory]
    [InlineData("Phantom.Workspaces.SecretUseDialogWindow")]
    [InlineData("Phantom.Workspaces.ViewModels.SecretUseDialogViewModel")]
    [InlineData("Phantom.Workspaces.ViewModels.SecretUseDialogRowViewModel")]
    [InlineData("Phantom.Workspaces.Services.Secrets.AvaloniaSecretUseDialogHost")]
    [InlineData("Phantom.Workspaces.CredentialManagerDialogWindow")]
    [InlineData("Phantom.Workspaces.ViewModels.CredentialManagerDialogViewModel")]
    [InlineData("Phantom.Workspaces.ViewModels.CredentialGroupViewModel")]
    [InlineData("Phantom.Workspaces.ViewModels.MemorizedUsePlaceViewModel")]
    [InlineData("Phantom.Workspaces.ViewModels.UnusedSavedCredentialViewModel")]
    [InlineData("Phantom.Workspaces.Services.Secrets.SecretMemoryEnumerator")]
    [InlineData("Phantom.Workspaces.Services.Secrets.SecretMemorySnapshot")]
    [InlineData("Phantom.Workspaces.Services.Secrets.SecretMemoryGroup")]
    [InlineData("Phantom.Workspaces.Services.Secrets.SecretMemoryUse")]
    [InlineData("Phantom.Workspaces.Services.Secrets.SecretSourceDisplay")]
    [InlineData("Phantom.Workspaces.Services.Secrets.SecretProvider")]
    [InlineData("Phantom.Workspaces.Converters.SecretSourceLabelConverter")]
    public void DisconnectedLegacyTypesAreAbsentFromApplicationAssembly(string name) =>
        Assert.Null(typeof(MainWindow).Assembly.GetType(name));

    [Fact]
    public void AdministrationWindowRequiresTheRealHostAndModernControlsRemainAvailable()
    {
        var constructor = Assert.Single(typeof(AccessAdministrationWindow).GetConstructors());
        Assert.Equal(typeof(AccessHostServices), Assert.Single(constructor.GetParameters()).ParameterType);
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(AccessAdministrationWindow)));
        foreach (var type in new[]
        {
            typeof(AuthorizationRequestRowsControl),
            typeof(ReleaseMemoryAdministrationControl),
            typeof(CredentialCacheAdministrationControl),
            typeof(ProviderGrantAdministrationControl),
        })
        {
            Assert.Same(typeof(MainWindow).Assembly, type.Assembly);
        }
    }

    [AvaloniaFact]
    public async Task ExistingNavigationUsesOnlyTheSharedRealAdministrationServices()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, ".access-ui-removal-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            await using var host = AccessHostServices.Create(new AccessHostOptions
            {
                PlatformSecretStore = new NullPlatformSecretStore(),
                Dispatch = action => action(),
                ReleaseMemoryFilePath = Path.Combine(directory, "memory.json"),
            });
            var services = new ApplicationServices(
                MainWindowIntegrationTests.CreateTestRunningAgentChatTable(),
                new AgentPersistenceStoreCache(),
                NullLoggerFactory.Instance,
                accessBroker: host.Broker);
            await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel(
                profileStore: new ProfileStore(Path.Combine(directory, "profile.json")),
                applicationServices: services);
            await viewModel.InitializeAsync();
            await using var popup = new AuthorizationRequestsPopupViewModel { Rows = host.AuthorizationRows };
            var window = new MainWindow(viewModel);
            window.AttachAccessHost(host, popup);
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var button = window.GetVisualDescendants().OfType<Button>()
                    .Single(control => control.Content is string label && label == "🤫");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await window.AccessAdministrationReady;
                Dispatcher.UIThread.RunJobs();
                var administration = Assert.IsType<AccessAdministrationWindow>(window.LastAccessAdministrationWindow);
                var tabs = Assert.IsType<TabControl>(administration.Content);
                var items = tabs.Items.Cast<TabItem>().ToArray();
                Assert.Equal(4, items.Length);
                Assert.Equal(
                    new[] { "Requests", "Saved approvals", "Cached credentials", "Provider access" },
                    items.Select(item => Assert.IsType<string>(item.Header)));
                var requests = Assert.IsType<AuthorizationRequestRowsControl>(items[0].Content);
                Assert.Same(host.AuthorizationRows, requests.DataContext);
                Assert.Same(host.AuthorizationRows, administration.RequestRows);
                Assert.Equal(CredentialAccessPage.SavedApprovals, administration.SelectedPage);
                Assert.Same(window, administration.Owner);
                Assert.True(window.IsEnabled);
                AssertAdministration(items[1], typeof(ReleaseMemoryAdministrationControl), host.ReleaseMemoryAdministration);
                AssertAdministration(items[2], typeof(CredentialCacheAdministrationControl), host.CredentialCacheAdministration);
                AssertAdministration(items[3], typeof(ProviderGrantAdministrationControl), host.ProviderGrantAdministration);
                administration.Close();
                Dispatcher.UIThread.RunJobs();
                await administration.DisposeAsync();
                await administration.DisposeAsync();
                Assert.True(administration.IsDisposed);
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }
        finally
        {
            if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
        }
    }

    private static void AssertAdministration(TabItem tab, Type controlType, object service)
    {
        var control = Assert.IsAssignableFrom<UserControl>(tab.Content);
        Assert.Equal(controlType, control.GetType());
        Assert.NotNull(control.DataContext);
        Assert.Same(service, control.DataContext.GetType().GetProperty("Administration")!.GetValue(control.DataContext));
    }
}
