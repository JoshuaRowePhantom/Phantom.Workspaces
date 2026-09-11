using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Phantom.Workspaces.Controls;
using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class RunningAgentBrainControlTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public void RunningAgentBrainControl_HeaderLabel_ReadsRunningAgents()
    {
        var control = new RunningAgentBrainControl();
        var window = new Window { Content = control };
        window.Show();

        try
        {
            var textBlocks = window.GetVisualDescendants().OfType<TextBlock>().ToList();
            var header = textBlocks.FirstOrDefault(tb => tb.Text == "Running agents");
            Assert.NotNull(header);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void ContinueInBackgroundCheckbox_Accessibility_UsesRequiredLabelAndTooltip()
    {
        var axaml = File.ReadAllText(FindAxaml());

        Assert.Contains("Content=\"Continue in background\"", axaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Continue in background\"", axaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip.Tip=\"Keep this agent running when the last viewer disconnects.\"", axaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding ContinueInBackground, Mode=OneWay}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding IsBackgroundOptionEnabled}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding SetContinueInBackgroundCommand}\"", axaml, StringComparison.Ordinal);
    }

    [Fact]
    public void InterruptAndTerminateButtons_AreDistinctAndAccessible()
    {
        var axaml = File.ReadAllText(FindAxaml());

        Assert.Contains("Content=\"X\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"Red\"", axaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Interrupt agent\"", axaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Terminate agent session\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding InterruptCommand}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding TerminateCommand}\"", axaml, StringComparison.Ordinal);
    }

    private static string FindAxaml()
        {
            var root = Directory.GetCurrentDirectory();
            while (!Directory.Exists(Path.Combine(root, "Phantom.Workspaces"))
                   && Path.GetDirectoryName(root) is string parent)
            {
                root = parent;
            }

            return Path.Combine(root, "Phantom.Workspaces", "Controls", "RunningAgentBrainControl.axaml");
        }
}
