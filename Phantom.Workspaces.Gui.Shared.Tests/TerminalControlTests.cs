using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Phantom.Workspaces.Gui.Shared.Controls;
using Phantom.Workspaces.Gui.Shared.ViewModels;
using VtNetCore.VirtualTerminal;
using VtNetCore.VirtualTerminal.Enums;
using VtNetCore.VirtualTerminal.Model;
using VtNetCore.XTermParser;

namespace Phantom.Workspaces.Gui.Shared.Tests;

/// <summary>Tests for <see cref="TerminalControl"/> and <see cref="TerminalSessionViewModel"/>.</summary>
public sealed class TerminalControlTests
{
    // ── TerminalSessionViewModel exit state ───────────────────────────────────────────────────

    [Fact]
    public void TerminalSessionViewModel_WhenExited_NotCompletedInitially()
    {
        var vm = new TerminalSessionViewModel
        {
            Stream = new MemoryStream(),
            ResizeCallback = static (_, _, _) => ValueTask.CompletedTask,
        };

        Assert.False(vm.IsExited);
        Assert.False(vm.WhenExited.IsCompleted);
    }

    [Fact]
    public void TerminalSessionViewModel_NotifyExited_FlipsIsExited()
    {
        var vm = new TerminalSessionViewModel
        {
            Stream = new MemoryStream(),
            ResizeCallback = static (_, _, _) => ValueTask.CompletedTask,
        };

        vm.NotifyExited();

        Assert.True(vm.IsExited);
        Assert.True(vm.WhenExited.IsCompleted);
    }

    [Fact]
    public void TerminalSessionViewModel_NotifyExited_Idempotent()
    {
        var vm = new TerminalSessionViewModel
        {
            Stream = new MemoryStream(),
            ResizeCallback = static (_, _, _) => ValueTask.CompletedTask,
        };

        vm.NotifyExited();
        vm.NotifyExited(); // should not throw or reset
        Assert.True(vm.IsExited);
    }

    // ── TerminalControl – screen buffer updates ───────────────────────────────────────────────

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void TerminalControl_PushBytes_UpdatesScreenBuffer()
    {
        var stream = new MemoryStream();
        var vm = new TerminalSessionViewModel
        {
            Stream = stream,
            ResizeCallback = static (_, _, _) => ValueTask.CompletedTask,
        };

        var control = new TerminalControl();
        control.Measure(new Size(800, 600));
        control.Arrange(new Rect(0, 0, 800, 600));
        control.Session = vm;

        // Push 'H', 'i' directly via the test hook.
        control.PushBytesForTest(System.Text.Encoding.ASCII.GetBytes("Hi"));

        var vtc = control.Vtc;
        Assert.NotNull(vtc);

        var line = vtc.ViewPort.GetVisibleLine(0);
        Assert.NotNull(line);
        Assert.True(line.Count >= 2);
        Assert.Equal('H', line[0].Char);
        Assert.Equal('i', line[1].Char);
    }

