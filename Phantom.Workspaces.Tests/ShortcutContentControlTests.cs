using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phantom.Workspaces.Controls;
using Phantom.Workspaces.Gui.Shared.Controls;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class ShortcutContentControlTests
{
    [Fact]
    public void Shortcut_BuiltInPresentation_PreservesIdentityAndSemanticCaptions()
    {
        Assert.Equal(ShortcutImageResource.VisualStudioCode,
            Assert.IsType<ShortcutImageContent>(Shortcut.VsCode.Content).Resource);
        var composition = Assert.IsType<ShortcutCompositeContent>(Shortcut.VsCodeWeb.Content);
        Assert.Equal(ShortcutImageResource.VisualStudioCode,
            Assert.IsType<ShortcutImageContent>(composition.Children[0]).Resource);
        Assert.Equal("🌐", Assert.IsType<ShortcutTextContent>(composition.Children[1]).Text);
        Assert.Equal("Open in VS Code", Shortcut.VsCode.HoverText);
        Assert.Equal("Open in VS Code Web", Shortcut.VsCodeWeb.HoverText);
        var legacy = new Shortcut("VsCode", "⌨");
        Assert.Equal(legacy, Shortcut.VsCode);
        Assert.Equal(legacy.GetHashCode(), Shortcut.VsCode.GetHashCode());
    }

    [Fact]
    public void ShortcutCompositeContent_Children_AreCopiedAndBounded()
    {
        ShortcutContent[] children = [new ShortcutTextContent("first")];
        var composition = new ShortcutCompositeContent(children);
        children[0] = new ShortcutTextContent("changed");
        Assert.Equal("first", Assert.IsType<ShortcutTextContent>(composition.Children[0]).Text);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShortcutCompositeContent([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShortcutCompositeContent(
            Enumerable.Repeat<ShortcutContent>(new ShortcutTextContent("x"), 5).ToArray()));
        Assert.Throws<ArgumentException>(() => new ShortcutCompositeContent([composition]));
        Assert.Throws<ArgumentException>(() => new ShortcutCompositeContent([null!]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShortcutTextContent(new string('x', 129)));
        Assert.Equal(128, new ShortcutTextContent(new string('x', 128)).Text.Length);
        Assert.Throws<ArgumentNullException>(() => new ShortcutTextContent(null!));
        Assert.Throws<ArgumentNullException>(() => new ShortcutCompositeContent(null!));
        Assert.Equal(4, new ShortcutCompositeContent(
            Enumerable.Repeat<ShortcutContent>(new ShortcutTextContent("x"), 4).ToArray()).Children.Count);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void ShortcutContentControl_LegacyAndTypedText_RenderCopyableLabels()
    {
        var legacy = new ShortcutContentControl { Shortcut = new Shortcut("Legacy", "Copy me") };
        var typed = new ShortcutContentControl
        {
            Shortcut = new Shortcut("Typed", "old") { Content = new ShortcutTextContent("Typed text") },
        };
        var window = new Window { Content = new StackPanel { Children = { legacy, typed } } };
        window.Show();
        try
        {
            Assert.Equal("Copy me", Assert.Single(legacy.GetVisualDescendants().OfType<CopyableLinkTextBlock>()).Text);
            Assert.Equal("Typed text", Assert.Single(typed.GetVisualDescendants().OfType<CopyableLinkTextBlock>()).Text);
            typed.Shortcut = null;
            Assert.Null(typed.Content);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void ShortcutContentControl_UnsupportedImageOrDescriptor_RendersSemanticFallback()
    {
        foreach (var descriptor in new ShortcutContent[]
        {
            new ShortcutImageContent((ShortcutImageResource)999),
            new UnsupportedContent(),
        })
        {
            var renderer = new ShortcutContentControl
            {
                Shortcut = new Shortcut("VsCode", "⌨") { Content = descriptor },
            };
            var count = 0;
            var parameter = new object();
            var button = new Button
            {
                Content = renderer,
                Command = new RelayCommand(value =>
                {
                    Assert.Same(parameter, value);
                    count++;
                }),
                CommandParameter = parameter,
            };
            var window = new Window { Content = button };
            window.Show();
            try
            {
                var fallback = Assert.Single(renderer.GetVisualDescendants().OfType<CopyableLinkTextBlock>());
                Assert.Equal("Open in VS Code", fallback.Text);
                fallback.Focus();
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                Assert.Equal(1, count);
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void ShortcutContentControl_SharedMetadata_RendersIndependentPackagedImages()
    {
        var first = new ShortcutContentControl { Shortcut = Shortcut.VsCode };
        var second = new ShortcutContentControl { Shortcut = Shortcut.VsCode };
        var window = new Window { Content = new StackPanel { Children = { first, second } } };
        window.Show();
        try
        {
            var firstImage = Assert.Single(first.GetVisualDescendants().OfType<Image>());
            var secondImage = Assert.Single(second.GetVisualDescendants().OfType<Image>());
            Assert.NotSame(firstImage, secondImage);
            Assert.NotNull(firstImage.Source);
            Assert.Equal(firstImage.Source!.Size.Width, firstImage.Source.Size.Height);
            Assert.Equal(Stretch.Uniform, firstImage.Stretch);
            Assert.False(firstImage.Focusable);
            Assert.False(firstImage.IsHitTestVisible);
            Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(firstImage));
            using var raster = new RenderTargetBitmap(new PixelSize(32, 32), new Vector(96, 96));
            raster.Render(firstImage);
            var pixels = new byte[32 * 32 * 4];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                raster.CopyPixels(new PixelRect(0, 0, 32, 32), handle.AddrOfPinnedObject(), pixels.Length, 32 * 4);
                var bluePixels = 0;
                for (var offset = 0; offset < pixels.Length; offset += 4)
                {
                    if (pixels[offset + 3] > 0 && pixels[offset] > pixels[offset + 2] + 40)
                    {
                        bluePixels++;
                    }
                }
                Assert.True(bluePixels > 20, "The packaged blue brand artwork must actually rasterize.");
            }
            finally
            {
                handle.Free();
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory(Timeout = 15_000)]
    [InlineData("Light", 1)]
    [InlineData("Light", 1.5)]
    [InlineData("Dark", 1)]
    [InlineData("Dark", 1.5)]
    [InlineData("Light", 2)]
    [InlineData("Dark", 2)]
    [InlineData("HighContrast", 1)]
    [InlineData("HighContrast", 1.5)]
    [InlineData("HighContrast", 2)]
    public void EntityCardControl_IconShortcuts_KeepSemanticsFootprintAndDispatch(
        string theme,
        double scale)
    {
        var entity = new SubscribedEntityViewModel(EntityCardControlTests.BuildGitWorktreeSnapshotForTests());
        var viewModel = new EntityCardViewModel(entity);
        var shortcuts = new[] { Shortcut.VsCode, Shortcut.VsCodeWeb }.Select(shortcut =>
            new EntityShortcutViewModel
            {
                Shortcut = shortcut,
                Entity = entity,
                ShortcutManager = new ShortcutManager(),
            }).ToArray();
        var count = 0;
        object? invokedWith = null;
        viewModel.SetShortcuts(shortcuts, new RelayCommand(parameter =>
        {
            count++;
            invokedWith = parameter;
        }));
        var card = new NavigationSpyCard { DataContext = viewModel };
        var window = new Window
        {
            Content = new LayoutTransformControl { LayoutTransform = new ScaleTransform(scale, scale), Child = card },
            RequestedThemeVariant = theme switch
            {
                "Light" => Avalonia.Styling.ThemeVariant.Light,
                "Dark" => Avalonia.Styling.ThemeVariant.Dark,
                _ => new Avalonia.Styling.ThemeVariant("HighContrast", Avalonia.Styling.ThemeVariant.Dark),
            },
            Width = 260,
            Height = 600,
        };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var buttons = card.GetVisualDescendants().OfType<Button>()
                .Where(button => button.CommandParameter is EntityShortcutViewModel).ToArray();
            Assert.Equal(2, buttons.Length);
            buttons[0].Focus();
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
            window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
            Assert.Same(buttons[1], window.FocusManager!.GetFocusedElement());
            foreach (var button in buttons)
            {
                var shortcut = (EntityShortcutViewModel)button.CommandParameter!;
                Assert.Equal(shortcut.Shortcut.HoverText, AutomationProperties.GetName(button));
                Assert.Equal(shortcut.Shortcut.HoverText, ToolTip.GetTip(button));
                Assert.Equal(new Size(28, 28), button.Bounds.Size);
                Assert.Single(button.GetVisualDescendants().OfType<Image>());
                Assert.All(button.GetVisualDescendants().OfType<Image>(), image =>
                    Assert.True(image.Bounds.Width > 0 && image.Bounds.Height > 0));
                Assert.All(button.GetVisualDescendants(), visual =>
                {
                    if (visual is InputElement inputElement)
                    {
                        Assert.False(inputElement.Focusable);
                    }
                });
                var point = button.TranslatePoint(new Point(14, 14), window)!.Value;
                var beforePointer = count;
                window.MouseMove(point);
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Assert.Equal(beforePointer + 1, count);
                Assert.Same(shortcut, invokedWith);
                var before = count;
                button.Focus();
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                Assert.Equal(before + 1, count);
                window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
                window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
                Assert.Equal(before + 2, count);
                shortcut.IsEnabled = false;
                Assert.False(button.IsEffectivelyEnabled);
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Assert.Equal(before + 2, count);
            }
            Assert.Equal(6, count);
            Assert.Equal(0, card.ActivationCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardControl_IconShortcuts_InheritSharedPresentationAcrossHosts()
    {
        var entity = new SubscribedEntityViewModel(EntityCardControlTests.BuildGitWorktreeSnapshotForTests());
        var root = new EntityListNodeViewModel(entity, ["root"], "root");
        var child = new EntityListNodeViewModel(entity, ["root", "child"], "child");
        root.Children.Add(child);
        foreach (var node in new[] { root, child })
        {
            node.Card.SetShortcuts(new[] { Shortcut.VsCode, Shortcut.VsCodeWeb }.Select(shortcut =>
                new EntityShortcutViewModel
                {
                    Shortcut = shortcut,
                    Entity = entity,
                    ShortcutManager = new ShortcutManager(),
                }).ToArray(), new RelayCommand(_ => { }));
        }
        var tree = new TreeView { ItemsSource = new[] { root } };
        var flat = new ItemsControl
        {
            ItemsSource = new[]
            {
                new EntityListItemViewModel(root, 0, 0, "root"),
                new EntityListItemViewModel(child, 1, 1, "child", "root"),
            },
        };
        var tab = new EntityWorkspaceTabViewModel { Id = "shortcut-host", Title = "Entity", EntityCardNode = root };
        var single = new ContentControl { Content = tab };
        var floating = new Window
        {
            Content = new ContentControl { Content = tab },
            Width = 600,
            Height = 400,
        };
        var window = new Window
        {
            Content = new StackPanel { Children = { tree, flat, single } },
            Width = 700,
            Height = 800,
        };
        window.Show();
        floating.Show();
        try
        {
            var container = Assert.IsType<TreeViewItem>(tree.ContainerFromIndex(0));
            container.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            floating.UpdateLayout();
            Assert.Equal(4, tree.GetVisualDescendants().OfType<ShortcutContentControl>().Count());
            Assert.Equal(4, flat.GetVisualDescendants().OfType<ShortcutContentControl>().Count());
            Assert.Equal(2, single.GetVisualDescendants().OfType<ShortcutContentControl>().Count());
            Assert.Equal(2, floating.GetVisualDescendants().OfType<ShortcutContentControl>().Count());
            var renderers = window.GetVisualDescendants().OfType<ShortcutContentControl>()
                .Concat(floating.GetVisualDescendants().OfType<ShortcutContentControl>()).ToArray();
            Assert.All(renderers, renderer =>
                Assert.Single(renderer.GetVisualDescendants().OfType<Image>()));
            Assert.Equal(renderers.Length, renderers.SelectMany(renderer =>
                renderer.GetVisualDescendants().OfType<Image>()).Distinct().Count());
        }
        finally
        {
            floating.Close();
            window.Close();
        }
    }

    private sealed record UnsupportedContent : ShortcutContent;

    private sealed class NavigationSpyCard : EntityCardControl
    {
        public int ActivationCount { get; private set; }

        internal override void ActivateCard() => this.ActivationCount++;
    }
}
