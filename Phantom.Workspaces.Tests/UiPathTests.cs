using Phantom.Workspaces.Services.Navigation;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class UiPathTests
{
    [Fact]
    public void UiPath_WithWorkspaceAndTab_ProjectsToSerializedTabIdString()
    {
        var path = UiPath.FromSerializedTabId("workspace-1", "tab-1");

        Assert.True(path.HasTab);
        Assert.Equal("workspace-1", path.WorkspaceId);
        Assert.Equal("tab-1", path.TabId);
        Assert.Equal("tab-1", path.ToString());
    }

    [Fact]
    public void UiPath_PaneOnly_HasNoTab()
    {
        var path = new UiPath("workspace-1", null);

        Assert.False(path.HasTab);
        Assert.Equal("workspace-1", path.ToString());
    }

    [Fact]
    public void NavigationRequest_ConstructedFromUiPath_CarriesBothWorkspaceAndTab()
    {
        var path = new UiPath("workspace-1", "tab-1");

        var request = new NavigationRequest(path);

        Assert.Equal(path, request.Path);
    }

    [Fact]
    public void WorkspaceDocument_Path_TabIdMatchesSerializedDocumentId()
    {
        var tab = new WebViewModel("https://example.com")
        {
            Id = "tab-1",
            Title = "Example",
            WorkspacePaneId = "workspace-1",
        };
        var document = new WorkspaceDocument(tab);

        Assert.Equal(document.Id, document.Path.TabId);
        Assert.Equal("workspace-1", document.Path.WorkspaceId);
    }
}
