using Avalonia.Headless.XUnit;
using System;
using Avalonia.Controls;
using Phantom.Workspaces.Agent.Gui.Controls;
using Phantom.Workspaces.Testing.Gui;
using Xunit;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgoTextBlockTests
{
    [AvaloniaFact]
    public void AgoTextBlock_Value_SetsRelativeText()
    {
        var control = new AgoTextBlock
        {
            Value = DateTime.UtcNow.AddMinutes(-5),
        };

        Assert.Equal("5 minutes ago", control.Text);
    }

    [AvaloniaFact]
    public void AgoTextBlock_Tooltip_ShowsAbsoluteTimestamp()
    {
        var value = new DateTime(2024, 3, 7, 13, 45, 9, DateTimeKind.Utc);
        var control = new AgoTextBlock
        {
            Value = value,
        };

        var tip = ToolTip.GetTip(control);
        Assert.Equal("2024-03-07 13:45:09", tip);
    }

    [AvaloniaFact]
    public void AgoTextBlock_NullValue_ClearsTextAndTooltip()
    {
        var control = new AgoTextBlock
        {
            Value = DateTime.UtcNow.AddMinutes(-5),
        };

        control.Value = null;

        Assert.Equal(string.Empty, control.Text);
        Assert.Null(ToolTip.GetTip(control));
    }
}
