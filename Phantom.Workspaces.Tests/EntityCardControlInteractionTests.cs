using System.Runtime.CompilerServices;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Phantom.Workspaces.Controls;

namespace Phantom.Workspaces.Tests;

public sealed class EntityCardControlInteractionTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public void OnEntityCardTapped_WhenEventAlreadyHandled_DoesNotOpenCard()
    {
        var card = new SpyEntityCardControl();
        var e = (TappedEventArgs)RuntimeHelpers.GetUninitializedObject(typeof(TappedEventArgs));
        e.Handled = true;

        card.OnEntityCardTapped(null, e);

        Assert.Equal(0, card.ActivateCardCallCount);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void OnEntityCardTapped_WhenEventNotHandled_OpensCard()
    {
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
