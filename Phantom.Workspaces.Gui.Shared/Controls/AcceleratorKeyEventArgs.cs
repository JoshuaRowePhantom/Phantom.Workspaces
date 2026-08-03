using System;
using Avalonia.Input;

namespace Phantom.Workspaces.Gui.Shared.Controls;

/// <summary>
/// Event args for a generic accelerator-key notification bridged out of the WebView2 COM
/// <c>AcceleratorKeyPressed</c> event. Listeners set <see cref="Handled"/> to true to mark the
/// underlying COM event handled so the WebView2 does not also process the keystroke; leaving it
/// false lets the key reach the hosted HTML page.
/// </summary>
public sealed class AcceleratorKeyEventArgs : EventArgs
{
    public AcceleratorKeyEventArgs(int keyEventKind, Key key, KeyModifiers modifiers)
    {
        this.KeyEventKind = keyEventKind;
        this.Key = key;
        this.Modifiers = modifiers;
    }

    /// <summary>Raw WebView2 <c>COREWEBVIEW2_KEY_EVENT_KIND</c> value.</summary>
    public int KeyEventKind { get; }

    /// <summary>Translated Avalonia key.</summary>
    public Key Key { get; }

    /// <summary>Modifier state at the time of the event, including Alt inferred from SystemKeyDown/Up.</summary>
    public KeyModifiers Modifiers { get; }

    /// <summary>True when <see cref="KeyEventKind"/> is <c>KeyDown</c> (0) or <c>SystemKeyDown</c> (2).</summary>
    public bool IsKeyDown => this.KeyEventKind == 0 || this.KeyEventKind == 2;

    /// <summary>True when <see cref="KeyEventKind"/> is <c>KeyUp</c> (1) or <c>SystemKeyUp</c> (3).</summary>
    public bool IsKeyUp => this.KeyEventKind == 1 || this.KeyEventKind == 3;

    /// <summary>True when the event originated from a <c>SystemKey*</c> kind (WebView2 raises these while Alt is held).</summary>
    public bool IsSystemKey => this.KeyEventKind == 2 || this.KeyEventKind == 3;

    /// <summary>
    /// When set to true by a listener, the underlying WebView2 COM args are marked handled and the
    /// hosted page does not receive the keystroke. Default is false.
    /// </summary>
    public bool Handled { get; set; }
}
