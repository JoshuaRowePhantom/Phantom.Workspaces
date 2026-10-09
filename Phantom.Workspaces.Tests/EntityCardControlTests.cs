using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phantom.Workspaces.Controls;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Gui.Shared.Controls;
using Phantom.Workspaces.ViewModels;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

// Issue #1164: a tool+note entity must compose per-type presentations. The card must render both
// the tool chrome/type labels AND the note markdown body — nothing contributed by any of the
// entity's non-abstract types may be silently hidden.
public sealed class EntityCardControlTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityCardControl_ToolAndNote_RendersNoteMarkdown()
    {
        var card = new EntityCardControl { DataContext = await BuildToolNoteCardViewModelAsync() };
        var window = new Window { Content = card };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var markdownView = window.GetVisualDescendants()
                .OfType<WorkspaceMarkdownView>()
                .FirstOrDefault(view => view.Markdown is { } text && text.Contains("# Run VS Code Tunnel", StringComparison.Ordinal));

            Assert.NotNull(markdownView);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityCardControl_ToolAndNote_ShowsBothTypeLabels()
    {
        var card = new EntityCardControl { DataContext = await BuildToolNoteCardViewModelAsync() };
        var window = new Window { Content = card };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var typeLabels = window.GetVisualDescendants()
                .OfType<TextBlock>()
                .Select(t => t.Text ?? string.Empty)
                .ToArray();

            Assert.Contains(typeLabels, text => text.Contains("tool", StringComparison.Ordinal)
                && text.Contains("note", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
        }
    }

    // Issue #1214: every text-data element in the entity card is a SafeSelectableTextBlock so it
    // can be selected/copied, while preserving wrapping and highlight-match runs.
    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityCardControl_DisplayName_RendersAsSafeSelectableTextBlock()
    {
        var card = new EntityCardControl { DataContext = await BuildToolNoteCardViewModelAsync() };
        var window = new Window { Content = card };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var title = window.GetVisualDescendants()
                .OfType<SafeSelectableTextBlock>()
                .FirstOrDefault(t => t.Classes.Contains("workspace-entity-title"));

            Assert.NotNull(title);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityCardControl_EntityTypeLabel_RendersAsSafeSelectableTextBlock()
    {
        var card = new EntityCardControl { DataContext = await BuildToolNoteCardViewModelAsync() };
        var window = new Window { Content = card };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var typeLabel = window.GetVisualDescendants()
                .OfType<SafeSelectableTextBlock>()
                .FirstOrDefault(t => t.Text is { } text
                    && text.Contains("tool", StringComparison.Ordinal)
                    && text.Contains("note", StringComparison.Ordinal));

            Assert.NotNull(typeLabel);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardControl_SearchQuerySetAfterRealize_DisplayNameRendersHighlightedRun()
    {
        var vm = new EntityCardViewModel(displayName: "the foo bar", entityType: "note");
        var card = new EntityCardControl { DataContext = vm };
        var window = new Window { Content = card };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            vm.SearchQuery = "foo";
            Dispatcher.UIThread.RunJobs();

            var title = window.GetVisualDescendants()
                .OfType<SafeSelectableTextBlock>()
                .First(t => t.Classes.Contains("workspace-entity-title"));

            var highlighted = title.Inlines!
                .OfType<Avalonia.Controls.Documents.Run>()
                .Where(r => r.Background is not null)
                .ToArray();
            Assert.Single(highlighted);
            Assert.Equal("foo", highlighted[0].Text);
            Assert.Same(title.HighlightBrush, highlighted[0].Background);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardControl_SearchQueryEmpty_DisplayNameKeepsPlainText()
    {
        var vm = new EntityCardViewModel(displayName: "the foo bar", entityType: "note");
        var card = new EntityCardControl { DataContext = vm };
        var window = new Window { Content = card };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            vm.SearchQuery = null;
            Dispatcher.UIThread.RunJobs();

            var title = window.GetVisualDescendants()
                .OfType<SafeSelectableTextBlock>()
                .First(t => t.Classes.Contains("workspace-entity-title"));

            Assert.Equal("the foo bar", title.Text);
            Assert.DoesNotContain(title.Inlines!.OfType<Avalonia.Controls.Documents.Run>(),
                r => r.Background is not null);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardControl_SearchQueryNoMatch_DisplayNameKeepsPlainText()
    {
        var vm = new EntityCardViewModel(displayName: "the foo bar", entityType: "note");
        var card = new EntityCardControl { DataContext = vm };
        var window = new Window { Content = card };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            vm.SearchQuery = "zzz";
            Dispatcher.UIThread.RunJobs();

            var title = window.GetVisualDescendants()
                .OfType<SafeSelectableTextBlock>()
                .First(t => t.Classes.Contains("workspace-entity-title"));

            Assert.Equal("the foo bar", title.Text);
            Assert.False(vm.Matches);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardControl_SearchQueryChangedAfterRealize_HighlightUpdatesToNewQuery()
    {
        var vm = new EntityCardViewModel(displayName: "foo and bar", entityType: "note");
        var card = new EntityCardControl { DataContext = vm };
        var window = new Window { Content = card };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var title = window.GetVisualDescendants()
                .OfType<SafeSelectableTextBlock>()
                .First(t => t.Classes.Contains("workspace-entity-title"));

            vm.SearchQuery = "foo";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("foo", title.Inlines!.OfType<Avalonia.Controls.Documents.Run>()
                .Single(r => r.Background is not null).Text);

            vm.SearchQuery = "bar";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("bar", title.Inlines!.OfType<Avalonia.Controls.Documents.Run>()
                .Single(r => r.Background is not null).Text);
        }
        finally
        {
            window.Close();
        }
    }

    // Issue #1257: a property VALUE whose text matches the active search query must render a
    // highlighted Run in the field-value SafeSelectableTextBlock. This is the new highlight surface
    // introduced by binding SearchQuery to the field-value presentation control.
    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardControl_SearchQueryMatchesPropertyValue_PropertyValueTextRendersHighlightedRun()
    {
        var entity = new SubscribedEntityViewModel(BuildGitWorktreeSnapshotForTests());
        var fieldEditors = new EntityFieldEditorViewModel[]
        {
            new StringFieldEditorViewModel("path", "the foo bar"),
        };
        var vm = new EntityCardViewModel(entity, fieldEditors);
        var card = new EntityCardControl { DataContext = vm };
        var window = new Window { Content = card, Width = 400, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var value = window.GetVisualDescendants()
                .OfType<SafeSelectableTextBlock>()
                .First(t => t.Classes.Contains("workspace-field-read-value"));
            Assert.Equal("the foo bar", value.Text);

            vm.SearchQuery = "foo";
            Dispatcher.UIThread.RunJobs();

            var highlighted = value.Inlines!
                .OfType<Avalonia.Controls.Documents.Run>()
                .Where(r => r.Background is not null)
                .ToArray();
            Assert.Single(highlighted);
            Assert.Equal("foo", highlighted[0].Text);
            Assert.Same(value.HighlightBrush, highlighted[0].Background);
        }
        finally
        {
            window.Close();
        }
    }

    // Issue #1257: a property NAME label must never be highlighted, even when the query is a
    // substring of the field name. The field-name label control does not bind SearchQuery, so it
    // stays a single plain Run regardless of the query.
    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardControl_SearchQueryMatchesPropertyName_PropertyNameLabelRendersSinglePlainRun()
    {
        var entity = new SubscribedEntityViewModel(BuildGitWorktreeSnapshotForTests());
        var fieldEditors = new EntityFieldEditorViewModel[]
        {
            new StringFieldEditorViewModel("path", "xyz"),
        };
        var vm = new EntityCardViewModel(entity, fieldEditors);
        var card = new EntityCardControl { DataContext = vm };
        var window = new Window { Content = card, Width = 400, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // "pat" is a substring of the field name "path" but not of any value/display name.
            vm.SearchQuery = "pat";
            Dispatcher.UIThread.RunJobs();

            var label = window.GetVisualDescendants()
                .OfType<SafeSelectableTextBlock>()
                .First(t => t.Classes.Contains("workspace-field-label") && t.Text == "path");

            // The label control never binds SearchQuery, so it stays plain text with no highlight.
            Assert.Null(label.SearchQuery);
            Assert.Equal("path", label.Text);
            Assert.DoesNotContain(
                label.Inlines!.OfType<Avalonia.Controls.Documents.Run>(),
                r => r.Background is not null);
        }
        finally
        {
            window.Close();
        }
    }

    // Issue #1257 (interaction with #1177 virtualization): when SearchQuery is assigned on the card
    // view model while its container is virtualized (not realized), realizing the container — here,
    // attaching a freshly-created EntityCardControl bound to that same view model — must apply the
    // highlight on realize rather than only responding to post-realize query changes.
    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardControl_VirtualizedItemRealizedAfterSearchQuerySet_ShowsHighlightOnRealize()
    {
        var vm = new EntityCardViewModel(displayName: "the foo bar", entityType: "note");

        // Query set before any control is realized against this view model.
        vm.SearchQuery = "foo";

        var card = new EntityCardControl { DataContext = vm };
        var window = new Window { Content = card };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var title = window.GetVisualDescendants()
                .OfType<SafeSelectableTextBlock>()
                .First(t => t.Classes.Contains("workspace-entity-title"));

            var highlighted = title.Inlines!
                .OfType<Avalonia.Controls.Documents.Run>()
                .Where(r => r.Background is not null)
                .ToArray();
            Assert.Single(highlighted);
            Assert.Equal("foo", highlighted[0].Text);
            Assert.Same(title.HighlightBrush, highlighted[0].Background);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardControl_FieldReadMode_ValueIsSafeSelectableTextBlock()
    {
        var entity = new SubscribedEntityViewModel(BuildToolNoteSnapshotForTests());
        var fieldEditors = new EntityFieldEditorViewModel[]
        {
            new StringFieldEditorViewModel("path", "/home/user/worktrees/9"),
        };
        var card = new EntityCardControl { DataContext = new EntityCardViewModel(entity, fieldEditors) };
        var window = new Window { Content = card, Width = 400, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var readValue = window.GetVisualDescendants()
                .OfType<SafeSelectableTextBlock>()
                .FirstOrDefault(t => t.Classes.Contains("workspace-field-read-value"));
            Assert.NotNull(readValue);
            Assert.Equal("/home/user/worktrees/9", readValue!.Text);

            // The field label is copyable too, and no read value remains a plain TextBlock.
            var plainReadValues = window.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(t => t is not SafeSelectableTextBlock && t.Classes.Contains("workspace-field-read-value"))
                .ToArray();
            Assert.Empty(plainReadValues);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityCardControl_FieldLabels_AreSafeSelectableTextBlock()
    {
        var card = new EntityCardControl { DataContext = await BuildToolNoteCardViewModelAsync() };
        var window = new Window { Content = card };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var labels = window.GetVisualDescendants()
                .OfType<SafeSelectableTextBlock>()
                .Where(t => t.Classes.Contains("workspace-field-label"))
                .ToArray();

            Assert.NotEmpty(labels);
            // No field label may remain a plain (non-selectable) TextBlock.
            var plainLabels = window.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(t => t is not SafeSelectableTextBlock && t.Classes.Contains("workspace-field-label"))
                .ToArray();
            Assert.Empty(plainLabels);
        }
        finally
        {
            window.Close();
        }
    }

    // Issue #1214: for a git-worktree entity card, every rendered property value element
    // (path / branch / head-commit / target-branch) must be a copyable SafeSelectableTextBlock.
    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardControl_GitWorktree_AllPropertyValuesAreCopyable()
    {
        var entity = new SubscribedEntityViewModel(BuildGitWorktreeSnapshotForTests());
        var fieldEditors = new EntityFieldEditorViewModel[]
        {
            new StringFieldEditorViewModel("path", "/home/user/worktrees/9"),
            new StringFieldEditorViewModel("branch", "feature/wrap-fix"),
            new StringFieldEditorViewModel("head-commit", "a1b2c3d4e5f6"),
            new StringFieldEditorViewModel("target-branch", "main"),
        };
        var card = new EntityCardControl { DataContext = new EntityCardViewModel(entity, fieldEditors) };
        var window = new Window { Content = card, Width = 500, Height = 500 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var readValueTexts = window.GetVisualDescendants()
                .OfType<SafeSelectableTextBlock>()
                .Where(t => t.Classes.Contains("workspace-field-read-value"))
                .Select(t => t.Text)
                .ToArray();

            foreach (var expected in new[] { "/home/user/worktrees/9", "feature/wrap-fix", "a1b2c3d4e5f6", "main" })
            {
                Assert.Contains(expected, readValueTexts);
            }

            // No property value may remain a plain (non-copyable) TextBlock.
            var plainReadValues = window.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(t => t is not SafeSelectableTextBlock && t.Classes.Contains("workspace-field-read-value"))
                .ToArray();
            Assert.Empty(plainReadValues);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityCardControl_GitWorktree_MouseSelectionAndCtrlC_CopiesEveryTextItem()
    {
        const string path = @"C:\repos\Phantom Workspaces\worktrees\20";
        const string sha = "a1b2c3d4e5f678901234567890abcdef12345678";
        var entity = new SubscribedEntityViewModel(BuildGitWorktreeSnapshotForTests());
        var fields = new EntityFieldEditorViewModel[]
        {
            new StringFieldEditorViewModel("path", path),
            new StringFieldEditorViewModel("branch", "fix/1624-copyable-card-text"),
            new StringFieldEditorViewModel("head-commit", sha),
            new StringFieldEditorViewModel("target-branch", "features"),
        };
        var vm = new EntityCardViewModel(entity, fields);
        var card = new NavigationSpyCard { DataContext = vm };
        var tree = new TreeView();
        tree.Classes.Add("entity-card-tree");
        tree.Items.Add(new TreeViewItem { Header = card });
        var window = new Window { Content = tree, Width = 900, Height = 600 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var textItems = window.GetVisualDescendants().OfType<SafeSelectableTextBlock>()
                .Where(t => t.IsEffectivelyVisible &&
                    (t.Classes.Contains("workspace-entity-title") ||
                     t.Classes.Contains("muted") && !t.Classes.Contains("workspace-field-label") ||
                     t.Classes.Contains("workspace-field-label") ||
                     t.Classes.Contains("workspace-field-read-value")))
                .ToArray();
            Assert.Contains(textItems, t => t.Text == path);
            Assert.Contains(textItems, t => t.Text == sha);
            Assert.Contains(textItems, t => t.Text == "head-commit");

            foreach (var item in textItems)
            {
                var expected = item.Text;
                if (string.IsNullOrEmpty(expected))
                    continue;

                var origin = item.TranslatePoint(new Point(0, 0), window)!.Value;
                var y = origin.Y + Math.Min(item.Bounds.Height / 2, 8);
                window.MouseDown(new Point(origin.X + 1, y), MouseButton.Left);
                window.MouseUp(new Point(origin.X + 1, y), MouseButton.Left);
                Assert.Equal(0, card.ActivationCount);
                window.MouseDown(new Point(origin.X + 1, y), MouseButton.Left);
                window.MouseMove(new Point(origin.X + item.Bounds.Width + 10, y), RawInputModifiers.LeftMouseButton);
                window.MouseUp(new Point(origin.X + item.Bounds.Width + 10, y), MouseButton.Left);
                Assert.NotEmpty(item.SelectedText);
                Assert.Equal(0, card.ActivationCount);
                item.SelectAll();
                window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
                using var clipboardData = await window.Clipboard!.TryGetDataAsync();
                Assert.NotNull(clipboardData);
                Assert.Equal(expected, await clipboardData.TryGetTextAsync());
            }

            vm.SearchQuery = "worktree";
            Dispatcher.UIThread.RunJobs();
            var title = textItems.Single(t => t.Classes.Contains("workspace-entity-title"));
            Assert.Contains(title.Inlines!.OfType<Avalonia.Controls.Documents.Run>(),
                run => run.Background is not null);
            title.Focus();
            title.SelectAll();
            window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
            using (var highlightedCopy = await window.Clipboard!.TryGetDataAsync())
            {
                Assert.NotNull(highlightedCopy);
                Assert.Equal("worktree, system-defined", await highlightedCopy.TryGetTextAsync());
            }

            var root = card.GetVisualDescendants().OfType<StackPanel>()
                .Single(panel => panel.Classes.Contains("workspace-entity-card-content"));
            var rootOrigin = root.TranslatePoint(new Point(0, 0), window)!.Value;
            var emptySpace = new Point(rootOrigin.X + root.Bounds.Width - 3,
                rootOrigin.Y + root.Bounds.Height - 3);
            window.MouseDown(emptySpace, MouseButton.Left);
            window.MouseUp(emptySpace, MouseButton.Left);
            Assert.Equal(1, card.ActivationCount);

        }
        finally
        {
            window.Close();
        }
    }

    private sealed class NavigationSpyCard : EntityCardControl
    {
        public int ActivationCount { get; private set; }

        internal override void ActivateCard() => this.ActivationCount++;
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityCardControl_ShortcutAndJsonButtonLabels_SelectCopyAndActivateOnce()
    {
        var entity = new SubscribedEntityViewModel(BuildGitWorktreeSnapshotForTests());
        var vm = new EntityCardViewModel(entity);
        var shortcut = new EntityShortcutViewModel
        {
            Shortcut = new Shortcut("Go", "Go"),
            Entity = entity,
            ShortcutManager = new ShortcutManager(),
        };
        object? invokedWith = null;
        var calls = 0;
        vm.SetShortcuts(new[] { shortcut }, new RelayCommand(parameter =>
        {
            invokedWith = parameter;
            calls++;
        }));
        var card = new NavigationSpyCard { DataContext = vm };
        var window = new Window { Content = card, Width = 500, Height = 250 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var labels = window.GetVisualDescendants().OfType<CopyableLinkTextBlock>().ToArray();
            var shortcutLabel = Assert.Single(labels, t => t.Text == "Go");
            var jsonLabel = Assert.Single(labels, t => t.Text == "{}");

            ClickText(window, shortcutLabel);
            Assert.Equal(1, calls);
            Assert.Same(shortcut, invokedWith);
            ClickText(window, jsonLabel);
            Assert.True(vm.ShowRawJsonEditor);
            Assert.Equal(0, card.ActivationCount);

            await AssertMouseSelectAndCopyAsync(window, shortcutLabel);
            Assert.Equal(1, calls);
            await AssertMouseSelectAndCopyAsync(window, jsonLabel);
            Assert.True(vm.ShowRawJsonEditor);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityCardControl_EditActionLabels_CopyAndRetainButtonCommands()
    {
        var vm = new EntityCardViewModel("Editable", "note",
            new EntityFieldEditorViewModel[] { new StringFieldEditorViewModel("name", "value") });
        var card = new NavigationSpyCard { DataContext = vm };
        var window = new Window { Content = card, Width = 500, Height = 260 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var edit = window.GetVisualDescendants().OfType<CopyableLinkTextBlock>()
                .Single(t => t.Text == "✎");
            await AssertMouseSelectAndCopyAsync(window, edit);
            Assert.False(vm.IsEditMode);
            edit.ClearSelection();
            edit.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            Assert.True(vm.IsEditMode);
            await vm.Validation.UpdateAsync("{}");
            vm.SaveEditModeCommand.RaiseCanExecuteChanged();
            Dispatcher.UIThread.RunJobs();

            var actions = window.GetVisualDescendants().OfType<CopyableLinkTextBlock>()
                .Where(t => t.IsEffectivelyVisible && (t.Text is "💾" or "✖")).ToArray();
            Assert.Equal(2, actions.Length);
            foreach (var action in actions)
            {
                Assert.True(action.IsEffectivelyEnabled, $"Action {action.Text} is disabled");
                await AssertMouseSelectAndCopyAsync(window, action);
                Assert.True(vm.IsEditMode);
            }

            var discard = actions.Single(t => t.Text == "✖");
            discard.ClearSelection();
            discard.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            Assert.False(vm.IsEditMode);
            Assert.Equal(0, card.ActivationCount);
        }
        finally
        {
            window.Close();
        }
    }

    private static void ClickText(Window window, SafeSelectableTextBlock item)
    {
        var origin = item.TranslatePoint(new Point(0, 0), window)!.Value;
        var point = new Point(origin.X + Math.Min(2, item.Bounds.Width / 2),
            origin.Y + Math.Min(8, item.Bounds.Height / 2));
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    private static async Task AssertMouseSelectAndCopyAsync(Window window, SafeSelectableTextBlock item)
    {
        var origin = item.TranslatePoint(new Point(0, 0), window)!.Value;
        var start = new Point(origin.X + 1, origin.Y + Math.Min(8, item.Bounds.Height / 2));
        var end = new Point(origin.X + item.Bounds.Width + 2, start.Y);
        window.MouseMove(start);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        window.MouseUp(end, MouseButton.Left);
        Assert.NotEmpty(item.SelectedText);
        item.Focus();
        item.SelectAll();
        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        using var data = await window.Clipboard!.TryGetDataAsync();
        Assert.NotNull(data);
        Assert.Equal(item.Text, await data.TryGetTextAsync());
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityCardControl_EntityListLink_MouseCopiesAndClickOpens()
    {
        const string id = "a1b2c3d4-e5f6-4123-8123-123456789abc";
        string? opened = null;
        var editor = new EntityListFieldEditorViewModel(
            "related", new[] { id }, Array.Empty<string>(), null, value => opened = value);
        editor.Items[0].DisplayName = "Referenced worktree";
        var card = new NavigationSpyCard
        {
            DataContext = new EntityCardViewModel(
                new SubscribedEntityViewModel(BuildGitWorktreeSnapshotForTests()),
                new EntityFieldEditorViewModel[] { editor }),
        };
        var window = new Window { Content = card, Width = 550, Height = 300 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var link = window.GetVisualDescendants().OfType<CopyableLinkTextBlock>()
                .Single(t => t.Text == "Referenced worktree");
            ClickText(window, link);
            Assert.Equal(id, opened);
            opened = null;
            await AssertMouseSelectAndCopyAsync(window, link);
            Assert.Null(opened);
            Assert.Equal(0, card.ActivationCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityCardControl_ArrayAndObjectHeaders_CopyWithoutTogglingExpanders()
    {
        var editors = new EntityFieldEditorViewModel[]
        {
            new ArrayFieldEditorViewModel("array-items", Array.Empty<EntityFieldEditorViewModel>()),
            new ObjectFieldEditorViewModel("object-info", Array.Empty<EntityFieldEditorViewModel>()),
        };
        var card = new NavigationSpyCard
        {
            DataContext = new EntityCardViewModel(
                new SubscribedEntityViewModel(BuildGitWorktreeSnapshotForTests()), editors),
        };
        var window = new Window { Content = card, Width = 500, Height = 350 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var expanders = window.GetVisualDescendants().OfType<Expander>()
                .Where(e => e.Classes.Contains("workspace-field-expander")).ToArray();
            Assert.Equal(2, expanders.Length);
            foreach (var expander in expanders)
            {
                var label = expander.GetVisualDescendants().OfType<SafeSelectableTextBlock>()
                    .Single(t => t.Text is "array-items" or "object-info");
                await AssertMouseSelectAndCopyAsync(window, label);
                Assert.False(expander.IsExpanded);
            }
            Assert.Equal(0, card.ActivationCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task EntityCardControl_MarkdownAttachment_HeaderAndRenderedContent_CopyWithMouse()
    {
        const string body = "Selectable markdown content";
        var editor = new MarkdownMimeAttachmentFieldEditorViewModel(
            "notes", "text/markdown", body, null);
        var card = new NavigationSpyCard
        {
            DataContext = new EntityCardViewModel(
                new SubscribedEntityViewModel(BuildGitWorktreeSnapshotForTests()),
                new EntityFieldEditorViewModel[] { editor }),
        };
        var window = new Window { Content = card, Width = 600, Height = 420 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var expander = window.GetVisualDescendants().OfType<Expander>()
                .Single(e => e.Classes.Contains("workspace-field-expander"));
            var header = expander.GetVisualDescendants().OfType<SafeSelectableTextBlock>()
                .Single(t => t.Text == "notes");
            await AssertMouseSelectAndCopyAsync(window, header);
            Assert.False(expander.IsExpanded);
            expander.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();

            var markdown = expander.GetVisualDescendants().OfType<WorkspaceMarkdownView>()
                .Single(view => view.IsEffectivelyVisible && view.Markdown == body);
            Assert.True(markdown.SelectionEnabled);
            var text = markdown.GetVisualDescendants().OfType<Control>()
                .First(t => t.GetType().Name == "CTextBlock" && t.Bounds.Width > 0);
            var origin = text.TranslatePoint(new Point(0, 0), window)!.Value;
            var start = new Point(origin.X + 2, origin.Y + Math.Min(8, text.Bounds.Height / 2));
            var end = new Point(origin.X + text.Bounds.Width - 3, start.Y);
            window.MouseMove(start);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, MouseButton.Left);
            window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
            using var data = await window.Clipboard!.TryGetDataAsync();
            Assert.NotNull(data);
            Assert.Equal(body, (await data.TryGetTextAsync())?.TrimEnd('\r', '\n'));
            Assert.Equal(0, card.ActivationCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityCardControl_ReferenceSearchCandidate_MouseCopiesAndClickSelects()
    {
        const string id = "a1b2c3d4-e5f6-4123-8123-123456789abc";
        var editor = new EntityReferenceFieldEditorViewModel("related", null, Array.Empty<string>(),
            new CandidateSearch(new EntityReferenceCandidate(id, "Candidate worktree", "worktrees")));
        var vm = new EntityCardViewModel(
            new SubscribedEntityViewModel(BuildGitWorktreeSnapshotForTests()),
            new EntityFieldEditorViewModel[] { editor });
        vm.IsEditMode = true;
        editor.SearchText = "worktree";
        await editor.SearchAsync();
        var card = new NavigationSpyCard { DataContext = vm };
        var window = new Window { Content = card, Width = 550, Height = 350 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var button = window.GetVisualDescendants().OfType<Button>()
                .Single(b => b.Classes.Contains("workspace-entity-reference-candidate"));
            var label = button.GetVisualDescendants().OfType<CopyableLinkTextBlock>()
                .Single(t => t.Text == "Candidate worktree");
            await AssertMouseSelectAndCopyAsync(window, label);
            Assert.Equal(string.Empty, editor.Value);
            label.ClearSelection();
            label.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            Assert.Equal(id, editor.Value);
            Assert.Equal(0, card.ActivationCount);
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class CandidateSearch(EntityReferenceCandidate candidate) : IEntityReferenceSearch
    {
        public Task<IReadOnlyList<EntityReferenceCandidate>> SearchAsync(
            string searchText, System.Collections.Generic.IReadOnlyCollection<string> entityTypes,
            System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<EntityReferenceCandidate>>(new[] { candidate });

        public Task<EntityReferenceCandidate?> ResolveAsync(
            string entityId, System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult<EntityReferenceCandidate?>(candidate);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityCardControl_ReferenceLink_DragCopiesText_ClickNavigates()
    {
        const string id = "a1b2c3d4-e5f6-4123-8123-123456789abc";
        string? opened = null;
        var editor = new EntityReferenceFieldEditorViewModel("related", id, Array.Empty<string>(), null,
            value => opened = value);
        var entity = new SubscribedEntityViewModel(BuildGitWorktreeSnapshotForTests());
        var card = new NavigationSpyCard
        {
            DataContext = new EntityCardViewModel(entity, new EntityFieldEditorViewModel[] { editor }),
        };
        var window = new Window { Content = card, Width = 500, Height = 300 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var link = window.GetVisualDescendants().OfType<CopyableLinkTextBlock>()
                .Single(t => t.Classes.Contains("workspace-entity-reference-link"));
            Assert.Equal(id, link.Text);
            Assert.True(link.Command!.CanExecute(null));
            var origin = link.TranslatePoint(new Point(0, 0), window)!.Value;
            var point = new Point(origin.X + 2, origin.Y + link.Bounds.Height / 2);

            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Assert.Equal(id, opened);
            Assert.Equal(0, card.ActivationCount);
            opened = null;

            window.MouseDown(point, MouseButton.Left);
            window.MouseMove(new Point(point.X + link.Bounds.Width, point.Y), RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(point.X + link.Bounds.Width, point.Y), MouseButton.Left);
            Assert.NotEmpty(link.SelectedText);
            Assert.Null(opened);
            link.SelectAll();
            window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
            using (var data = await window.Clipboard!.TryGetDataAsync())
            {
                Assert.NotNull(data);
                Assert.Equal(id, await data.TryGetTextAsync());
            }

            Assert.Null(opened);
            link.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            Assert.Equal(id, opened);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityCardControl_ExternalUrlAndKey_CanBeSelectedAndCopied()
    {
        const string url = "https://example.com/full/path?query=value";
        using var document = JsonDocument.Parse(
            """{"entity-id":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","entity-types":["entity","external"],"names":[["externals","link"]],"display-name":{"default":"Link"},"urls":{"docs":"https://example.com/full/path?query=value"}}""");
        var snapshot = new EntitySnapshot
        {
            EntityId = new EntityId(document.RootElement.GetProperty("entity-id").GetString()!),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = document.RootElement.Clone(),
            Relationships = Array.Empty<EntitySnapshot>(),
        };
        var card = new NavigationSpyCard
        {
            DataContext = new EntityCardViewModel(new SubscribedEntityViewModel(snapshot), cardViewName: "external"),
        };
        var window = new Window { Content = card, Width = 220, Height = 300 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var link = window.GetVisualDescendants().OfType<CopyableLinkTextBlock>()
                .Single(t => t.Classes.Contains("workspace-url-link"));
            var key = window.GetVisualDescendants().OfType<SafeSelectableTextBlock>()
                .Single(t => t.Text == "docs");
            Assert.Equal(url, link.Text);
            Assert.True(link.TextLayout.TextLines.Count > 1, "Long URLs must wrap without truncating the copied text.");
            Assert.NotNull(link.Command);
            var opened = 0;
            // Replace the shell-launching command, not the rendered link, so activation is
            // exercised without opening a real browser during the headless test.
            link.Command = new RelayCommand(_ => opened++);
            ClickText(window, link);
            Assert.Equal(1, opened);
            foreach (var item in new SafeSelectableTextBlock[] { key, link })
            {
                var origin = item.TranslatePoint(new Point(0, 0), window)!.Value;
                var point = new Point(origin.X + 1, origin.Y + Math.Min(item.Bounds.Height / 2, 8));
                window.MouseDown(point, MouseButton.Left);
                window.MouseMove(new Point(point.X + item.Bounds.Width, point.Y), RawInputModifiers.LeftMouseButton);
                window.MouseUp(new Point(point.X + item.Bounds.Width, point.Y), MouseButton.Left);
                Assert.True(!string.IsNullOrEmpty(item.SelectedText),
                    $"Could not select {item.Text} at {point} (bounds {item.Bounds}).");
                item.SelectAll();
                window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
                using var data = await window.Clipboard!.TryGetDataAsync();
                Assert.NotNull(data);
                Assert.Equal(item.Text, await data.TryGetTextAsync());
            }
            Assert.Equal(1, opened);
            Assert.Equal(0, card.ActivationCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityCardControl_MimeExpanderHeaderAndUrl_CopyWithoutOpeningOrCollapsing()
    {
        const string url = "https://example.com/attachment/full/path";
        var entity = new SubscribedEntityViewModel(BuildGitWorktreeSnapshotForTests());
        var editor = new PlainMimeAttachmentFieldEditorViewModel(
            "attachment", "text/plain", "body", url);
        var card = new NavigationSpyCard
        {
            DataContext = new EntityCardViewModel(entity, new EntityFieldEditorViewModel[] { editor }),
        };
        var window = new Window { Content = card, Width = 600, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var expander = window.GetVisualDescendants().OfType<Expander>()
                .Single(e => e.Classes.Contains("workspace-field-expander"));
            expander.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            var values = window.GetVisualDescendants().OfType<SafeSelectableTextBlock>().ToArray();
            var header = Assert.Single(values, t => t.Text == "attachment");
            var urlText = Assert.Single(values, t => t.Text == url);
            foreach (var item in new[] { header, urlText })
            {
                var origin = item.TranslatePoint(new Point(0, 0), window)!.Value;
                var point = new Point(origin.X + 1, origin.Y + item.Bounds.Height / 2);
                window.MouseDown(point, MouseButton.Left);
                window.MouseMove(new Point(point.X + item.Bounds.Width, point.Y), RawInputModifiers.LeftMouseButton);
                window.MouseUp(new Point(point.X + item.Bounds.Width, point.Y), MouseButton.Left);
                Assert.NotEmpty(item.SelectedText);
                item.SelectAll();
                window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
                using var data = await window.Clipboard!.TryGetDataAsync();
                Assert.NotNull(data);
                Assert.Equal(item.Text, await data.TryGetTextAsync());
            }
            Assert.True(expander.IsExpanded);
            Assert.Equal(0, card.ActivationCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityCardControl_StatusBadge_MouseSelectionAndCtrlC_CopiesStatus()
    {
        var vm = new EntityCardViewModel(displayName: "Task", entityType: "task");
        var badges = new StatusBadgesModel();
        badges.SetBadges(new[] { new StatusBadgeModel("completed", "Theme.Status.Good", "status: completed") });
        vm.SetStatusBadges(new StatusBadgesViewModel(badges));
        var card = new NavigationSpyCard { DataContext = vm };
        var window = new Window { Content = card, Width = 400, Height = 200 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var badge = window.GetVisualDescendants().OfType<SafeSelectableTextBlock>()
                .Single(t => t.Classes.Contains("status-badge-text"));
            var origin = badge.TranslatePoint(new Point(0, 0), window)!.Value;
            var point = new Point(origin.X + 1, origin.Y + badge.Bounds.Height / 2);
            window.MouseDown(point, MouseButton.Left);
            window.MouseMove(new Point(point.X + badge.Bounds.Width, point.Y), RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(point.X + badge.Bounds.Width, point.Y), MouseButton.Left);
            Assert.NotEmpty(badge.SelectedText);
            badge.SelectAll();
            window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
            using var data = await window.Clipboard!.TryGetDataAsync();
            Assert.NotNull(data);
            Assert.Equal("completed", await data.TryGetTextAsync());
            Assert.Equal(0, card.ActivationCount);
        }
        finally
        {
            window.Close();
        }
    }

    // Issue #1214: regression guard for #1006/#1177 — header-row selectable text blocks still wrap
    // (word-level, not character-clipped) when the row is constrained narrow after the swap to
    // SafeSelectableTextBlock.
    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityCardControl_HeaderRow_TextBlocksWrapWhenNarrow()
    {
        var card = new EntityCardControl { DataContext = await BuildToolNoteCardViewModelAsync() };
        var window = new Window { Content = card, Width = 90, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var title = window.GetVisualDescendants()
                .OfType<SafeSelectableTextBlock>()
                .First(t => t.Classes.Contains("workspace-entity-title"));

            Assert.Equal(Avalonia.Media.TextWrapping.Wrap, title.TextWrapping);
            Assert.True(
                title.TextLayout.TextLines.Count >= 2,
                $"Header-row title should wrap to multiple lines when narrow; got {title.TextLayout.TextLines.Count} line(s).");
        }
        finally
        {
            window.Close();
        }
    }

    // Issue #1214: regression guard — the read-mode field value SafeSelectableTextBlock still wraps
    // under the workspace-field-read-value style when the value column is narrow.
    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardControl_FieldReadValue_WrapsWhenNarrow()
    {
        var entity = new SubscribedEntityViewModel(BuildToolNoteSnapshotForTests());
        var fieldEditors = new EntityFieldEditorViewModel[]
        {
            new StringFieldEditorViewModel(
                "path",
                "the quick brown fox jumps over the lazy dog several times over again here"),
        };
        var card = new EntityCardControl { DataContext = new EntityCardViewModel(entity, fieldEditors) };
        var window = new Window { Content = card, Width = 160, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var readValue = window.GetVisualDescendants()
                .OfType<SafeSelectableTextBlock>()
                .First(t => t.Classes.Contains("workspace-field-read-value"));

            Assert.Equal(Avalonia.Media.TextWrapping.Wrap, readValue.TextWrapping);
            Assert.True(
                readValue.TextLayout.TextLines.Count >= 2,
                $"Read-mode field value should wrap to multiple lines when narrow; got {readValue.TextLayout.TextLines.Count} line(s).");
        }
        finally
        {
            window.Close();
        }
    }

    // Issue #1213: rendering EntityCardControl at a narrow width must not character-clip the display
    // name — the header wrap layout reflows the name across multiple lines at word boundaries and
    // preserves every character.
    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityCardControl_HeaderMeasuredNarrow_DoesNotClipDisplayName()
    {
        var card = new EntityCardControl { DataContext = await BuildToolNoteCardViewModelAsync() };
        var window = new Window { Content = card, Width = 90, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var title = window.GetVisualDescendants()
                .OfType<SafeSelectableTextBlock>()
                .First(t => t.Classes.Contains("workspace-entity-title"));

            // Word-boundary wrapping (not character clipping): the title uses TextWrapping=Wrap and
            // reflows the display name across multiple lines rather than truncating it.
            Assert.Equal(Avalonia.Media.TextWrapping.Wrap, title.TextWrapping);
            var lines = title.TextLayout.TextLines;
            Assert.True(lines.Count >= 2, $"Expected wrapped display name, got {lines.Count} line(s).");

            // Every character of the display name remains present across the wrapped lines.
            var renderedLength = lines.Sum(l => l.Length);
            var displayName = title.Text ?? string.Empty;
            Assert.True(
                renderedLength >= displayName.Length,
                $"Rendered {renderedLength} chars < display name {displayName.Length}; text was clipped.");
        }
        finally
        {
            window.Close();
        }
    }

    // Issue #1266 (retry): rendered-bounds coverage for the shared entity-card action-button style.
    // The headless test App applies SharedStyles globally, so a Button carrying the shared class
    // receives the shipped 28x28 footprint after a real layout pass.
    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardControl_ActionButtons_ShareUniformSize()
    {
        var buttons = new[] { "🔧", "{}", "💾", "✖", "A" }
            .Select(glyph =>
            {
                var button = new Button { Content = glyph };
                button.Classes.Add("entity-card-action-button");
                return button;
            })
            .ToArray();

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in buttons)
        {
            panel.Children.Add(button);
        }

        var window = new Window { Content = panel, Width = 400, Height = 200 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            foreach (var button in buttons)
            {
                Assert.Equal(28, button.Bounds.Width, precision: 1);
                Assert.Equal(28, button.Bounds.Height, precision: 1);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardActionButton_HighlightedVariant_KeepsSameFootprint()
    {
        var baseButton = new Button { Content = "A" };
        baseButton.Classes.Add("entity-card-action-button");

        var highlightedButton = new Button { Content = "A" };
        highlightedButton.Classes.Add("entity-card-action-button");
        highlightedButton.Classes.Add("highlighted");

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(baseButton);
        panel.Children.Add(highlightedButton);

        var window = new Window { Content = panel, Width = 400, Height = 200 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // The highlighted variant only changes visual accents — never the footprint.
            Assert.Equal(baseButton.Bounds.Width, highlightedButton.Bounds.Width, precision: 1);
            Assert.Equal(baseButton.Bounds.Height, highlightedButton.Bounds.Height, precision: 1);
            Assert.Equal(28, highlightedButton.Bounds.Width, precision: 1);
            Assert.Equal(28, highlightedButton.Bounds.Height, precision: 1);
        }
        finally
        {
            window.Close();
        }
    }

    private static EntitySnapshot BuildGitWorktreeSnapshotForTests()
    {
        var entityId = Guid.NewGuid();
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "git-worktree"],
              "names": [["git-worktrees", "wt-{{entityId:N}}"]],
              "display-name": { "default": "worktree, system-defined" }
            }
            """);
        return new EntitySnapshot
        {
            EntityId = new EntityId(entityId),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = document.RootElement.Clone(),
            Relationships = Array.Empty<EntitySnapshot>(),
        };
    }

    private static async Task<EntityCardViewModel> BuildToolNoteCardViewModelAsync()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "e4f5a6b7-c8d9-4e0f-b1c2-d3e4f5a6b7c8",
              "entity-types": ["entity", "tool", "note"],
              "names": [["tools", "run-vs-code-tunnel"]],
              "display-name": { "default": "Run VS Code Tunnel" },
              "content": {
                "default": {
                  "mime-type": "text/markdown",
                  "content": { "text": "# Run VS Code Tunnel\n\nConfiguration and Usage." }
                }
              }
            }
            """);
        var entityData = document.RootElement.Clone();
        var snapshot = new EntitySnapshot
        {
            EntityId = new EntityId("e4f5a6b7-c8d9-4e0f-b1c2-d3e4f5a6b7c8"),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = entityData,
            Relationships = Array.Empty<EntitySnapshot>(),
        };
        var entity = new SubscribedEntityViewModel(snapshot);

        // Bug #1182: EntityCardViewModel.BuildFieldEditorsAsync no-ops without a factory, so the
        // note's markdown WorkspaceMarkdownView never appears. Build the field editors with a real
        // FieldEditorFactory across every non-abstract entity type (mirrors production) — matches
        // the harness used by EntityCardFieldBuildingTests.MarkdownMimeAttachment_ReadMode_RendersMarkdownNotRawSource.
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);
        var entityTypeViewCatalog = await EntityTypeViewCatalog.CreateAsync(broker);
        var factory = new FieldEditorFactory(broker, entityTypeViewCatalog);
        var fieldEditors = await factory.BuildFieldEditorsAsync(entityData, entity.NonAbstractEntityTypeNames);

        return new EntityCardViewModel(entity, fieldEditors);
    }

    // Issue #1177: attaching an EntityCardControl to the visual tree triggers the card's lazy
    // field-editor build; a detached card (never attached) does not build editors.
    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityCardControl_OnAttachedToVisualTree_TriggersFieldEditorBuild()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);
        var entityTypeViewCatalog = await EntityTypeViewCatalog.CreateAsync(broker);
        var factory = new FieldEditorFactory(broker, entityTypeViewCatalog);

        var entity = new SubscribedEntityViewModel(BuildToolNoteSnapshotForTests());
        var attachedCard = new EntityCardViewModel(entity, fieldEditorFactory: factory);
        var detachedCard = new EntityCardViewModel(
            new SubscribedEntityViewModel(BuildToolNoteSnapshotForTests()),
            fieldEditorFactory: factory);

        var control = new EntityCardControl { DataContext = attachedCard };
        var window = new Window { Content = control, Width = 400, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Issue #1185: deterministically await the lazy field-editor build task exposed by
            // EntityCardViewModel instead of pumping RunJobs speculatively — the previous pump
            // loop could observe FieldEditors.Count == 0 before the async build finished on the
            // UI thread. Awaiting the actual build task removes the timing race.
            await attachedCard.FieldEditorsBuildTask;
            Dispatcher.UIThread.RunJobs();

            Assert.NotEmpty(attachedCard.FieldEditors);
            Assert.Empty(detachedCard.FieldEditors);
        }
        finally
        {
            window.Close();
        }
    }

    // Issue #1177: the shared TreeView.entity-card-tree style materializes a VirtualizingStackPanel
    // as its ItemsPanel, not a plain StackPanel.
    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardTree_ItemsPanel_IsVirtualizingStackPanel()
    {
        AssertItemsPanelIsVirtualizing("entity-card-tree");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardTreeView_ItemsPanel_IsVirtualizingStackPanel()
    {
        AssertItemsPanelIsVirtualizing("entity-card-tree-view");
    }

    // Issue #1177: hosting the tree in a bounded Window layout gives its inner ScrollViewer a
    // finite pixel viewport — a prerequisite for VirtualizingStackPanel to virtualize while still
    // scrolling per-pixel.
    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardTree_ScrollHost_ProvidesBoundedPixelViewport()
    {
        var tree = new TreeView();
        tree.Classes.Add("entity-card-tree");
        tree.ItemsSource = new[] { "a", "b", "c" };
        var window = new Window { Content = tree, Width = 400, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var scrollViewer = tree.GetVisualDescendants()
                .OfType<Avalonia.Controls.ScrollViewer>()
                .First();

            Assert.True(scrollViewer.Viewport.Height > 0, $"Viewport height was {scrollViewer.Viewport.Height}; expected a bounded, non-zero pixel viewport.");
            Assert.True(double.IsFinite(scrollViewer.Viewport.Height));
            // Offset is a per-pixel Avalonia.Vector; existence of the property confirms pixel-scroll semantics.
            _ = scrollViewer.Offset;
        }
        finally
        {
            window.Close();
        }
    }

    // Issue #1347: when a single entity card is taller than the pane, the entity-card-tree's inner
    // ScrollViewer must accumulate vertical extent beyond the viewport so the vertical scrollbar
    // engages. The TreeViewItem template's items row is Auto (not *) so the header measures cleanly
    // under the pixel-virtualizing VirtualizingStackPanel.
    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardTree_ItemTallerThanViewport_ScrollExtentExceedsViewport()
    {
        var (window, tree) = BuildTreeWithTallItem();
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var scrollViewer = tree.GetVisualDescendants()
                .OfType<Avalonia.Controls.ScrollViewer>()
                .First();

            Assert.True(
                scrollViewer.Extent.Height > scrollViewer.Viewport.Height,
                $"Extent height {scrollViewer.Extent.Height} should exceed viewport height {scrollViewer.Viewport.Height} for a tall card.");
        }
        finally
        {
            window.Close();
        }
    }

    // Issue #1347: offsetting the inner ScrollViewer must actually move content, so the bottom of a
    // tall card is reachable.
    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardTree_ItemTallerThanViewport_CanScrollToBottom()
    {
        var (window, tree) = BuildTreeWithTallItem();
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var scrollViewer = tree.GetVisualDescendants()
                .OfType<Avalonia.Controls.ScrollViewer>()
                .First();

            var maxOffset = scrollViewer.Extent.Height - scrollViewer.Viewport.Height;
            Assert.True(maxOffset > 0, "Tall card should produce a positive scrollable range.");

            scrollViewer.Offset = scrollViewer.Offset.WithY(maxOffset);
            Dispatcher.UIThread.RunJobs();

            Assert.True(
                scrollViewer.Offset.Y > 0,
                $"Vertical offset {scrollViewer.Offset.Y} should be positive after scrolling to the bottom.");
        }
        finally
        {
            window.Close();
        }
    }

    // Issue #1347: the shell border must not clip its own content in isolation, otherwise tall-card
    // overflow is silently cut off instead of being scrollable.
    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardTree_ShellBorder_DoesNotClipTallContent()
    {
        var (window, tree) = BuildTreeWithTallItem();
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var shellBorders = tree.GetVisualDescendants()
                .OfType<Border>()
                .Where(b => b.Classes.Contains("entity-card-shell-border"))
                .ToArray();

            Assert.NotEmpty(shellBorders);
            Assert.All(shellBorders, border => Assert.False(
                border.ClipToBounds,
                "entity-card-shell-border must not clip tall content."));
        }
        finally
        {
            window.Close();
        }
    }

    private static (Window Window, TreeView Tree) BuildTreeWithTallItem()
    {
        var tree = new TreeView();
        tree.Classes.Add("entity-card-tree");
        var tallItem = new TreeViewItem
        {
            Header = new Border { Height = 2000, Width = 120 },
        };
        tree.ItemsSource = new[] { tallItem };
        var window = new Window { Content = tree, Width = 300, Height = 300 };
        return (window, tree);
    }

    private static void AssertItemsPanelIsVirtualizing(string className)
    {
        var tree = new TreeView();
        tree.Classes.Add(className);
        tree.ItemsSource = new[] { "a", "b", "c" };
        var window = new Window { Content = tree, Width = 400, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var panel = tree.ItemsPanelRoot;
            Assert.NotNull(panel);
            Assert.IsType<Avalonia.Controls.VirtualizingStackPanel>(panel);
        }
        finally
        {
            window.Close();
        }
    }

    private static EntitySnapshot BuildToolNoteSnapshotForTests()
    {
        var entityId = Guid.NewGuid();
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "tool", "note"],
              "names": [["tools", "t-{{entityId:N}}"]],
              "display-name": { "default": "Tool Note" },
              "content": {
                "default": {
                  "mime-type": "text/markdown",
                  "content": { "text": "# Body" }
                }
              }
            }
            """);
        return new EntitySnapshot
        {
            EntityId = new EntityId(entityId),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = document.RootElement.Clone(),
            Relationships = Array.Empty<EntitySnapshot>(),
        };
    }
}
