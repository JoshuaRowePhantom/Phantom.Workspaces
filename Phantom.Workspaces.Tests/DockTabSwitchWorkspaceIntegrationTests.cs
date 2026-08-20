using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Phantom.Dock.Avalonia.TabSwitching;
using Phantom.Workspaces;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// #1344 end-to-end coverage for the badge-overlay ownership guard. The app installs two overlapping
/// <see cref="DockTabSwitchController"/>s — an outer one on <c>TopLevelDockControl</c> (Alt+Shift) and one
/// inner controller per <c>WorkspacePaneDocument</c> DockControl (Alt) — and each inner DockControl is
/// nested inside the outer. Before the fix the outer pipeline's <c>DiscoverStrips</c> reached into every
/// nested inner strip and overwrote its per-container <c>DockTabSwitch.IndexContext</c> with an empty-label
/// context (last-writer-wins), so inner Alt badges vanished per-pane and Alt+Shift badges never lit inner
/// tabs. Unlike the single-controller coverage in <c>MainWindowIntegrationTests</c> and
/// <c>DockTabSwitchControllerTests</c> (which only assert controller-level <c>AreBadgesVisible</c>), these
/// tests assert per-label <c>IsVisible</c> on the actual realized tab containers after the outer
/// controller's discovery has run.
/// </summary>
public sealed class DockTabSwitchWorkspaceIntegrationTests
{
    private static bool HasVisibleLabelFor(Control container, KeyModifiers modifiers)
    {
        var context = DockTabSwitch.GetIndexContext(container);
        return context is not null
            && context.Labels.Any(label => label.GestureSet.Modifiers == modifiers && label.IsVisible);
    }

    private static void RaiseKey(MainWindow window, Key key, KeyModifiers modifiers, RoutedEventKind kind)
    {
        var treeView = window.GetVisualDescendants().OfType<Avalonia.Controls.TreeView>().First();
        window.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = kind == RoutedEventKind.Down ? InputElement.KeyDownEvent : InputElement.KeyUpEvent,
            Key = key,
            KeyModifiers = modifiers,
            Source = treeView,
        });
    }

    private enum RoutedEventKind
    {
        Down,
        Up,
    }

    private static async Task<MainWindow> BuildWindowWithTwoPanesEachTwoTabsAsync(MainWindowViewModel viewModel)
    {
        await MainWindowIntegrationTests.OpenTwoWorkspacesForTabSwitchAsync(viewModel, "1344cccc");

        var window = new MainWindow(viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Give each of the two panes two document tabs so every inner strip has ≥1 numbered container.
        MainWindowIntegrationTests.ActivateWorkspacePaneAtIndex(viewModel, "0");
        Dispatcher.UIThread.RunJobs();
        await viewModel.OpenTabAsync(new WebViewModel("https://a1.example.com") { Id = "1344-a1", Title = "A1" });
        await viewModel.OpenTabAsync(new WebViewModel("https://a2.example.com") { Id = "1344-a2", Title = "A2" });

        MainWindowIntegrationTests.ActivateWorkspacePaneAtIndex(viewModel, "1");
        Dispatcher.UIThread.RunJobs();
        await viewModel.OpenTabAsync(new WebViewModel("https://b1.example.com") { Id = "1344-b1", Title = "B1" });
        await viewModel.OpenTabAsync(new WebViewModel("https://b2.example.com") { Id = "1344-b2", Title = "B2" });

        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task DockTabSwitch_AltHeld_EveryWorkspacePaneInnerTab_HasAltLabelWithIsVisibleTrue()
    {
        // Symptom (a) end-to-end: with Alt held, EVERY realized inner (workspace-content) tab across
        // EVERY pane must show a visible Alt label. Before the ownership guard the outer
        // TopLevelDockControl pipeline overwrote some inner containers' IndexContext with empty labels,
        // so those tabs showed nothing.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var window = await BuildWindowWithTwoPanesEachTwoTabsAsync(viewModel);
        try
        {
            var topLevel = MainWindowIntegrationTests.GetTopLevelDockControl(window);

            // Hold the Alt chord (the inner controllers' binding) from focus outside the DockControls.
            RaiseKey(window, Key.LeftAlt, KeyModifiers.Alt, RoutedEventKind.Down);
            Dispatcher.UIThread.RunJobs();

            // Every inner DockControl (a WorkspacePaneDocument's nested pane control) is distinct from the
            // outer TopLevelDockControl.
            var innerDockControls = window.GetVisualDescendants()
                .OfType<DockControl>()
                .Where(d => !ReferenceEquals(d, topLevel))
                .ToList();
            Assert.NotEmpty(innerDockControls);

            var innerContainers = innerDockControls
                .SelectMany(d => d.GetVisualDescendants().OfType<DocumentTabStripItem>())
                .Where(c => c.DataContext is WorkspaceDocument)
                .ToList();
            Assert.NotEmpty(innerContainers);

            foreach (var container in innerContainers)
            {
                Assert.True(
                    HasVisibleLabelFor(container, KeyModifiers.Alt),
                    $"Inner tab '{(container.DataContext as WorkspaceDocument)?.Id}' has no visible Alt label — " +
                    "its per-container IndexContext was overwritten by the outer pipeline (#1344).");
            }
        }
        finally
        {
            await MainWindowIntegrationTests.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task DockTabSwitch_AltShiftHeld_WorkspaceTabStripContainer_HasAltShiftLabelWithIsVisibleTrue()
    {
        // Symptom (b) end-to-end: with Alt+Shift held, the OUTER workspace-tab containers (the
        // WorkspacePaneDocument headers on TopLevelDockControl) must show a visible Alt+Shift label. The
        // guard keeps the outer pipeline focused on its own strips so it correctly numbers them.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var window = await BuildWindowWithTwoPanesEachTwoTabsAsync(viewModel);
        try
        {
            var topLevel = MainWindowIntegrationTests.GetTopLevelDockControl(window);

            // Hold the Alt+Shift chord (the outer controller's binding) from focus outside the DockControls.
            RaiseKey(window, Key.LeftAlt, KeyModifiers.Alt, RoutedEventKind.Down);
            RaiseKey(window, Key.LeftShift, KeyModifiers.Alt | KeyModifiers.Shift, RoutedEventKind.Down);
            Dispatcher.UIThread.RunJobs();

            var outerContainers = topLevel.GetVisualDescendants()
                .OfType<DocumentTabStripItem>()
                .Where(c => c.DataContext is WorkspacePaneDocument)
                .ToList();
            Assert.NotEmpty(outerContainers);

            foreach (var container in outerContainers)
            {
                Assert.True(
                    HasVisibleLabelFor(container, KeyModifiers.Alt | KeyModifiers.Shift),
                    $"Outer workspace tab '{(container.DataContext as WorkspacePaneDocument)?.Id}' has no " +
                    "visible Alt+Shift label after the outer controller's DiscoverStrips ran (#1344).");
            }
        }
        finally
        {
            await MainWindowIntegrationTests.CloseWindowAsync(window);
        }
    }
}
