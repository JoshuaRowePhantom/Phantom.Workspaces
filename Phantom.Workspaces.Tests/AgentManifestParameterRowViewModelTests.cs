using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class AgentManifestParameterRowViewModelTests
{
    [Fact]
    public void DetermineParameterKind_NameIsWorkingDirectory_ReturnsDirectory()
    {
        var kind = AgentManifestLaunchpadViewModel.DetermineParameterKind("working-directory");

        Assert.Equal(AgentManifestParameterKind.Directory, kind);
    }

    [Fact]
    public void DetermineParameterKind_NameIsOther_ReturnsText()
    {
        var kind = AgentManifestLaunchpadViewModel.DetermineParameterKind("trust-profile");

        Assert.Equal(AgentManifestParameterKind.Text, kind);
    }

    [Fact]
    public void DetermineParameterKind_EmptyName_ReturnsText()
    {
        var kind = AgentManifestLaunchpadViewModel.DetermineParameterKind(string.Empty);

        Assert.Equal(AgentManifestParameterKind.Text, kind);
    }

    [Fact]
    public void AgentManifestParameterRowViewModel_ParameterKindDirectory_IsDirectoryPickerIsTrue()
    {
        var row = new AgentManifestParameterRowViewModel
        {
            Name = "working-directory",
            ParameterKind = AgentManifestParameterKind.Directory,
        };

        Assert.True(row.IsDirectoryPicker);
    }

    [Fact]
    public void AgentManifestParameterRowViewModel_ParameterKindText_IsDirectoryPickerIsFalse()
    {
        var row = new AgentManifestParameterRowViewModel
        {
            Name = "trust-profile",
            ParameterKind = AgentManifestParameterKind.Text,
        };

        Assert.False(row.IsDirectoryPicker);
    }

    [Fact]
    public void AgentManifestParameterRowViewModel_DefaultParameterKind_IsText()
    {
        var row = new AgentManifestParameterRowViewModel();

        Assert.Equal(AgentManifestParameterKind.Text, row.ParameterKind);
    }
}
