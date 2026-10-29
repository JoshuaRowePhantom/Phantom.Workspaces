using System;
using System.Collections.Generic;
using System.Linq;

namespace Phantom.Workspaces.ViewModels;

public sealed record ShortcutCompositeContent : ShortcutContent
{
    public ShortcutCompositeContent(
        IReadOnlyList<ShortcutContent> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (children.Count is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(children), "Shortcut compositions contain one to four leaves.");
        }

        if (children.Any(child => child is not (ShortcutTextContent or ShortcutImageContent)))
        {
            throw new ArgumentException("Only text and packaged images are allowed in a shortcut composition.", nameof(children));
        }

        this.Children = Array.AsReadOnly(children.ToArray());
    }

    public IReadOnlyList<ShortcutContent> Children { get; }
}
