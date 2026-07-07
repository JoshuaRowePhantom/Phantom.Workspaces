using Phantom.Workspaces.Gui.Shared.Encoding;
using Xunit;

namespace Phantom.Workspaces.Gui.Shared.Tests;

public class VtMouseEncoderTests
{
    [Fact]
    public void VtMouseEncoder_X10_LeftButtonPress_EncodesCorrectly()
    {
        var result = VtMouseEncoder.Encode(
            button: 0,
            eventType: MouseEventType.Press,
            modifiers: MouseModifiers.None,
            col: 10,
            row: 5,
            mode: VtMouseMode.X10);

        var expected = "\x1b[M" + (char)(32) + (char)(42) + (char)(37);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void VtMouseEncoder_Sgr_LeftButtonPress_EncodesCorrectly()
    {
        var result = VtMouseEncoder.Encode(
            button: 0,
            eventType: MouseEventType.Press,
            modifiers: MouseModifiers.None,
            col: 10,
            row: 5,
            mode: VtMouseMode.Sgr);

        Assert.Equal("\x1b[<0;10;5M", result);
    }

    [Fact]
    public void VtMouseEncoder_Sgr_LeftButtonRelease_EncodesCorrectly()
    {
        var result = VtMouseEncoder.Encode(
            button: 0,
            eventType: MouseEventType.Release,
            modifiers: MouseModifiers.None,
            col: 10,
            row: 5,
            mode: VtMouseMode.Sgr);

        Assert.Equal("\x1b[<0;10;5m", result);
    }

    [Fact]
    public void VtMouseEncoder_Sgr_MouseMove_EncodesCorrectly()
    {
        var result = VtMouseEncoder.Encode(
            button: 0,
            eventType: MouseEventType.Motion,
            modifiers: MouseModifiers.None,
            col: 10,
            row: 5,
            mode: VtMouseMode.Sgr);

        Assert.Equal("\x1b[<32;10;5M", result);
    }

    [Fact]
    public void VtMouseEncoder_ScrollUp_EncodesButton64()
    {
        var result = VtMouseEncoder.Encode(
            button: 64,
            eventType: MouseEventType.Press,
            modifiers: MouseModifiers.None,
            col: 10,
            row: 5,
            mode: VtMouseMode.Sgr);

        Assert.Equal("\x1b[<64;10;5M", result);
    }

    [Fact]
    public void VtMouseEncoder_ScrollDown_EncodesButton65()
    {
        var result = VtMouseEncoder.Encode(
            button: 65,
            eventType: MouseEventType.Press,
            modifiers: MouseModifiers.None,
            col: 10,
            row: 5,
            mode: VtMouseMode.Sgr);

        Assert.Equal("\x1b[<65;10;5M", result);
    }

    [Fact]
    public void VtMouseEncoder_CoordinatesClampedToTerminalSize()
    {
        var result = VtMouseEncoder.Encode(
            button: 0,
            eventType: MouseEventType.Press,
            modifiers: MouseModifiers.None,
            col: 300,
            row: 300,
            mode: VtMouseMode.X10);

        var expected = "\x1b[M" + (char)(32) + (char)(255) + (char)(255);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void VtMouseEncoder_ModifierFlags_AddedToButtonCode()
    {
        var result = VtMouseEncoder.Encode(
            button: 0,
            eventType: MouseEventType.Press,
            modifiers: MouseModifiers.Shift | MouseModifiers.Alt,
            col: 10,
            row: 5,
            mode: VtMouseMode.Sgr);

        Assert.Equal("\x1b[<12;10;5M", result);
    }

    [Fact]
    public void VtMouseEncoder_Urxvt_LeftButtonPress_EncodesCorrectly()
    {
        var result = VtMouseEncoder.Encode(
            button: 0,
            eventType: MouseEventType.Press,
            modifiers: MouseModifiers.None,
            col: 10,
            row: 5,
            mode: VtMouseMode.Urxvt);

        Assert.Equal("\x1b[0;10;5M", result);
    }

    [Fact]
    public void VtMouseEncoder_ButtonTracking_EncodesLikeX10()
    {
        var result = VtMouseEncoder.Encode(
            button: 0,
            eventType: MouseEventType.Press,
            modifiers: MouseModifiers.None,
            col: 10,
            row: 5,
            mode: VtMouseMode.ButtonTracking);

        var expected = "\x1b[M" + (char)(32) + (char)(42) + (char)(37);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void VtMouseEncoder_AllMotion_EncodesLikeX10()
    {
        var result = VtMouseEncoder.Encode(
            button: 0,
            eventType: MouseEventType.Press,
            modifiers: MouseModifiers.None,
            col: 10,
            row: 5,
            mode: VtMouseMode.AllMotion);

        var expected = "\x1b[M" + (char)(32) + (char)(42) + (char)(37);
        Assert.Equal(expected, result);
    }
}
