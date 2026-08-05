using System.Runtime.CompilerServices;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Phantom.Workspaces.Controls;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class EntityCardControlInteractionTests
{
    [Fact]
    public void EntityCardControl_TapHandlers_WiredToRootElement()
    {
        // Issue #1029: after removing the outer Border chrome, the tap-to-activate and
        // interactive-child bubbling-suppression handlers must remain wired to the new content root.
        var cardPath = Path.Combine(FindRepositoryRoot().FullName, "Phantom.Workspaces", "Controls", "EntityCardControl.axaml");
        var card = File.ReadAllText(cardPath);

        Assert.DoesNotContain("<Border Classes=\"entity-card\"", card, StringComparison.Ordinal);

        var rootStart = card.IndexOf("<StackPanel Classes=\"workspace-entity-card-content\"", StringComparison.Ordinal);
        Assert.True(rootStart >= 0, "The card root must be the content StackPanel.");
        var rootEnd = card.IndexOf('>', rootStart);
        var root = card[rootStart..rootEnd];
        Assert.Contains("Tapped=\"OnEntityCardTapped\"", root, StringComparison.Ordinal);

        Assert.Contains("Tapped=\"OnInteractiveChildTapped\"", card, StringComparison.Ordinal);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Phantom.Workspaces.slnx")))
            {
                return current;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardControl_WhenTappedEventAlreadyHandled_DoesNotOpenEntity()
    {
        // Reproduces issue #85: an interactive child (e.g. a Button or CheckBox) sets e.Handled=true
        // before the tap bubbles to the card's Tapped handler. The handler must skip activation so
        // the card does not double-open alongside the child's own action.
        var card = new SpyEntityCardControl();
        var e = (TappedEventArgs)RuntimeHelpers.GetUninitializedObject(typeof(TappedEventArgs));
        e.Handled = true;

        card.OnEntityCardTapped(null, e);

        Assert.Equal(0, card.ActivateCardCallCount);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardControl_WhenTappedOnBackground_OpensEntity()
    {
        // A tap on a non-interactive area (e.g. the title TextBlock or empty card space) arrives
        // with Handled=false; the handler must activate the entity and claim the event.
        var card = new SpyEntityCardControl();
        var e = (TappedEventArgs)RuntimeHelpers.GetUninitializedObject(typeof(TappedEventArgs));

        card.OnEntityCardTapped(null, e);

        Assert.Equal(1, card.ActivateCardCallCount);
        Assert.True(e.Handled);
    }

    private sealed class SpyEntityCardControl : EntityCardControl
    {
        public int ActivateCardCallCount { get; private set; }

        internal override void ActivateCard()
        {
            ActivateCardCallCount++;
        }
    }
}
