using Phantom.Workspaces.Services.Notifications;

namespace Phantom.Workspaces.Tests;

internal sealed class FakeActiveTabProvider : IActiveTabProvider
{
    public string? ActiveTabId { get; set; }
}
