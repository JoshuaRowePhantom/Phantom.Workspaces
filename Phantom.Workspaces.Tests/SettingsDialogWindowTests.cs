namespace Phantom.Workspaces.Tests;

using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Phantom.Workspaces;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.ViewModels.Configuration;

public sealed class SettingsDialogWindowTests
{
    [AvaloniaFact]
    public async Task TrySaveAsync_WhenSaveThrows_DoesNotCrash_ShowsErrorAndStaysOpen()
    {
        // #1349: a Save-path exception (here, persistence failure) must never reach the dispatcher.
        // The dialog must stay open, remain unsaved, and surface the failure in the in-dialog banner.
        var tempFile = Path.Combine(Path.GetTempPath(), $"phantom-settings-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempFile, "occupied");
        try
        {
            // Point persistence at a path *under* an existing file so directory creation fails.
            var service = new ConfigurationPersistenceService(Path.Combine(tempFile, "config.json"));
            var viewModel = new WorkspacesSettingsViewModel(service, new WorkspacesConfiguration());
            var window = new SettingsDialogWindow(viewModel);

            var exception = await Record.ExceptionAsync(() => window.TrySaveAsync());

            Assert.Null(exception);
            Assert.False(window.Saved);
            Assert.Null(window.Result);
            Assert.NotNull(window.SaveErrorMessage);
            Assert.Contains("Saving settings failed", window.SaveErrorMessage!, StringComparison.Ordinal);

            var banner = window.FindControl<TextBlock>("SaveErrorBanner");
            Assert.NotNull(banner);
            Assert.True(banner!.IsVisible);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

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
