using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Phantom.Workspaces.Gui.Shared.Controls;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Controls;

/// <summary>Renders bounded presentation descriptors using only application-owned templates.</summary>
public sealed class ShortcutContentControl : ContentControl
{
    public static readonly StyledProperty<Shortcut?> ShortcutProperty =
        AvaloniaProperty.Register<ShortcutContentControl, Shortcut?>(nameof(Shortcut));

    private static readonly Lazy<Bitmap> VisualStudioCodeImage = new(() => LoadVisualStudioCodeImage(
        uri => AssetLoader.Open(uri)));

    private readonly Lazy<Bitmap> visualStudioCodeImage;
    private readonly IDataTemplate[] registeredTemplates;

    public ShortcutContentControl()
        : this(VisualStudioCodeImage)
    {
    }

    internal ShortcutContentControl(
        Func<Uri, Stream> openAsset)
        : this(new Lazy<Bitmap>(() => LoadVisualStudioCodeImage(openAsset)))
    {
    }

    private ShortcutContentControl(
        Lazy<Bitmap> visualStudioCodeImage)
    {
        this.visualStudioCodeImage = visualStudioCodeImage;
        this.registeredTemplates =
        [
            new FuncDataTemplate<ShortcutTextContent>((descriptor, _) => CreateText(descriptor!.Text)),
            new FuncDataTemplate<ShortcutImageContent>((descriptor, _) => this.CreateImage(descriptor!)),
            new FuncDataTemplate<ShortcutCompositeContent>((descriptor, _) => this.CreateComposition(descriptor!)),
        ];
    }

    private static Bitmap LoadVisualStudioCodeImage(
        Func<Uri, Stream> openAsset)
    {
        using var stream = openAsset(
            new Uri("avares://Phantom.Workspaces/Assets/Shortcuts/vscode.png"));
        return new Bitmap(stream);
    }

    public Shortcut? Shortcut
    {
        get => this.GetValue(ShortcutProperty);
        set => this.SetValue(ShortcutProperty, value);
    }

    protected override void OnPropertyChanged(
        AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShortcutProperty)
        {
            this.RefreshContent();
        }
    }

    private void RefreshContent()
    {
        var shortcut = this.Shortcut;
        if (shortcut is null)
        {
            this.Content = null;
            return;
        }

        try
        {
            this.Content = shortcut.Content is null
                ? CreateText(shortcut.Label)
                : this.Render(shortcut.Content);
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException or ArgumentException or InvalidOperationException)
        {
            Trace.TraceWarning("Shortcut presentation failed for {0}: {1}", shortcut.Name, exception.Message);
            this.Content = CreateText(shortcut.HoverText);
        }
    }

    private Control Render(
        ShortcutContent descriptor)
    {
        var template = this.registeredTemplates.FirstOrDefault(candidate => candidate.Match(descriptor))
            ?? throw new NotSupportedException("Unsupported shortcut content descriptor.");
        return template.Build(descriptor)
            ?? throw new InvalidOperationException("Shortcut template did not create content.");
    }

    private static CopyableLinkTextBlock CreateText(
        string text)
    {
        var control = new CopyableLinkTextBlock
        {
            Text = text,
            Background = Brushes.Transparent,
            TextWrapping = TextWrapping.Wrap,
        };
        control.Bind(CopyableLinkTextBlock.CommandProperty, new Binding(nameof(Button.Command))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(Button) },
        });
        control.Bind(CopyableLinkTextBlock.CommandParameterProperty, new Binding(nameof(Button.CommandParameter))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(Button) },
        });
        return control;
    }

    private Control CreateImage(
        ShortcutImageContent descriptor)
    {
        var bitmap = descriptor.Resource switch
        {
            ShortcutImageResource.VisualStudioCode => this.visualStudioCodeImage.Value,
            _ => throw new NotSupportedException("Unsupported packaged shortcut image."),
        };
        var image = new Image
        {
            Source = bitmap,
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            Focusable = false,
            IsHitTestVisible = false,
        };
        AutomationProperties.SetAccessibilityView(image, AccessibilityView.Raw);
        // Keep 25% clear space around the unchanged mark, including beside other content.
        return new Border { Padding = new Thickness(4), Child = image, IsHitTestVisible = false };
    }

    private Control CreateComposition(
        ShortcutCompositeContent descriptor)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        foreach (var child in descriptor.Children)
        {
            var visual = this.Render(child);
            visual.IsHitTestVisible = false;
            visual.Focusable = false;
            AutomationProperties.SetAccessibilityView(visual, AccessibilityView.Raw);
            if (visual is Border { Child: Image image } border)
            {
                image.Width = 10;
                image.Height = 10;
                border.Padding = new Thickness(2.5);
            }
            else if (visual is CopyableLinkTextBlock text)
            {
                text.FontSize = 9;
                text.VerticalAlignment = VerticalAlignment.Center;
            }

            panel.Children.Add(visual);
        }

        return panel;
    }
}
