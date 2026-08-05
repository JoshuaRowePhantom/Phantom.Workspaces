using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;

namespace Phantom.Workspaces.Controls;

public partial class PhantomHostWindow : HostWindow
{
    private readonly DockControl? sourceDockControl;

    public PhantomHostWindow()
        : this(null)
    {
    }

    // #1196: A floating dock host window shares the SAME DockDataTemplates
    // IDataTemplate instances that the source (main-window) DockControl uses.
    // IDataTemplate is a stateless resolver and Avalonia's DataTemplates
    // (AvaloniaList<IDataTemplate>) does no owner tracking, so the same
    // instances can live in multiple collections without duplication.
    public PhantomHostWindow(DockControl? sourceDockControl)
    {
        this.sourceDockControl = sourceDockControl;
        AvaloniaXamlLoader.Load(this);

        if (sourceDockControl is not null)
        {
            foreach (var template in sourceDockControl.DataTemplates)
            {
                this.DataTemplates.Add(template);
            }
        }

        // #1196: HostWindow's Fluent ControlTheme materialises an unnamed
        // DockControl inside its ContentTemplate with the default
        // AutoCreateDataTemplates=true and an empty DataTemplates collection.
        // Descendants may not exist yet at OnApplyTemplate time, so hook
        // LayoutUpdated (fires after the visual tree is fully laid out) and
        // propagate the source's DataTemplates once per attached DockControl.
        this.LayoutUpdated += this.PropagateSourceDataTemplatesToDescendants;
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        this.PropagateSourceDataTemplatesToDescendants(this, System.EventArgs.Empty);
    }

    private void PropagateSourceDataTemplatesToDescendants(object? sender, System.EventArgs e)
    {
        // Fallback source resolution mirrors Dock.Avalonia's own pattern in
        // HostWindow.axaml.cs (var owner = windowModel.Layout?.Factory?
        // .DockControls.FirstOrDefault()).
        var source = this.sourceDockControl
            ?? this.Window?.Factory?.DockControls.OfType<DockControl>().FirstOrDefault();

        if (source is null)
        {
            return;
        }

        // Walk both the visual tree (where the templated HostWindow ControlTheme
        // materialises its own inner DockControl) and the logical tree (where
        // Content-bound DockControls appear before their visual parents are
        // attached).
        var candidates = this.GetVisualDescendants().OfType<DockControl>()
            .Concat(this.GetLogicalDescendants().OfType<DockControl>())
            .Distinct();

        foreach (var dockControl in candidates)
        {
            if (dockControl == source)
            {
                continue;
            }
            // Guards: preserve Host 2's XAML-declared scoped 5-template subset
            // (#1130) on the inner-pane DockControl — never overwrite a
            // non-empty template set.
            if (dockControl.DataTemplates.Count != 0)
            {
                continue;
            }
            foreach (var template in source.DataTemplates)
            {
                dockControl.DataTemplates.Add(template);
            }
        }
    }
}
