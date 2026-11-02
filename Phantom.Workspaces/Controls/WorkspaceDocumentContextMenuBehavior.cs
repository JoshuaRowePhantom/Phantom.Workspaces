using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Avalonia.Converters;
using Phantom.Workspaces.Services.TabCommands;
using Phantom.Workspaces.ViewModels;
using System.Runtime.CompilerServices;

namespace Phantom.Workspaces.Controls;

public static class WorkspaceDocumentContextMenuBehavior
{
    private static readonly ConditionalWeakTable<DocumentTabStripItem, ItemSubscription> ItemSubscriptions = [];
    private static readonly ConditionalWeakTable<ContextMenu, MenuController> MenuControllers = [];
    private static readonly ConditionalWeakTable<ContextMenu, DocumentTabStripItem> StockMenuOwners = [];
    private static readonly WorkspaceDocumentMenuFactory MenuFactory = new();

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<DocumentTabStripItem, bool>(
            "IsEnabled",
            typeof(WorkspaceDocumentContextMenuBehavior));

    static WorkspaceDocumentContextMenuBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<DocumentTabStripItem>(OnIsEnabledChanged);
    }

    public static bool GetIsEnabled(DocumentTabStripItem item) => item.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DocumentTabStripItem item, bool value) =>
        item.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DocumentTabStripItem item, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.GetNewValue<bool>())
        {
            ItemSubscriptions.GetValue(item, static key => new ItemSubscription(key));
            return;
        }

        if (ItemSubscriptions.TryGetValue(item, out var subscription))
        {
            subscription.Dispose();
            ItemSubscriptions.Remove(item);
        }
    }

    private sealed class ItemSubscription : IDisposable
    {
        private readonly DocumentTabStripItem item;
        private MenuController? controller;
        private ContextMenu? menu;

        public ItemSubscription(DocumentTabStripItem item)
        {
            this.item = item;
            this.item.PropertyChanged += this.OnItemPropertyChanged;
            this.item.DetachedFromVisualTree += this.OnDetachedFromVisualTree;
            this.item.AttachedToVisualTree += this.OnAttachedToVisualTree;
            this.item.AddHandler(Control.ContextRequestedEvent, this.OnContextRequested, RoutingStrategies.Tunnel);
            this.item.AddHandler(InputElement.KeyUpEvent, this.OnKeyUp, RoutingStrategies.Tunnel);
            this.AttachController();
        }

        public void Dispose()
        {
            this.item.PropertyChanged -= this.OnItemPropertyChanged;
            this.item.DetachedFromVisualTree -= this.OnDetachedFromVisualTree;
            this.item.AttachedToVisualTree -= this.OnAttachedToVisualTree;
            this.item.RemoveHandler(Control.ContextRequestedEvent, this.OnContextRequested);
            this.item.RemoveHandler(InputElement.KeyUpEvent, this.OnKeyUp);
            this.DetachController();
        }

        private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs args)
            => this.DetachController();

        private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs args)
            => this.AttachController();

        private void OnContextRequested(object? sender, ContextRequestedEventArgs args)
        {
            if (args.Source is not Visual source) { return; }
            var origin = ResolveOrigin(source);
            if (ReferenceEquals(origin, this.item))
            {
                this.CaptureOrigin();
                if (ReferenceEquals(source, this.item) && this.menu is not null)
                {
                    // The theme attaches the stock menu to PART_TabBody, which is not
                    // keyboard-focusable. A keyboard request on the container still has
                    // an exact origin; open through Avalonia's supported owner overload.
                    this.OpenNativeMenu();
                    args.Handled = true;
                }
            }
        }

        private void OnKeyUp(object? sender, KeyEventArgs args)
        {
            if (args.Handled || this.menu is null ||
                !(args.Key == Key.Apps || args.Key == Key.F10 && args.KeyModifiers == KeyModifiers.Shift))
            {
                return;
            }
            if (args.Source is not Visual source ||
                !ReferenceEquals(ResolveOrigin(source), this.item))
            {
                return;
            }
            // Opening on KeyDown lets Avalonia's popup consume the same gesture's
            // KeyUp as a close request (notably Apps). Open on release instead.
            this.CaptureOrigin();
            this.OpenNativeMenu();
            args.Handled = true;
        }

        private static DocumentTabStripItem? ResolveOrigin(Visual source) =>
            source as DocumentTabStripItem ?? source.GetVisualAncestors().OfType<DocumentTabStripItem>().FirstOrDefault();

        private void CaptureOrigin()
        {
            if (this.menu is not null && this.OwnsStockMenu(this.menu)
                && this.item.DataContext is WorkspaceDocument document
                && this.item.GetVisualAncestors().OfType<DocumentControl>().FirstOrDefault() is { } documentControl)
            {
                // Popup is a logical root in the consumed Avalonia version, so Dock's
                // $parent[DocumentControl] lookup cannot reach the owning tab layout.
                // Keep the stock capability/count converters and give only these four
                // directional bindings an explicit, live orientation source.
                foreach (var (index, layouts) in new[]
                {
                    (5, "Top"),
                    (7, "Top"),
                    (6, "Left,Right"),
                    (8, "Left,Right"),
                })
                {
                    // Pinned stock item order; localized headers are unresolved until
                    // first opening and must never be used as command identities.
                    var action = this.menu!.Items.OfType<MenuItem>().ElementAt(index);
                    action.Bind(Visual.IsVisibleProperty, new MultiBinding
                    {
                        Converter = BoolConverters.And,
                        Bindings =
                        {
                            new MultiBinding
                            {
                                Converter = DockCapabilityConverters.CanCloseMultiConverter,
                                Bindings =
                                {
                                    new Binding { Source = document },
                                    new Binding(nameof(document.CanClose)) { Source = document },
                                    new Binding("DockCapabilityOverrides.CanClose") { Source = document },
                                    new Binding("Owner.DockCapabilityPolicy.CanClose") { Source = document },
                                },
                            },
                            new MultiBinding
                            {
                                Converter = CanRemoveDockableConverter.Instance,
                                Bindings =
                                {
                                    new Binding("Owner.CanCloseLastDockable") { Source = document, FallbackValue = true },
                                    new Binding("Owner.VisibleDockables.Count") { Source = document, FallbackValue = 0 },
                                },
                            },
                            new Binding(nameof(documentControl.TabsLayout))
                            {
                                Source = documentControl,
                                Converter = DocumentTabLayoutMatchConverter.Instance,
                                ConverterParameter = layouts,
                            },
                        },
                    });
                }
            }
            this.controller?.CaptureOrigin(this.item);
        }

        private void OpenNativeMenu()
        {
            var owner = this.item.GetVisualDescendants().OfType<Control>()
                .SingleOrDefault(control => ReferenceEquals(control.ContextMenu, this.menu));
            if (owner is not null) { this.menu!.Open(owner); }
        }

        private void OnItemPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == DocumentTabStripItem.DocumentContextMenuProperty)
            {
                this.AttachController();
            }
            else if (args.Property == StyledElement.DataContextProperty)
            {
                this.controller?.InvalidateOrigin(this.item);
                this.AttachController();
            }
        }

        private void AttachController()
        {
            var nextMenu = this.item.DocumentContextMenu as ContextMenu;
            if (this.item.DataContext is WorkspaceDocument
                && nextMenu is not null
                && !this.OwnsStockMenu(nextMenu)
                && (StockMenuOwners.TryGetValue(nextMenu, out _)
                    || ReferenceEquals(nextMenu, this.item.FindResource("DocumentTabStripItemContextMenu"))))
            {
                // Instantiate the consumed compiled Dock resource, not a shallow copy of
                // already-parented controls. Each content header owns its stock bindings.
                var source = new Uri("avares://Dock.Avalonia.Themes.Fluent/Controls/DocumentTabStripItem.axaml");
                var resources = new ResourceInclude(source) { Source = source };
                if (resources.TryGetResource("DocumentTabStripItemContextMenu", this.item.ActualThemeVariant, out var value)
                    && value is ContextMenu stockMenu)
                {
                    StockMenuOwners.Add(stockMenu, this.item);
                    this.item.SetCurrentValue(DocumentTabStripItem.DocumentContextMenuProperty, stockMenu);
                    return;
                }
            }
            if (ReferenceEquals(this.menu, nextMenu))
            {
                return;
            }

            this.DetachController();
            this.menu = nextMenu;
            if (this.menu is null)
            {
                return;
            }

            this.controller = MenuControllers.GetValue(this.menu, static menu => new MenuController(menu));
            this.controller.Register(this.item);
        }

        private bool OwnsStockMenu(ContextMenu candidate) =>
            StockMenuOwners.TryGetValue(candidate, out var owner) && ReferenceEquals(owner, this.item);

        private void DetachController()
        {
            if (this.controller is null || this.menu is null)
            {
                this.controller = null;
                this.menu = null;
                return;
            }

            this.controller.Unregister(this.item);
            if (this.controller.IsEmpty)
            {
                this.controller.Dispose();
                MenuControllers.Remove(this.menu);
            }

            this.controller = null;
            this.menu = null;
        }
    }

    private sealed class MenuController : IDisposable
    {
        private readonly ContextMenu menu;
        private readonly HashSet<DocumentTabStripItem> registeredItems = [];
        private DocumentTabStripItem? origin;

        public MenuController(ContextMenu menu)
        {
            this.menu = menu;
            this.menu.Opened += this.OnOpened;
            this.menu.Closed += this.OnClosed;
        }

        public bool IsEmpty => this.registeredItems.Count == 0;

        public void Dispose()
        {
            this.menu.Opened -= this.OnOpened;
            this.menu.Closed -= this.OnClosed;
            MenuFactory.Clear(this.menu);
            this.registeredItems.Clear();
        }

        public void Register(DocumentTabStripItem item) => this.registeredItems.Add(item);

        public void Unregister(DocumentTabStripItem item)
        {
            this.InvalidateOrigin(item);
            this.registeredItems.Remove(item);
        }

        public void CaptureOrigin(DocumentTabStripItem item)
        {
            this.origin = this.registeredItems.Contains(item) ? item : null;
            if (this.origin is not null) { MenuFactory.Apply(this.menu, this.origin); }
            else { MenuFactory.Clear(this.menu); }
        }

        public void InvalidateOrigin(DocumentTabStripItem item)
        {
            if (ReferenceEquals(this.origin, item))
            {
                this.origin = null;
                MenuFactory.Clear(this.menu);
            }
        }

        private void OnOpened(object? sender, EventArgs args)
        {
            if (this.origin is not { } tabItem
                || !this.registeredItems.Contains(tabItem))
            {
                MenuFactory.Clear(this.menu);
                return;
            }

            MenuFactory.Apply(this.menu, tabItem);
        }

        private void OnClosed(object? sender, EventArgs args)
        {
            this.origin = null;
            MenuFactory.Clear(this.menu);
        }
    }
}
