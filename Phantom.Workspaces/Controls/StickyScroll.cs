using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Phantom.Workspaces.Controls;

public static class StickyScroll
{
    private sealed class Owner { }

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Owner, ScrollViewer, bool>("IsEnabled");

    private static readonly ConditionalWeakTable<ScrollViewer, Engine> Engines = new();

    static StickyScroll()
    {
        IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>(static (scrollViewer, e) =>
        {
            if (e.NewValue is true)
            {
                var engine = new Engine(scrollViewer);
                Engines.AddOrUpdate(scrollViewer, engine);
                engine.Update();
            }
            else
            {
                if (Engines.TryGetValue(scrollViewer, out var engine))
                {
                    engine.Dispose();
                    Engines.Remove(scrollViewer);
                }
            }
        });
    }

    public static bool GetIsEnabled(ScrollViewer element) => element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(ScrollViewer element, bool value) => element.SetValue(IsEnabledProperty, value);

    private sealed class Engine : IDisposable
    {
        private readonly ScrollViewer scrollViewer;
        private bool disposed;

        public Engine(ScrollViewer scrollViewer)
        {
            this.scrollViewer = scrollViewer;
            scrollViewer.ScrollChanged += this.OnScrollChanged;
            scrollViewer.LayoutUpdated += this.OnLayoutUpdated;
            scrollViewer.DetachedFromVisualTree += this.OnDetachedFromVisualTree;
        }

        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => this.Update();
        private void OnLayoutUpdated(object? sender, EventArgs e) => this.Update();

        private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            this.Dispose();
        }

        public void Update()
        {
            if (this.disposed)
            {
                return;
            }

            var scrollViewer = this.scrollViewer;

            var measurements = new List<StickyItemMeasurement>();
            var presentersByControl = new Dictionary<Control, ContentPresenter>(ReferenceEqualityComparer.Instance);

            foreach (var descendant in scrollViewer.GetVisualDescendants())
            {
                if (descendant is not Control control)
                {
                    continue;
                }

                var row = StickyItem.GetRow(control);
                var col = StickyItem.GetColumn(control);
                if (row is null && col is null)
                {
                    continue;
                }

                if (control.RenderTransform is TranslateTransform tt)
                {
                    tt.X = 0;
                    tt.Y = 0;
                }

                var presenter = FindContentPresenter(control, scrollViewer);
                if (presenter is not null)
                {
                    presentersByControl[control] = presenter;
                    presenter.SetValue(Panel.ZIndexProperty, 0);
                }

                control.Classes.Remove("sticky-pinned");
            }

            foreach (var descendant in scrollViewer.GetVisualDescendants())
            {
                if (descendant is not Control control)
                {
                    continue;
                }

                var row = StickyItem.GetRow(control);
                var col = StickyItem.GetColumn(control);
                if (row is null && col is null)
                {
                    continue;
                }

                var translatedPoint = control.TranslatePoint(new Point(0, 0), scrollViewer);
                if (translatedPoint is null)
                {
                    continue;
                }

                var effectiveRow = row.HasValue
                    ? row.Value + ComputeBaseRow(control, scrollViewer)
                    : (int?)null;
                var effectiveCol = col.HasValue
                    ? col.Value + ComputeBaseColumn(control, scrollViewer)
                    : (int?)null;

                measurements.Add(new StickyItemMeasurement(
                    Key: control,
                    Top: translatedPoint.Value.Y,
                    Left: translatedPoint.Value.X,
                    Height: control.Bounds.Height,
                    Width: control.Bounds.Width,
                    VerticalLevel: effectiveRow,
                    HorizontalLevel: effectiveCol));
            }

            var pins = StickyLayoutSelector.ComputePins(measurements);

            foreach (var pin in pins)
            {
                if (pin.Key is not Control control)
                {
                    continue;
                }

                var translatedPoint = control.TranslatePoint(new Point(0, 0), scrollViewer);
                if (translatedPoint is null)
                {
                    continue;
                }

                var translateTransform = control.RenderTransform as TranslateTransform
                    ?? new TranslateTransform();

                if (pin.PinY.HasValue)
                {
                    translateTransform.Y = pin.PinY.Value - translatedPoint.Value.Y;
                }

                if (pin.PinX.HasValue)
                {
                    translateTransform.X = pin.PinX.Value - translatedPoint.Value.X;
                }

                control.RenderTransform = translateTransform;
                control.Classes.Add("sticky-pinned");

                if (presentersByControl.TryGetValue(control, out var presenter))
                {
                    var effectiveLevel = (StickyItem.GetRow(control) ?? StickyItem.GetColumn(control) ?? 0)
                        + ComputeBaseRow(control, scrollViewer);
                    presenter.SetValue(Panel.ZIndexProperty, 1000 - effectiveLevel);
                }
            }
        }

        private static int ComputeBaseRow(Control control, ScrollViewer boundary)
        {
            var total = 0;
            var current = control.Parent;
            while (current is Control parent && !ReferenceEquals(parent, boundary))
            {
                total += StickyItem.GetBaseRow(parent);
                current = parent.Parent;
            }

            return total;
        }

        private static int ComputeBaseColumn(Control control, ScrollViewer boundary)
        {
            var total = 0;
            var current = control.Parent;
            while (current is Control parent && !ReferenceEquals(parent, boundary))
            {
                total += StickyItem.GetBaseColumn(parent);
                current = parent.Parent;
            }

            return total;
        }

        private static ContentPresenter? FindContentPresenter(Control control, ScrollViewer boundary)
        {
            var current = control.Parent;
            while (current is Control parent && !ReferenceEquals(parent, boundary))
            {
                if (parent is ContentPresenter cp)
                {
                    return cp;
                }

                current = parent.Parent;
            }

            return null;
        }

        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.scrollViewer.ScrollChanged -= this.OnScrollChanged;
            this.scrollViewer.LayoutUpdated -= this.OnLayoutUpdated;
            this.scrollViewer.DetachedFromVisualTree -= this.OnDetachedFromVisualTree;
        }
    }
}
