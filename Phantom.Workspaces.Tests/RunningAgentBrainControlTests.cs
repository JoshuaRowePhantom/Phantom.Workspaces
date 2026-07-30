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
}
