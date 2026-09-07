namespace Phantom.Workspaces.Services.Navigation;

/// <summary>
/// A single entry on the navigation history stack.
/// The history is held purely in memory (see <see cref="NavigationHistoryService"/>), so there is no
/// on-disk representation to preserve. See #1341.
/// </summary>
public sealed record NavigationEntry(UiPath Path);
