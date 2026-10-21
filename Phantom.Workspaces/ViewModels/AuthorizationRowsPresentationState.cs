using Avalonia;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.ViewModels;

public enum AuthorizationRowFocusTarget { Memory, Source, PrimaryAction, Deny, Cancel }

public sealed class AuthorizationRowsPresentationState
{
    public Vector ScrollOffset { get; private set; }
    public UserAuthorizationRequestIdentifier? FocusedRequestIdentifier { get; private set; }
    public AuthorizationRowFocusTarget? FocusTarget { get; private set; }

    internal void CaptureScroll(Vector offset) => this.ScrollOffset = offset;

    internal void CaptureFocus(
        UserAuthorizationRequestIdentifier identifier,
        AuthorizationRowFocusTarget target)
    {
        this.FocusedRequestIdentifier = identifier;
        this.FocusTarget = target;
    }

    internal void Clear()
    {
        this.ScrollOffset = default;
        this.FocusedRequestIdentifier = null;
        this.FocusTarget = null;
    }
}
