using System;

namespace Phantom.Workspaces.ViewModels;

public sealed record ShortcutTextContent : ShortcutContent
{
    public ShortcutTextContent(
        string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(text), "Shortcut text is limited to 128 characters.");
        }

        this.Text = text;
    }

    public string Text { get; }
}