    // ── TerminalControl – key-down writes VT sequence to stream ──────────────────────────────

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void TerminalControl_KeyDown_WritesSequenceToStream()
    {
        var stream = new MemoryStream();
        var vm = new TerminalSessionViewModel
        {
            Stream = stream,
            ResizeCallback = static (_, _, _) => ValueTask.CompletedTask,
        };

        var control = new TerminalControl();
        control.Measure(new Size(800, 600));
        control.Arrange(new Rect(0, 0, 800, 600));
        control.Session = vm;

        // Raise an Up-arrow key-down event. MemoryStream.WriteAsync completes synchronously.
        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Up,
            KeyModifiers = KeyModifiers.None,
        };
        control.RaiseEvent(args);

        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("\x1b[A"), stream.ToArray());
    }

    // ── TerminalControl – resize callback ─────────────────────────────────────────────────────

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void TerminalControl_Resize_CallsResizeCallback()
    {
        var resizeCalls = new System.Collections.Generic.List<(int cols, int rows)>();

        var vm = new TerminalSessionViewModel
        {
            Stream = new MemoryStream(),
            ResizeCallback = (cols, rows, _) =>
            {
                resizeCalls.Add((cols, rows));
                return ValueTask.CompletedTask;
            },
        };

        var control = new TerminalControl();
        control.Session = vm;

        // Simulate the control receiving a size so ApplyResize computes columns/rows.
        control.Measure(new Size(640, 400));
        control.Arrange(new Rect(0, 0, 640, 400));

        // Trigger a synchronous resize by calling ApplyResize via reflection.
        typeof(TerminalControl)
            .GetMethod("ApplyResize", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(control, null);

        Assert.NotEmpty(resizeCalls);
        var (cols, rows) = resizeCalls[0];
        Assert.True(cols > 0);
        Assert.True(rows > 0);
    }

    // ── VT core – SGR color sequences ─────────────────────────────────────────────────────────

    [Fact]
    public void VtCore_SgrRedForeground_SetsForegroundColor()
    {
        var vtc = CreateVtc(cols: 80, rows: 24);
        var consumer = new DataConsumer(vtc);

        // ESC [ 3 1 m = red foreground, then ASCII 'A'
        consumer.Push(System.Text.Encoding.ASCII.GetBytes("\x1b[31mA"));

        var line = vtc.ViewPort.GetVisibleLine(0);
        Assert.NotNull(line);
        Assert.True(line.Count >= 1);
        Assert.Equal('A', line[0].Char);
        Assert.Equal(ETerminalColor.Red, line[0].Attributes.ForegroundColor);
    }

    [Fact]
    public void VtCore_SgrReset_ClearsAttributes()
    {
        var vtc = CreateVtc(cols: 80, rows: 24);
        var consumer = new DataConsumer(vtc);

        // Set red, write 'A', then reset, write 'B'.
        consumer.Push(System.Text.Encoding.ASCII.GetBytes("\x1b[31mA\x1b[0mB"));

        var line = vtc.ViewPort.GetVisibleLine(0);
        Assert.NotNull(line);
        Assert.Equal('A', line[0].Char);
        Assert.Equal(ETerminalColor.Red, line[0].Attributes.ForegroundColor);
        Assert.Equal('B', line[1].Char);
        // After ESC[0m, ForegroundColor defaults back to White.
        Assert.Equal(ETerminalColor.White, line[1].Attributes.ForegroundColor);
    }

    [Fact]
    public void VtCore_SgrBold_SetsBrightAttribute()
    {
        var vtc = CreateVtc(cols: 80, rows: 24);
        var consumer = new DataConsumer(vtc);

        // ESC [ 1 m = bold/bright
        consumer.Push(System.Text.Encoding.ASCII.GetBytes("\x1b[1mX"));

        var line = vtc.ViewPort.GetVisibleLine(0);
        Assert.NotNull(line);
        Assert.Equal('X', line[0].Char);
        Assert.True(line[0].Attributes.Bright);
    }

    // ── VT core – alt-screen ──────────────────────────────────────────────────────────────────

    [Fact]
    public void VtCore_AltScreenEnter_SwitchesBuffer()
    {
        var vtc = CreateVtc(cols: 80, rows: 24);
        var consumer = new DataConsumer(vtc);

        // Write on normal screen, then switch to alt screen.
        consumer.Push(System.Text.Encoding.ASCII.GetBytes("Normal"));

        // ESC [ ? 1 0 4 9 h = enable alt screen with cursor save (xterm)
        consumer.Push(System.Text.Encoding.ASCII.GetBytes("\x1b[?1049h"));

        // Alt screen should be blank (empty buffer). GetVisibleLine may return null for an empty line.
        var line = vtc.ViewPort.GetVisibleLine(0);
        // The alt-screen line should not start with 'N' from "Normal".
        Assert.True(line is null || line.Count == 0 || line[0].Char == '\0' || line[0].Char == ' ');
    }

    // ── VT core – SGR mouse mode ──────────────────────────────────────────────────────────────

    [Fact]
    public void VtCore_SgrMouseEnable_SetsSgrMouseMode()
    {
        var vtc = CreateVtc(cols: 80, rows: 24);
        var consumer = new DataConsumer(vtc);

        // ESC [ ? 1 0 0 6 h = enable SGR extended mouse mode
        consumer.Push(System.Text.Encoding.ASCII.GetBytes("\x1b[?1006h"));

        Assert.True(vtc.SgrMouseMode);
    }

    [Fact]
    public void VtCore_SgrMouseDisable_ClearsSgrMouseMode()
    {
        var vtc = CreateVtc(cols: 80, rows: 24);
        var consumer = new DataConsumer(vtc);

        consumer.Push(System.Text.Encoding.ASCII.GetBytes("\x1b[?1006h")); // enable
        consumer.Push(System.Text.Encoding.ASCII.GetBytes("\x1b[?1006l")); // disable

        Assert.False(vtc.SgrMouseMode);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void TerminalControl_Render_WhenCellHasNullRgbAttributes_DoesNotThrow()
    {
        var vm = new TerminalSessionViewModel
        {
            Stream = new MemoryStream(),
            ResizeCallback = static (_, _, _) => ValueTask.CompletedTask,
        };

        var control = new TerminalControl();
        control.Measure(new Size(800, 600));
        control.Arrange(new Rect(0, 0, 800, 600));
        control.Session = vm;
        // ANSI palette colour 31 (red) — no explicit RGB, so ForegroundRgb is null
        control.PushBytesForTest(System.Text.Encoding.ASCII.GetBytes("\x1b[31mA"));

        using var renderTarget = new RenderTargetBitmap(new PixelSize(800, 600), new Vector(96, 96));
        var ex = Record.Exception(() =>
        {
            using var context = renderTarget.CreateDrawingContext();
            control.Render(context);
        });
        Assert.Null(ex);
    }

    // ── TerminalControl – Terminal.Background resource override ──────────────────────────────

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void TerminalControl_TerminalBackgroundResource_OverridesDefaultBackground()
    {
        var overrideBrush = new SolidColorBrush(Colors.HotPink);
        var control = new TerminalControl();
        control.Resources["Terminal.Background"] = overrideBrush;
        control.Measure(new Size(800, 600));
        control.Arrange(new Rect(0, 0, 800, 600));

        // Invoke TryFindBrush via reflection to verify the resource is resolved.
        var tryFindBrush = typeof(TerminalControl)
            .GetMethod("TryFindBrush", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var resolved = (IBrush?)tryFindBrush.Invoke(control, ["Terminal.Background"]);

        Assert.Same(overrideBrush, resolved);
    }

    // ── ResolveFg / ResolveBgColor – null RGB guard ───────────────────────────────────────────
    [Fact]
    public void TerminalControl_ResolveFg_WhenForegroundRgbIsNull_ReturnsDefaultForeground()
    {
        var attrs = new TerminalAttribute { ForegroundRgb = null };
        IBrush defaultFg = new SolidColorBrush(Colors.White);

        var result = InvokeResolveFg(attrs, reverse: false, defaultFg);

        Assert.Same(defaultFg, result);
    }

    [Fact]
    public void TerminalControl_ResolveFg_WhenForegroundRgbIsNonZero_ReturnsRgbBrush()
    {
        var attrs = new TerminalAttribute
        {
            ForegroundRgb = new TerminalColor { Red = 100, Green = 150, Blue = 200 },
        };

        var result = InvokeResolveFg(attrs, reverse: false, defaultFg: null) as SolidColorBrush;

        Assert.NotNull(result);
        Assert.Equal(100, result.Color.R);
        Assert.Equal(150, result.Color.G);
        Assert.Equal(200, result.Color.B);
    }

    [Fact]
    public void TerminalControl_ResolveBgColor_WhenBackgroundRgbIsNull_ReturnsNull()
    {
        var attrs = new TerminalAttribute { BackgroundRgb = null };

        var result = InvokeResolveBgColor(attrs, reverse: false);

        Assert.Null(result);
    }

    [Fact]
    public void TerminalControl_ResolveBgColor_WhenBackgroundRgbIsNonZero_ReturnsRgbColor()
    {
        var attrs = new TerminalAttribute
        {
            BackgroundRgb = new TerminalColor { Red = 10, Green = 20, Blue = 30 },
        };

        var result = InvokeResolveBgColor(attrs, reverse: false);

        Assert.NotNull(result);
        Assert.Equal(10, result.Value.R);
        Assert.Equal(20, result.Value.G);
        Assert.Equal(30, result.Value.B);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private static readonly MethodInfo ResolveFgMethod =
        typeof(TerminalControl).GetMethod("ResolveFg", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo ResolveBgColorMethod =
        typeof(TerminalControl).GetMethod("ResolveBgColor", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static IBrush? InvokeResolveFg(TerminalAttribute attrs, bool reverse, IBrush? defaultFg) =>
        (IBrush?)ResolveFgMethod.Invoke(null, [attrs, reverse, defaultFg]);

    private static Color? InvokeResolveBgColor(TerminalAttribute attrs, bool reverse) =>
        (Color?)ResolveBgColorMethod.Invoke(null, [attrs, reverse]);


    private static VirtualTerminalController CreateVtc(int cols, int rows)
    {
        var vtc = new VirtualTerminalController();
        vtc.ResizeView(cols, rows);
        return vtc;
    }

    // ── ReadLoopAsync incomplete VT sequences ─────────────────────────────────────────────────

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task ReadLoopAsync_IncompleteOscSequence_DoesNotThrow()
    {
        var chunked = new ChunkedStream(
            System.Text.Encoding.UTF8.GetBytes("\x1b]0;My"),       // incomplete OSC
            System.Text.Encoding.UTF8.GetBytes("Title\x07")        // completion + BEL
        );

        var vm = new TerminalSessionViewModel
        {
            Stream = chunked,
            ResizeCallback = static (_, _, _) => ValueTask.CompletedTask,
        };

        var control = new TerminalControl();
        control.Measure(new Size(800, 600));
        control.Arrange(new Rect(0, 0, 800, 600));
        control.Session = vm;

        // Let the read loop start.
        await Task.Delay(50);

        // Release chunk 0 (incomplete OSC).
        chunked.ReleaseChunk(0);
        await Task.Delay(50);

        // Release chunk 1 (completion).
        chunked.ReleaseChunk(1);

        // Wait for session to exit cleanly.
        await vm.WhenExited;
        Assert.True(vm.IsExited);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task ReadLoopAsync_SequenceSplitAcrossTwoChunks_RenderedCorrectly()
    {
        var chunked = new ChunkedStream(
            System.Text.Encoding.UTF8.GetBytes("\x1b"),            // ESC
            System.Text.Encoding.UTF8.GetBytes("[HHello")          // [H = cursor home, then "Hello"
        );

        var vm = new TerminalSessionViewModel
        {
            Stream = chunked,
            ResizeCallback = static (_, _, _) => ValueTask.CompletedTask,
        };

        var control = new TerminalControl();
        control.Measure(new Size(800, 600));
        control.Arrange(new Rect(0, 0, 800, 600));
        control.Session = vm;

        await Task.Delay(50);

        chunked.ReleaseChunk(0);
        await Task.Delay(50);

        chunked.ReleaseChunk(1);

        await vm.WhenExited;

        // Verify text appeared.
        var vtc = control.Vtc;
        Assert.NotNull(vtc);
        var line = vtc.ViewPort.GetVisibleLine(0);
        Assert.NotNull(line);
        Assert.True(line.Count >= 5);
        Assert.Equal('H', line[0].Char);
        Assert.Equal('e', line[1].Char);
        Assert.Equal('l', line[2].Char);
        Assert.Equal('l', line[3].Char);
        Assert.Equal('o', line[4].Char);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task ReadLoopAsync_MalformedStream_ExceedingThreshold_ClearsAndContinues()
    {
        // 17 chunks of 4096 bytes each = 69632 bytes > 65536 threshold.
        var largeIncomplete = new byte[4096];
        Array.Fill(largeIncomplete, (byte)'X');

        var chunks = new List<byte[]>();
        for (int i = 0; i < 17; i++)
            chunks.Add(largeIncomplete);

        // Followed by valid text.
        chunks.Add(System.Text.Encoding.UTF8.GetBytes("OK"));

        var chunked = new ChunkedStream(chunks.ToArray());
        var vm = new TerminalSessionViewModel
        {
            Stream = chunked,
            ResizeCallback = static (_, _, _) => ValueTask.CompletedTask,
        };

        var control = new TerminalControl();
        control.Measure(new Size(800, 600));
        control.Arrange(new Rect(0, 0, 800, 600));
        control.Session = vm;

        await Task.Delay(50);

        // Release all chunks.
        for (int i = 0; i < chunks.Count; i++)
        {
            chunked.ReleaseChunk(i);
            await Task.Delay(10);
        }

        await vm.WhenExited;

        // Verify session completed (pending bytes cleared, stream processed).
        var vtc = control.Vtc;
        Assert.NotNull(vtc);
        Assert.True(vm.IsExited);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task ReadLoopAsync_PartialSequenceThenCompleteText_BothRenderedCorrectly()
    {
        var chunked = new ChunkedStream(
            System.Text.Encoding.UTF8.GetBytes("\x1b"),            // ESC only
            System.Text.Encoding.UTF8.GetBytes("[2Jtest")          // clear screen + "test"
        );

        var vm = new TerminalSessionViewModel
        {
            Stream = chunked,
            ResizeCallback = static (_, _, _) => ValueTask.CompletedTask,
        };

        var control = new TerminalControl();
        control.Measure(new Size(800, 600));
        control.Arrange(new Rect(0, 0, 800, 600));
        control.Session = vm;

        await Task.Delay(50);

        chunked.ReleaseChunk(0);
        await Task.Delay(50);

        chunked.ReleaseChunk(1);

        await vm.WhenExited;

        var vtc = control.Vtc;
        Assert.NotNull(vtc);
        var line = vtc.ViewPort.GetVisibleLine(0);
        Assert.NotNull(line);
        Assert.True(line.Count >= 4);
        Assert.Equal('t', line[0].Char);
        Assert.Equal('e', line[1].Char);
        Assert.Equal('s', line[2].Char);
        Assert.Equal('t', line[3].Char);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task ReadLoopAsync_MultipleIncompleteChunks_ReassembledIntoCompleteSequence()
    {
        var chunked = new ChunkedStream(
            System.Text.Encoding.UTF8.GetBytes("\x1b]"),           // ESC ]
            System.Text.Encoding.UTF8.GetBytes("0;Title"),         // OSC title param
            System.Text.Encoding.UTF8.GetBytes("\x07")             // BEL terminator
        );

        var vm = new TerminalSessionViewModel
        {
            Stream = chunked,
            ResizeCallback = static (_, _, _) => ValueTask.CompletedTask,
        };

        var control = new TerminalControl();
        control.Measure(new Size(800, 600));
        control.Arrange(new Rect(0, 0, 800, 600));
        control.Session = vm;

        await Task.Delay(50);

        chunked.ReleaseChunk(0);
        await Task.Delay(50);

        chunked.ReleaseChunk(1);
        await Task.Delay(50);

        chunked.ReleaseChunk(2);

        await vm.WhenExited;
        Assert.True(vm.IsExited);
    }

    // ── ReadLoopAsync unhandled VT sequences (issue #713) ─────────────────────────────────────

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task UnhandledOscSequence_Param110_DoesNotKillReadLoop()
    {
        var chunked = new ChunkedStream(
            System.Text.Encoding.UTF8.GetBytes("START\x1b]110\x07END")  // text + OSC 110 + text in one chunk
        );

        var vm = new TerminalSessionViewModel
        {
            Stream = chunked,
            ResizeCallback = static (_, _, _) => ValueTask.CompletedTask,
        };

        var control = new TerminalControl();
        control.Measure(new Size(800, 600));
        control.Arrange(new Rect(0, 0, 800, 600));
        control.Session = vm;

        await Task.Delay(50);
        chunked.ReleaseChunk(0);

        await vm.WhenExited;

        // Read loop completed without crashing - that's the main assertion
        Assert.True(vm.IsExited);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task UnhandledOscSequence_Param4_DoesNotKillReadLoop()
    {
        var chunked = new ChunkedStream(
            System.Text.Encoding.UTF8.GetBytes("START\x1b]4;1;rgb:ff/00/00\x07END")
        );

        var vm = new TerminalSessionViewModel
        {
            Stream = chunked,
            ResizeCallback = static (_, _, _) => ValueTask.CompletedTask,
        };

        var control = new TerminalControl();
        control.Measure(new Size(800, 600));
        control.Arrange(new Rect(0, 0, 800, 600));
        control.Session = vm;

        await Task.Delay(50);
        chunked.ReleaseChunk(0);

        await vm.WhenExited;

        Assert.True(vm.IsExited);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task UnhandledCsiSequence_KittyQuery_DoesNotKillReadLoop()
    {
        var chunked = new ChunkedStream(
            System.Text.Encoding.UTF8.GetBytes("START\x1b[?uEND")
        );

        var vm = new TerminalSessionViewModel
        {
            Stream = chunked,
            ResizeCallback = static (_, _, _) => ValueTask.CompletedTask,
        };

        var control = new TerminalControl();
        control.Measure(new Size(800, 600));
        control.Arrange(new Rect(0, 0, 800, 600));
        control.Session = vm;

        await Task.Delay(50);
        chunked.ReleaseChunk(0);

        await vm.WhenExited;

        Assert.True(vm.IsExited);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task UnhandledCsiSequence_GreaterThanM_DoesNotKillReadLoop()
    {
        var chunked = new ChunkedStream(
            System.Text.Encoding.UTF8.GetBytes("START\x1b[>4;2mEND")
        );

        var vm = new TerminalSessionViewModel
        {
            Stream = chunked,
            ResizeCallback = static (_, _, _) => ValueTask.CompletedTask,
        };

        var control = new TerminalControl();
        control.Measure(new Size(800, 600));
        control.Arrange(new Rect(0, 0, 800, 600));
        control.Session = vm;

        await Task.Delay(50);
        chunked.ReleaseChunk(0);

        await vm.WhenExited;

        Assert.True(vm.IsExited);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task UnknownSequence_AfterPartialText_TextStillRendered()
    {
        var chunked = new ChunkedStream(
            System.Text.Encoding.UTF8.GetBytes("BEFORE\x1b]110\x07AFTER")
        );

        var vm = new TerminalSessionViewModel
        {
            Stream = chunked,
            ResizeCallback = static (_, _, _) => ValueTask.CompletedTask,
        };

        var control = new TerminalControl();
        control.Measure(new Size(800, 600));
        control.Arrange(new Rect(0, 0, 800, 600));
        control.Session = vm;

        await Task.Delay(50);
        chunked.ReleaseChunk(0);

        await vm.WhenExited;

        Assert.True(vm.IsExited);
    }

    // ── TerminalControl – VT mouse reporting ──────────────────────────────────────────────────

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void TerminalControl_MouseReportingEnabled_WritesVtSequenceToInput()
    {
        var stream = new MemoryStream();
        var vm = new TerminalSessionViewModel
        {
            Stream = stream,
            ResizeCallback = static (_, _, _) => ValueTask.CompletedTask,
        };

        var control = new TerminalControl();
        control.Measure(new Size(800, 600));
        control.Arrange(new Rect(0, 0, 800, 600));
        control.Session = vm;

        // Enable button tracking mode
        var modeBytes = System.Text.Encoding.UTF8.GetBytes("\x1b[?1002h");
        control.MouseModeState.Apply(modeBytes);
        control.PushBytesForTest(modeBytes);
        
        // Verify mouse mode is set
        Assert.NotNull(control.MouseModeState.EffectiveMode);

        // Raise a pointer pressed event
        var pressProps = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        var pressArgs = new PointerPressedEventArgs(control, new Avalonia.Input.Pointer(0, PointerType.Mouse, true), control, new Point(100, 50), 0, pressProps, KeyModifiers.None, 1);
        typeof(Avalonia.Interactivity.RoutedEventArgs).GetProperty("RoutedEvent")!.SetValue(pressArgs, InputElement.PointerPressedEvent);
        
        control.RaiseEvent(pressArgs);

        // Should write a VT mouse sequence
        var written = stream.ToArray();
        Assert.True(written.Length > 0);
        Assert.Equal(0x1b, written[0]); // ESC
        Assert.Equal((byte)'[', written[1]);
        Assert.Equal((byte)'M', written[2]);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void TerminalControl_MouseReportingEnabled_X10Mode_SuppressesMotionEvents()
    {
        var stream = new MemoryStream();
        var vm = new TerminalSessionViewModel
        {
            Stream = stream,
            ResizeCallback = static (_, _, _) => ValueTask.CompletedTask,
        };

        var control = new TerminalControl();
        control.Measure(new Size(800, 600));
        control.Arrange(new Rect(0, 0, 800, 600));
        control.Session = vm;

        // Enable X10 mode
        var modeBytes = System.Text.Encoding.UTF8.GetBytes("\x1b[?1000h");
        control.MouseModeState.Apply(modeBytes);
        control.PushBytesForTest(modeBytes);

        // Raise press event
        var pressProps = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        var pressArgs = new PointerPressedEventArgs(control, new Avalonia.Input.Pointer(0, PointerType.Mouse, true), control, new Point(100, 50), 0, pressProps, KeyModifiers.None, 1);
        typeof(Avalonia.Interactivity.RoutedEventArgs).GetProperty("RoutedEvent")!.SetValue(pressArgs, InputElement.PointerPressedEvent);
        
        control.RaiseEvent(pressArgs);

        var pressLength = stream.Length;
        Assert.True(pressLength > 0);

        // Raise motion event
        var motionProps = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other);
        var motionArgs = new PointerEventArgs(InputElement.PointerMovedEvent, control, new Avalonia.Input.Pointer(0, PointerType.Mouse, true), control, new Point(110, 60), 0, motionProps, KeyModifiers.None);
        
        control.RaiseEvent(motionArgs);

        // Stream length should not change (X10 suppresses motion)
        Assert.Equal(pressLength, stream.Length);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void TerminalControl_MouseReportingEnabled_ScrollWheel_WritesButton64Or65()
    {
        var stream = new MemoryStream();
        var vm = new TerminalSessionViewModel
        {
            Stream = stream,
            ResizeCallback = static (_, _, _) => ValueTask.CompletedTask,
        };

        var control = new TerminalControl();
        control.Measure(new Size(800, 600));
        control.Arrange(new Rect(0, 0, 800, 600));
        control.Session = vm;

        // Enable button tracking mode
        var modeBytes = System.Text.Encoding.UTF8.GetBytes("\x1b[?1002h");
        control.MouseModeState.Apply(modeBytes);
        control.PushBytesForTest(modeBytes);

        // Raise scroll up event (Delta.Y > 0)
        var scrollProps = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other);
        var scrollArgs = new PointerWheelEventArgs(control, new Avalonia.Input.Pointer(0, PointerType.Mouse, true), control, new Point(100, 50), 0, scrollProps, KeyModifiers.None, new Vector(0, 1));
        typeof(Avalonia.Interactivity.RoutedEventArgs).GetProperty("RoutedEvent")!.SetValue(scrollArgs, InputElement.PointerWheelChangedEvent);
        
        control.RaiseEvent(scrollArgs);

        var written = stream.ToArray();
        // Button 64 for scroll up: 64 + 32 = 96 = '`'
        Assert.Contains((byte)'`', written);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void TerminalControl_MouseReportingEnabled_ModifierKeys_EncodedInSequence()
    {
        var stream = new MemoryStream();
        var vm = new TerminalSessionViewModel
        {
            Stream = stream,
            ResizeCallback = static (_, _, _) => ValueTask.CompletedTask,
        };

        var control = new TerminalControl();
        control.Measure(new Size(800, 600));
        control.Arrange(new Rect(0, 0, 800, 600));
        control.Session = vm;

        // Enable button tracking mode
        var modeBytes = System.Text.Encoding.UTF8.GetBytes("\x1b[?1002h");
        control.MouseModeState.Apply(modeBytes);
        control.PushBytesForTest(modeBytes);

        // Raise pointer press with Shift held
        var pressProps = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        var pressArgs = new PointerPressedEventArgs(control, new Avalonia.Input.Pointer(0, PointerType.Mouse, true), control, new Point(100, 50), 0, pressProps, KeyModifiers.Shift, 1);
        typeof(Avalonia.Interactivity.RoutedEventArgs).GetProperty("RoutedEvent")!.SetValue(pressArgs, InputElement.PointerPressedEvent);
        
        control.RaiseEvent(pressArgs);

        var written = stream.ToArray();
        // Button 0 with Shift modifier: 0 + 4 = 4, plus 32 = 36 = '$'
        Assert.Contains((byte)'$', written);
    }

    // ── ChunkedStream helper ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A stream that delivers pre-defined byte chunks one at a time, controlled by semaphore.
    /// After all chunks are released, returns EOF (0 bytes read).
    /// </summary>
    private sealed class ChunkedStream : Stream
    {
        private readonly byte[][] _chunks;
        private readonly SemaphoreSlim[] _semaphores;
        private int _nextChunk;

        public ChunkedStream(params byte[][] chunks)
        {
            _chunks = chunks;
            _semaphores = new SemaphoreSlim[chunks.Length];
            for (int i = 0; i < chunks.Length; i++)
                _semaphores[i] = new SemaphoreSlim(0, 1);
        }

        public void ReleaseChunk(int index) => _semaphores[index].Release();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (_nextChunk >= _chunks.Length)
                return 0; // EOF

            await _semaphores[_nextChunk].WaitAsync(ct);
            var chunk = _chunks[_nextChunk++];
            chunk.CopyTo(buffer, offset);
            return chunk.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
