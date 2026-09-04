using System.Threading.Tasks;

namespace Phantom.Workspaces.Services.Navigation;

/// <summary>
/// Single navigation service shared by the running-agent brain button, the notifications dropdown,
/// and the Ctrl nav-stack popup. Resolves a <see cref="NavigationTarget"/> (pane + tab/entity) and
/// performs the navigation — selecting the target's workspace pane (opening it first if it is
/// open-but-unselected, per #1157), then activating and focusing the tab — so all three call sites
/// delegate rather than duplicate. See issue #1254.
/// </summary>
public interface ITabNavigator
{
    /// <summary>
    /// Resolves <paramref name="target"/> and navigates to it, applying the per-call-site
    /// <paramref name="options"/>. Returns <see langword="true"/> when navigation (or the entity
    /// fallback) was performed; <see langword="false"/> when there was nothing to navigate to.
    /// </summary>
    Task<bool> NavigateAsync(NavigationTarget target, NavigationOptions? options = null);
}
