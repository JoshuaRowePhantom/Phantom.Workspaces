namespace Phantom.Workspaces.Tests;

public sealed class SettingsDialogWindowTests
{
    [Fact]
    public void SettingsDialogWindow_SizesToContentInBothDimensions()
    {
        var text = ReadAxaml("SettingsDialogWindow.axaml");

        Assert.Contains("SizeToContent=\"WidthAndHeight\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SizeToContent=\"Width\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" Width=\"680\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" Height=\"560\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsDialogWindow_HasBoundedMinAndMaxWidth()
    {
        var text = ReadAxaml("SettingsDialogWindow.axaml");

        Assert.Contains("MinWidth=\"", text, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsDialogWindow_HasBoundedMinAndMaxHeight()
    {
        var text = ReadAxaml("SettingsDialogWindow.axaml");

        Assert.Contains("MinHeight=\"", text, StringComparison.Ordinal);
        Assert.Contains("MaxHeight=\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsDialogWindow_RemainsUserResizable()
    {
        var text = ReadAxaml("SettingsDialogWindow.axaml");

        Assert.Contains("CanResize=\"True\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsDialogWindow_DetailColumn_Stretches()
    {
        var text = ReadAxaml("SettingsDialogWindow.axaml");

        Assert.Contains("ColumnDefinitions=\"180,*\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallationWizardWindow_SizesToContentInBothDimensions()
    {
        var text = ReadAxaml("InstallationWizardWindow.axaml");

        Assert.Contains("SizeToContent=\"WidthAndHeight\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SizeToContent=\"Width\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" Width=\"480\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" Height=\"580\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallationWizardWindow_IsUserResizable()
    {
        var text = ReadAxaml("InstallationWizardWindow.axaml");

        Assert.Contains("CanResize=\"True\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CanResize=\"False\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallationWizardWindow_HasBoundedMinAndMaxWidth()
    {
        var text = ReadAxaml("InstallationWizardWindow.axaml");

        Assert.Contains("MinWidth=\"", text, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallationWizardWindow_HasBoundedMinAndMaxHeight()
    {
        var text = ReadAxaml("InstallationWizardWindow.axaml");

        Assert.Contains("MinHeight=\"", text, StringComparison.Ordinal);
        Assert.Contains("MaxHeight=\"", text, StringComparison.Ordinal);
    }

    private static string ReadAxaml(string fileName)
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(root, "Phantom.Workspaces", fileName));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Phantom.Workspaces.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
