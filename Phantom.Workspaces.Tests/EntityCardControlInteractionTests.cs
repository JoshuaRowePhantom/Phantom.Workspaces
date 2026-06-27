using System.Runtime.CompilerServices;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Phantom.Workspaces.Controls;

namespace Phantom.Workspaces.Tests;

public sealed class EntityCardControlInteractionTests
{
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
