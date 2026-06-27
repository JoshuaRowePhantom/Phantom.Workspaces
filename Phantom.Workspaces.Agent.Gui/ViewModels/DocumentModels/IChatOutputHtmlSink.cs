namespace Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;

/// <summary>Where an <see cref="IChatOutputHtmlSink.UpdateContent"/> payload is placed relative to its target node.</summary>
public enum ChatOutputUpdateLocation
{
    /// <summary>Replace the target element's outer HTML with the payload.</summary>
    Replace,

    /// <summary>Insert the payload as the previous sibling of the target element.</summary>
    Before,

    /// <summary>Insert the payload as the next sibling of the target element.</summary>
    After,

    /// <summary>Append the payload as the last child of the target element (used for container nodes).</summary>
    Append,
}

/// <summary>
/// Receives incremental HTML update operations produced by <see cref="ChatOutputHtmlModel"/>. The
/// browser-hosted renderer implements this by forwarding the operations to the page's JavaScript
/// update surface; tests implement it with a recorder to assert the produced operation sequence.
/// </summary>
public interface IChatOutputHtmlSink
{
    /// <summary>Updates the DOM by placing <paramref name="content"/> relative to <paramref name="path"/>.</summary>
    /// <param name="path">Element id (or container id for <see cref="ChatOutputUpdateLocation.Append"/>) to update.</param>
    /// <param name="location">Where to place the content relative to the target.</param>
    /// <param name="content">The HTML payload to insert.</param>
    void UpdateContent(string path, ChatOutputUpdateLocation location, string content);

    /// <summary>Removes the element identified by <paramref name="path"/> from the DOM.</summary>
    void RemoveContent(string path);

    /// <summary>Requests that the page scroll to the bottom (honored only when scroll lock is enabled).</summary>
    void ScrollToBottom();
}
