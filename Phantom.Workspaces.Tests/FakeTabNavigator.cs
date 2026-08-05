using System.Collections.Generic;
using System.Threading.Tasks;
using Phantom.Workspaces.Services.Navigation;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Records every <see cref="ITabNavigator.NavigateAsync"/> invocation so call-site view models
/// (the running-agent brain button and the notifications dropdown) can be verified to delegate to
/// the single navigator with the expected <see cref="NavigationTarget"/> and
/// <see cref="NavigationOptions"/> (issue #1254).
/// </summary>
internal sealed class FakeTabNavigator : ITabNavigator
{
    public List<(NavigationTarget Target, NavigationOptions Options)> Calls { get; } = [];

    public NavigationTarget? LastTarget => this.Calls.Count == 0 ? null : this.Calls[^1].Target;

    public NavigationOptions? LastOptions => this.Calls.Count == 0 ? null : this.Calls[^1].Options;

    public Task<bool> NavigateAsync(NavigationTarget target, NavigationOptions? options = null)
    {
        this.Calls.Add((target, options ?? new NavigationOptions()));
        return Task.FromResult(true);
    }
}
