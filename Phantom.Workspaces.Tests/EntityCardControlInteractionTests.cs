using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Phantom.Workspaces.Controls;

namespace Phantom.Workspaces.Tests;

public sealed class EntityCardControlInteractionTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public void IsInteractiveSource_TapInsideReferenceLinkButton_IsTreatedAsInteractive()
    {
        // Reproduces issue #22: a workspace reference-link button renders its content as an
        // inner TextBlock, so a routed tap reports the TextBlock (not the Button) as its source.
        // The card must still recognize the tap as interactive so it does not open the entity a
        // second time in addition to the button's own open command.
        var innerText = new TextBlock { Text = "Linked entity" };
        var openButton = new Button { Content = innerText };
        var plainText = new TextBlock { Text = "Card title" };
        var card = new Border
        {
            Child = new StackPanel
            {
                Children = { openButton, plainText },
            },
        };

        var window = CreateWindow(card);
        window.Show();

        try
        {
            var renderedInner = window.GetVisualDescendants()
                .OfType<TextBlock>()
                .First(textBlock => textBlock.Text == "Linked entity");

            Assert.True(EntityCardControl.IsInteractiveSource(renderedInner, card));
            Assert.True(EntityCardControl.IsInteractiveSource(openButton, card));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void IsInteractiveSource_TapOnNonInteractiveContent_IsNotInteractive()
    {
        // Reproduces issue #26: tapping non-interactive card chrome (a title TextBlock) should
        // still trigger the card's default open behavior, while tapping inner controls should not.
        var plainText = new TextBlock { Text = "Card title" };
        var textBox = new TextBox { Text = "editable" };
        var card = new Border
        {
            Child = new StackPanel
            {
                Children = { plainText, textBox },
            },
        };

        var window = CreateWindow(card);
        window.Show();

        try
        {
            Assert.False(EntityCardControl.IsInteractiveSource(plainText, card));
            Assert.True(EntityCardControl.IsInteractiveSource(textBox, card));
        }
        finally
        {
            window.Close();
        }
    }

    private static Window CreateWindow(Control content)
    {
        var window = new Window
        {
            Width = 400,
            Height = 400,
            Content = content,
        };
        window.Styles.Add(new FluentTheme());
        return window;
    }
}
