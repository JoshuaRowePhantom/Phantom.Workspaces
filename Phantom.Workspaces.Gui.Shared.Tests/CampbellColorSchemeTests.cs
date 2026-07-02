using System.Reflection;
using Avalonia.Media;
using Phantom.Workspaces.Gui.Shared.Controls;

namespace Phantom.Workspaces.Gui.Shared.Tests;

/// <summary>Tests for <see cref="CampbellColorScheme"/> and related <see cref="TerminalControl"/> defaults.</summary>
public sealed class CampbellColorSchemeTests
{
    // ── Normal (dim) ANSI colors ─────────────────────────────────────────────────────────────

    [Fact]
    public void CampbellColorScheme_Black_IsCorrect() =>
        AssertRgb(12, 12, 12, CampbellColorScheme.Black);

    [Fact]
    public void CampbellColorScheme_Red_IsCorrect() =>
        AssertRgb(197, 15, 31, CampbellColorScheme.Red);

    [Fact]
    public void CampbellColorScheme_Green_IsCorrect() =>
        AssertRgb(19, 161, 14, CampbellColorScheme.Green);

    [Fact]
    public void CampbellColorScheme_Yellow_IsCorrect() =>
        AssertRgb(193, 156, 0, CampbellColorScheme.Yellow);

    [Fact]
    public void CampbellColorScheme_Blue_IsCorrect() =>
        AssertRgb(0, 55, 218, CampbellColorScheme.Blue);

    [Fact]
    public void CampbellColorScheme_Magenta_IsCorrect() =>
        AssertRgb(136, 23, 152, CampbellColorScheme.Magenta);

    [Fact]
    public void CampbellColorScheme_Cyan_IsCorrect() =>
        AssertRgb(58, 150, 221, CampbellColorScheme.Cyan);

    [Fact]
    public void CampbellColorScheme_White_IsCorrect() =>
        AssertRgb(204, 204, 204, CampbellColorScheme.White);

    // ── Bright ANSI colors ───────────────────────────────────────────────────────────────────

    [Fact]
    public void CampbellColorScheme_BrightBlack_IsCorrect() =>
        AssertRgb(118, 118, 118, CampbellColorScheme.BrightBlack);

    [Fact]
    public void CampbellColorScheme_BrightRed_IsCorrect() =>
        AssertRgb(231, 72, 86, CampbellColorScheme.BrightRed);

    [Fact]
    public void CampbellColorScheme_BrightGreen_IsCorrect() =>
        AssertRgb(22, 198, 12, CampbellColorScheme.BrightGreen);

    [Fact]
    public void CampbellColorScheme_BrightYellow_IsCorrect() =>
        AssertRgb(249, 241, 165, CampbellColorScheme.BrightYellow);

    [Fact]
    public void CampbellColorScheme_BrightBlue_IsCorrect() =>
        AssertRgb(59, 120, 255, CampbellColorScheme.BrightBlue);

    [Fact]
    public void CampbellColorScheme_BrightMagenta_IsCorrect() =>
        AssertRgb(180, 0, 158, CampbellColorScheme.BrightMagenta);

    [Fact]
    public void CampbellColorScheme_BrightCyan_IsCorrect() =>
        AssertRgb(97, 214, 214, CampbellColorScheme.BrightCyan);

    [Fact]
    public void CampbellColorScheme_BrightWhite_IsCorrect() =>
        AssertRgb(242, 242, 242, CampbellColorScheme.BrightWhite);

    // ── Background / Foreground defaults ────────────────────────────────────────────────────

    [Fact]
    public void CampbellColorScheme_Background_IsCorrect() =>
        AssertRgb(12, 12, 12, CampbellColorScheme.Background);

    [Fact]
    public void CampbellColorScheme_Foreground_IsCorrect() =>
        AssertRgb(204, 204, 204, CampbellColorScheme.Foreground);

    // ── Indexed lookup arrays ────────────────────────────────────────────────────────────────

    [Fact]
    public void CampbellColorScheme_DimArray_HasEightEntries() =>
        Assert.Equal(8, CampbellColorScheme.Dim.Length);

    [Fact]
    public void CampbellColorScheme_BrightArray_HasEightEntries() =>
        Assert.Equal(8, CampbellColorScheme.Bright.Length);

    [Fact]
    public void CampbellColorScheme_DimArray_IndexZeroIsBlack() =>
        Assert.Equal(CampbellColorScheme.Black, CampbellColorScheme.Dim[0]);

    [Fact]
    public void CampbellColorScheme_BrightArray_IndexZeroIsBrightBlack() =>
        Assert.Equal(CampbellColorScheme.BrightBlack, CampbellColorScheme.Bright[0]);

    // ── TerminalControl font defaults ────────────────────────────────────────────────────────

    [Fact]
    public void TerminalControl_DefaultFontFamily_StartsWithCascadiaMono()
    {
        var field = typeof(TerminalControl).GetField("MonoFamily",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var fontFamily = (FontFamily)field.GetValue(null)!;
        Assert.StartsWith("Cascadia Mono", fontFamily.Name);
    }

    [Fact]
    public void TerminalControl_DefaultFontSize_Is12()
    {
        var field = typeof(TerminalControl).GetField("TermFontSize",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var size = (double)field.GetValue(null)!;
        Assert.Equal(12.0, size);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private static void AssertRgb(byte r, byte g, byte b, Color actual)
    {
        Assert.Equal(r, actual.R);
        Assert.Equal(g, actual.G);
        Assert.Equal(b, actual.B);
    }
}
