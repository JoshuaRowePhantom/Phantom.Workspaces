using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Phantom.Workspaces.Gui.Shared.Encoding;
using Phantom.Workspaces.Gui.Shared.Models;
using Phantom.Workspaces.Gui.Shared.Utilities;
using Phantom.Workspaces.Gui.Shared.ViewModels;
using VtNetCore.VirtualTerminal;
using VtNetCore.VirtualTerminal.Enums;
using VtNetCore.VirtualTerminal.Model;
using VtNetCore.XTermParser;

namespace Phantom.Workspaces.Gui.Shared.Controls;

/// <summary>
/// Represents a shell integration marker (OSC 133).
/// </summary>
internal record ShellMark(string Type, int? ExitCode = null);

/// <summary>
/// Represents a hyperlink region (OSC 8).
/// </summary>
internal record Hyperlink(string Uri);

/// <summary>
/// Avalonia terminal control. Reads output bytes from a <see cref="TerminalSessionViewModel"/>'s
/// <see cref="System.IO.Stream"/>, feeds them into VtNetCore's VT emulator, and draws the cell
/// grid via <see cref="DrawingContext"/>. Translates Avalonia key and text events into standard
/// VT input sequences written back to the stream. Maps its pixel size to columns/rows and calls
/// the session's resize delegate (debounced to avoid excessive resize events).
/// </summary>
public partial class TerminalControl : Control
{
    // ── Styled property ──────────────────────────────────────────────────────────────────────

    /// <summary>The session the control reads from and writes to.</summary>
    public static readonly StyledProperty<TerminalSessionViewModel?> SessionProperty =
        AvaloniaProperty.Register<TerminalControl, TerminalSessionViewModel?>(nameof(Session));

    public TerminalSessionViewModel? Session
    {
        get => GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    // ── Events ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Raised when a URL is detected under a Ctrl+left-click gesture.
    /// The event argument is the detected URL string.
    /// </summary>
    public event EventHandler<string>? NavigationRequested;

    // ── VtNetCore state ───────────────────────────────────────────────────────────────────────

    private VirtualTerminalController? _vtc;
    private DataConsumer? _dataConsumer;
    private ViewModelLifetime? _sessionLifetime;
    private byte[] _pendingBytes = Array.Empty<byte>();
    private readonly TerminalMouseModeState _mouseModeState = new();
    private readonly TerminalSelectionModel _selectionModel = new();

    // Exposed internally for tests so they can push bytes synchronously without the async loop.
    internal VirtualTerminalController? Vtc => _vtc;
    internal TerminalMouseModeState MouseModeState => _mouseModeState;
    internal TerminalSelectionModel SelectionModel => _selectionModel;

    // ── VT sequence handler state (issue #725) ────────────────────────────────────────────────

    internal int CursorShape { get; private set; }
    internal Dictionary<int, Color> PaletteOverrides { get; } = new();
    internal Color? DefaultFgOverride { get; private set; }
    internal Color? DefaultBgOverride { get; private set; }
    internal Color? CursorColorOverride { get; private set; }
    internal List<ShellMark> ShellMarks { get; } = new();
    internal string? CurrentWorkingDirectory { get; private set; }
    internal List<Hyperlink> Hyperlinks { get; } = new();
    internal string? Title { get; private set; }
    internal int SynchronizedOutputNestingLevel { get; private set; }

    // ── Cell metrics ──────────────────────────────────────────────────────────────────────────

    private static readonly FontFamily MonoFamily =
        new FontFamily("Cascadia Mono,Cascadia Code,Consolas,Courier New,monospace");
    private const double TermFontSize = 12.0;

    private double _cellWidth;
    private double _cellHeight;

    // ── Resize debounce ───────────────────────────────────────────────────────────────────────

    internal TimeSpan ResizeDebounceDelay { get; set; } = TimeSpan.FromMilliseconds(50);

    private CancellationTokenSource? _resizeCts;
    private bool _isDragging;

    // Test infrastructure - allows tests to override pointer position
    internal static Point? TestPointerPositionOverride;

    // ── ANSI color tables ─────────────────────────────────────────────────────────────────────

    private static readonly Color[] DimAnsi    = CampbellColorScheme.Dim;
    private static readonly Color[] BrightAnsi = CampbellColorScheme.Bright;

    // ── Static constructor ────────────────────────────────────────────────────────────────────

    static TerminalControl()
    {
        SessionProperty.Changed.AddClassHandler<TerminalControl>(static (c, e) =>
        {
            if (e.OldValue is TerminalSessionViewModel)
                c.DetachSession();
            if (e.NewValue is TerminalSessionViewModel newSession)
                c.AttachSession(newSession);
        });
        FocusableProperty.OverrideDefaultValue<TerminalControl>(true);
        ClipToBoundsProperty.OverrideDefaultValue<TerminalControl>(true);
    }

    // ── Session lifecycle ─────────────────────────────────────────────────────────────────────

    private void AttachSession(TerminalSessionViewModel session)
    {
        _vtc = new VirtualTerminalController();
        _dataConsumer = new DataConsumer(_vtc);

        MeasureCells();

        var cols = ComputeColumns();
        var rows = ComputeRows();
        if (cols > 0 && rows > 0)
            _vtc.ResizeView(cols, rows);

        _sessionLifetime = new ViewModelLifetime();
        _sessionLifetime.Run(ct => ReadLoopAsync(session, ct));
    }

    private void DetachSession()
    {
        _resizeCts?.Cancel();
        _resizeCts?.Dispose();
        _resizeCts = null;
        
        _ = _sessionLifetime?.DisposeAsync();
        _sessionLifetime = null;
        _vtc = null;
        _dataConsumer = null;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        _resizeCts?.Cancel();
        _resizeCts?.Dispose();
        _resizeCts = null;
    }

    private async Task ReadLoopAsync(TerminalSessionViewModel session, CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await session.Stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                    break;

                byte[] chunk;
                if (_pendingBytes.Length > 0)
                {
                    chunk = new byte[_pendingBytes.Length + read];
                    _pendingBytes.CopyTo(chunk, 0);
                    buffer[..read].CopyTo(chunk, _pendingBytes.Length);
                    _pendingBytes = Array.Empty<byte>();
                }
                else
                {
                    chunk = buffer[..read];
                }

                // Track mouse mode state before pushing to VT emulator
                _mouseModeState.Apply(chunk);

                // Process VT sequences we handle (issue #725)
                var interceptor = new TerminalSequenceInterceptor(this, session.Stream);
                chunk = interceptor.Process(chunk);

                try
                {
                    _dataConsumer?.Push(chunk);
                }
                catch (IndexOutOfRangeException)
                {
                    _pendingBytes = chunk.Length > 65536 ? Array.Empty<byte>() : chunk;
                    continue;
                }
                catch (Exception ex) when (ex.Message.StartsWith("There are no", StringComparison.Ordinal))
                {
                    // Unhandled VT sequence — skip and continue
                }

                await Dispatcher.UIThread.InvokeAsync(InvalidateVisual, DispatcherPriority.Render);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
        finally
        {
            session.NotifyExited();
        }
    }

    // ── Resize ────────────────────────────────────────────────────────────────────────────────

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        MeasureCells();
        ScheduleResize();
    }

    private void MeasureCells()
    {
        var tf = new FormattedText(
            "M",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(MonoFamily),
            TermFontSize,
            Brushes.White);

        _cellWidth = tf.Width;
        _cellHeight = tf.Height;
    }

    private int ComputeColumns() =>
        _cellWidth > 0 ? Math.Max(1, (int)(Bounds.Width / _cellWidth)) : 80;

    private int ComputeRows() =>
        _cellHeight > 0 ? Math.Max(1, (int)(Bounds.Height / _cellHeight)) : 24;

    private void ScheduleResize()
    {
        _resizeCts?.Cancel();
        _resizeCts = new CancellationTokenSource();
        var token = _resizeCts.Token;
        _ = Task.Delay(ResizeDebounceDelay, token).ContinueWith(
            _ => Dispatcher.UIThread.Post(ApplyResize),
            CancellationToken.None,
            TaskContinuationOptions.NotOnCanceled,
            TaskScheduler.Default);
    }

    private void ApplyResize()
    {
        if (_vtc is null || Session is null)
            return;

        var cols = ComputeColumns();
        var rows = ComputeRows();
        _vtc.ResizeView(cols, rows);
        _ = Session.ResizeCallback(cols, rows, CancellationToken.None);
    }

    // ── Rendering ─────────────────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);

        // Background
        var bgBrush = TryFindBrush("Terminal.Background") ?? new SolidColorBrush(CampbellColorScheme.Background);
        context.FillRectangle(bgBrush, bounds);

        if (_vtc is null)
            return;

        if (_cellWidth <= 0 || _cellHeight <= 0)
            MeasureCells();

        var visRows = _vtc.VisibleRows;
        var visCols = _vtc.VisibleColumns;

        var defaultFg = TryFindBrush("Terminal.Foreground") ?? new SolidColorBrush(CampbellColorScheme.Foreground);
        var cursorBrush = TryFindBrush("Terminal.Cursor") ?? Brushes.White;

        var normalTypeface = new Typeface(MonoFamily);
        var boldTypeface = new Typeface(MonoFamily, FontStyle.Normal, FontWeight.Bold);

        for (var row = 0; row < visRows; row++)
        {
            var line = _vtc.ViewPort.GetVisibleLine(row);
            if (line is null)
                continue;

            for (var col = 0; col < Math.Min(line.Count, visCols); col++)
            {
                var cell = line[col];
                if (cell is null)
                    continue;

                var x = col * _cellWidth;
                var y = row * _cellHeight;
                var cellRect = new Rect(x, y, _cellWidth, _cellHeight);
                var attrs = cell.Attributes;
                var reverse = attrs.Reverse;

                var fgBrush = ResolveFg(attrs, reverse, defaultFg);
                var bgColor = ResolveBgColor(attrs, reverse);

                if (bgColor.HasValue)
                    context.FillRectangle(new SolidColorBrush(bgColor.Value), cellRect);

                var ch = cell.Char;
                if (ch != '\0' && ch != ' ')
                {
                    var tf = attrs.Bright ? boldTypeface : normalTypeface;
                    var ft = new FormattedText(
                        ch.ToString(),
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        tf,
                        TermFontSize,
                        fgBrush);

                    context.DrawText(ft, new Point(x, y));
                }

                if (attrs.Underscore)
                {
                    var pen = new Pen(fgBrush, 1);
                    context.DrawLine(pen,
                        new Point(x, y + _cellHeight - 1),
                        new Point(x + _cellWidth, y + _cellHeight - 1));
                }
            }
        }

        // Selection highlight
        if (_selectionModel.HasSelection)
        {
            var selectionBrush = new SolidColorBrush(Color.FromArgb(128, 100, 149, 237));
            for (var row = 0; row < visRows; row++)
            {
                for (var col = 0; col < visCols; col++)
                {
                    if (_selectionModel.IsSelected(col, row))
                    {
                        var x = col * _cellWidth;
                        var y = row * _cellHeight;
                        var cellRect = new Rect(x, y, _cellWidth, _cellHeight);
                        context.FillRectangle(selectionBrush, cellRect);
                    }
                }
            }
        }

        // Cursor
        var cursor = _vtc.ViewPort.CursorPosition;
        if (cursor.Row >= 0 && cursor.Column >= 0
            && cursor.Row < visRows && cursor.Column < visCols)
        {
            context.FillRectangle(
                cursorBrush,
                new Rect(cursor.Column * _cellWidth, cursor.Row * _cellHeight, _cellWidth, _cellHeight));
        }
    }

    private IBrush? TryFindBrush(string key)
    {
        if (this.TryGetResource(key, ActualThemeVariant, out var res) && res is IBrush b)
            return b;
        return null;
    }

    private static IBrush ResolveFg(TerminalAttribute attrs, bool reverse, IBrush? defaultFg)
    {
        if (reverse)
            return new SolidColorBrush(AnsiToColor(attrs.BackgroundColor, bright: false));

        if (attrs.ForegroundRgb is { ARGB: not 0 } fgRgb)
            return new SolidColorBrush(Color.FromRgb(
                (byte)fgRgb.Red,
                (byte)fgRgb.Green,
                (byte)fgRgb.Blue));

        return defaultFg ?? new SolidColorBrush(AnsiToColor(attrs.ForegroundColor, attrs.Bright));
    }

    private static Color? ResolveBgColor(TerminalAttribute attrs, bool reverse)
    {
        if (reverse)
            return AnsiToColor(attrs.ForegroundColor, bright: false);

        if (attrs.BackgroundRgb is { ARGB: not 0 } bgRgb)
            return Color.FromRgb(
                (byte)bgRgb.Red,
                (byte)bgRgb.Green,
                (byte)bgRgb.Blue);

        // Default background — caller fills the whole rect with Terminal.Background.
        var idx = (int)attrs.BackgroundColor;
        if (idx > 0 || attrs.Reverse)
            return AnsiToColor(attrs.BackgroundColor, bright: false);

        return null;
    }

    private static Color AnsiToColor(ETerminalColor color, bool bright)
    {
        var table = bright ? BrightAnsi : DimAnsi;
        var idx = (int)color;
        return idx >= 0 && idx < table.Length ? table[idx] : (bright ? Color.FromRgb(255, 255, 255) : Color.FromRgb(0, 0, 0));
    }

    // ── Input ─────────────────────────────────────────────────────────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Session is null)
            return;

        var seq = MapKey(e.Key, e.KeyModifiers);
        if (seq is null)
            return;

        var bytes = System.Text.Encoding.UTF8.GetBytes(seq);
        _ = Session.Stream.WriteAsync(bytes).AsTask();
        e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (Session is null || string.IsNullOrEmpty(e.Text))
            return;

        var text = _vtc?.BracketedPasteMode == true && e.Text.Length > 1
            ? $"\x1b[200~{e.Text}\x1b[201~"
            : e.Text;

        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        _ = Session.Stream.WriteAsync(bytes).AsTask();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        
        if (_mouseModeState.EffectiveMode is { } mode)
        {
            SendMouseVtSequence(e.GetCurrentPoint(this), MouseEventType.Press, e.KeyModifiers, mode);
            e.Handled = true;
            return;
        }
        
        Focus();
        HandleNativeMousePress(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        
        if (_mouseModeState.EffectiveMode is { } mode)
        {
            if (!ShouldSendMotionEvent(mode, e.GetCurrentPoint(this).Properties))
                return;
            
            SendMouseVtSequence(e.GetCurrentPoint(this), MouseEventType.Motion, e.KeyModifiers, mode);
            e.Handled = true;
            return;
        }
        
        HandleNativeMouseMove(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        
        if (_mouseModeState.EffectiveMode is { } mode)
        {
            if (_mouseModeState.TrackingMode == VtMouseTrackingMode.X10)
                return;
            
            SendMouseVtSequence(e.GetCurrentPoint(this), MouseEventType.Release, e.KeyModifiers, mode);
            e.Handled = true;
            return;
        }
        
        HandleNativeMouseRelease(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        
        if (_mouseModeState.EffectiveMode is { } mode)
        {
            int button = e.Delta.Y > 0 ? 64 : 65;
            SendScrollWheelVtSequence(e.GetCurrentPoint(this), button, e.KeyModifiers, mode);
            e.Handled = true;
            return;
        }
        
        HandleNativeScroll(e);
    }

    private bool ShouldSendMotionEvent(VtMouseMode mode, PointerPointProperties props)
    {
        if (_mouseModeState.TrackingMode == VtMouseTrackingMode.X10)
            return false;
        
        if (_mouseModeState.TrackingMode == VtMouseTrackingMode.Button)
        {
            return props.IsLeftButtonPressed || props.IsMiddleButtonPressed || props.IsRightButtonPressed;
        }
        
        return true;
    }

    private void SendMouseVtSequence(PointerPoint point, MouseEventType eventType, KeyModifiers keyMods, VtMouseMode mode)
    {
        if (_vtc is null || Session is null)
            return;
        
        int col = Math.Clamp((int)(point.Position.X / _cellWidth) + 1, 1, _vtc.VisibleColumns);
        int row = Math.Clamp((int)(point.Position.Y / _cellHeight) + 1, 1, _vtc.VisibleRows);
        
        int button = DetermineButton(point.Properties, eventType);
        var modifiers = MapModifiers(keyMods);
        
        var seq = VtMouseEncoder.Encode(button, eventType, modifiers, col, row, mode);
        var bytes = System.Text.Encoding.UTF8.GetBytes(seq);
        _ = Session.Stream.WriteAsync(bytes).AsTask();
    }

    private void SendScrollWheelVtSequence(PointerPoint point, int button, KeyModifiers keyMods, VtMouseMode mode)
    {
        if (_vtc is null || Session is null)
            return;
        
        int col = Math.Clamp((int)(point.Position.X / _cellWidth) + 1, 1, _vtc.VisibleColumns);
        int row = Math.Clamp((int)(point.Position.Y / _cellHeight) + 1, 1, _vtc.VisibleRows);
        
        var modifiers = MapModifiers(keyMods);
        
        var seq = VtMouseEncoder.Encode(button, MouseEventType.Press, modifiers, col, row, mode);
        var bytes = System.Text.Encoding.UTF8.GetBytes(seq);
        _ = Session.Stream.WriteAsync(bytes).AsTask();
    }

    private static int DetermineButton(PointerPointProperties props, MouseEventType eventType)
    {
        if (eventType == MouseEventType.Motion)
        {
            if (props.IsLeftButtonPressed)
                return 0;
            if (props.IsMiddleButtonPressed)
                return 1;
            if (props.IsRightButtonPressed)
                return 2;
            return 0;
        }
        
        if (eventType == MouseEventType.Press)
        {
            if (props.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed)
                return 0;
            if (props.PointerUpdateKind == PointerUpdateKind.MiddleButtonPressed)
                return 1;
            if (props.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
                return 2;
        }
        
        if (eventType == MouseEventType.Release)
        {
            if (props.PointerUpdateKind == PointerUpdateKind.LeftButtonReleased)
                return 0;
            if (props.PointerUpdateKind == PointerUpdateKind.MiddleButtonReleased)
                return 1;
            if (props.PointerUpdateKind == PointerUpdateKind.RightButtonReleased)
                return 2;
        }
        
        return 0;
    }

    private static MouseModifiers MapModifiers(KeyModifiers keyMods)
    {
        var modifiers = MouseModifiers.None;
        
        if ((keyMods & KeyModifiers.Shift) != 0)
            modifiers |= MouseModifiers.Shift;
        if ((keyMods & KeyModifiers.Alt) != 0)
            modifiers |= MouseModifiers.Alt;
        if ((keyMods & KeyModifiers.Control) != 0)
            modifiers |= MouseModifiers.Ctrl;
        
        return modifiers;
    }

    private static string? MapKey(Key key, KeyModifiers mods)
    {
        var ctrl = (mods & KeyModifiers.Control) != 0;
        var shift = (mods & KeyModifiers.Shift) != 0;
        var alt = (mods & KeyModifiers.Alt) != 0;

        if (ctrl && !alt)
        {
            return key switch
            {
                Key.A => "\x01", Key.B => "\x02", Key.C => "\x03", Key.D => "\x04",
                Key.E => "\x05", Key.F => "\x06", Key.G => "\x07", Key.H => "\x08",
                Key.I => "\x09", Key.J => "\x0a", Key.K => "\x0b", Key.L => "\x0c",
                Key.M => "\x0d", Key.N => "\x0e", Key.O => "\x0f", Key.P => "\x10",
                Key.Q => "\x11", Key.R => "\x12", Key.S => "\x13", Key.T => "\x14",
                Key.U => "\x15", Key.V => "\x16", Key.W => "\x17", Key.X => "\x18",
                Key.Y => "\x19", Key.Z => "\x1a",
                _ => null,
            };
        }

        if (alt && !ctrl)
        {
            var inner = MapKey(key, KeyModifiers.None);
            return inner != null ? $"\x1b{inner}" : null;
        }

        return key switch
        {
            Key.Up => "\x1b[A",
            Key.Down => "\x1b[B",
            Key.Right => "\x1b[C",
            Key.Left => "\x1b[D",
            Key.Home => "\x1b[H",
            Key.End => "\x1b[F",
            Key.Insert => "\x1b[2~",
            Key.Delete => "\x1b[3~",
            Key.PageUp => "\x1b[5~",
            Key.PageDown => "\x1b[6~",
            Key.Back => "\x7f",
            Key.Tab => shift ? "\x1b[Z" : "\x09",
            Key.Enter => "\x0d",
            Key.Escape => "\x1b",
            Key.F1 => "\x1bOP", Key.F2 => "\x1bOQ", Key.F3 => "\x1bOR", Key.F4 => "\x1bOS",
            Key.F5 => "\x1b[15~", Key.F6 => "\x1b[17~", Key.F7 => "\x1b[18~",
            Key.F8 => "\x1b[19~", Key.F9 => "\x1b[20~", Key.F10 => "\x1b[21~",
            Key.F11 => "\x1b[23~", Key.F12 => "\x1b[24~",
            _ => null,
        };
    }

    // ── Native mouse selection (no VT mouse mode active) ─────────────────────────────────────

    private void HandleNativeMousePress(PointerPressedEventArgs e)
    {
        if (_vtc is null)
            return;

        var point = e.GetCurrentPoint(this);
        var props = point.Properties;
        var position = TestPointerPositionOverride ?? point.Position;
        var cell = PixelToCell(position);

        if (props.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed)
        {
            if ((e.KeyModifiers & KeyModifiers.Control) != 0)
            {
                TryOpenUrlAtCell(cell);
                e.Handled = true;
                return;
            }

            var isRectangular = (e.KeyModifiers & KeyModifiers.Alt) != 0;
            
            if ((e.KeyModifiers & KeyModifiers.Shift) != 0)
            {
                _selectionModel.ExtendSelection(cell);
            }
            else
            {
                _selectionModel.StartSelection(cell, isRectangular);
                _isDragging = true;

                if (e.ClickCount == 2)
                {
                    var lines = GetAllVisibleLines();
                    _selectionModel.ExpandToWords(lines);
                }
                else if (e.ClickCount == 3)
                {
                    _selectionModel.ExpandToLines(_vtc.VisibleRows);
                }
            }

            InvalidateVisual();
            e.Handled = true;
        }
        else if (props.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            _ = HandleRightClickAsync();
            e.Handled = true;
        }
        else if (props.PointerUpdateKind == PointerUpdateKind.MiddleButtonPressed)
        {
            _ = HandleMiddleClickAsync();
            e.Handled = true;
        }
    }

    private void HandleNativeMouseMove(PointerEventArgs e)
    {
        if (_vtc is null || !_isDragging)
            return;

        var point = e.GetCurrentPoint(this);
        var position = TestPointerPositionOverride ?? point.Position;
        var cell = PixelToCell(position);
        _selectionModel.ExtendSelection(cell);
        InvalidateVisual();
        e.Handled = true;
    }

    private void HandleNativeMouseRelease(PointerReleasedEventArgs e)
    {
        _isDragging = false;
    }

    private void HandleNativeScroll(PointerWheelEventArgs e)
    {
        if (_vtc is null)
            return;

        var delta = (int)-e.Delta.Y * 3;
        
        var maxTopRow = 0;
        for (int i = 0; ; i++)
        {
            if (_vtc.ViewPort.GetLine(i) == null)
            {
                maxTopRow = Math.Max(0, i - _vtc.VisibleRows);
                break;
            }
        }
        
        var newTopRow = _vtc.ViewPort.TopRow + delta;
        _vtc.ViewPort.TopRow = Math.Clamp(newTopRow, 0, maxTopRow);
        InvalidateVisual();
        e.Handled = true;
    }

    private async Task HandleRightClickAsync()
    {
        if (Session is null || _vtc is null)
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            _selectionModel.Clear();
            InvalidateVisual();
            return;
        }

        if (_selectionModel.HasSelection)
        {
            var lines = GetAllVisibleLines();
            var selectedText = _selectionModel.GetSelectedText(lines);
            if (!string.IsNullOrEmpty(selectedText))
            {
                var data = new DataTransfer();
                data.Add(DataTransferItem.CreateText(selectedText));
                await clipboard.SetDataAsync(data);
            }
        }

        _selectionModel.Clear();
        InvalidateVisual();

        using var clipboardData = await clipboard.TryGetDataAsync();
        if (clipboardData is not null)
        {
            var clipboardText = await clipboardData.TryGetTextAsync();
            if (!string.IsNullOrEmpty(clipboardText))
            {
                await WriteTextToSessionAsync(clipboardText);
            }
        }
    }

    private async Task HandleMiddleClickAsync()
    {
        if (Session is null)
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;

        using var clipboardData = await clipboard.TryGetDataAsync();
        if (clipboardData is not null)
        {
            var clipboardText = await clipboardData.TryGetTextAsync();
            if (!string.IsNullOrEmpty(clipboardText))
            {
                await WriteTextToSessionAsync(clipboardText);
            }
        }
    }

    private async Task WriteTextToSessionAsync(string text)
    {
        if (Session is null)
            return;

        var wrappedText = _vtc?.BracketedPasteMode == true
            ? $"\x1b[200~{text}\x1b[201~"
            : text;

        var bytes = System.Text.Encoding.UTF8.GetBytes(wrappedText);
        await Session.Stream.WriteAsync(bytes);
    }

    private void TryOpenUrlAtCell(TerminalCell cell)
    {
        if (_vtc is null || cell.Row < 0 || cell.Row >= _vtc.VisibleRows)
            return;

        var line = _vtc.ViewPort.GetVisibleLine(cell.Row);
        if (line is null)
            return;

        var sb = new StringBuilder();
        for (int i = 0; i < line.Count; i++)
        {
            var ch = line[i]?.Char ?? '\0';
            if (ch != '\0')
                sb.Append(ch);
        }

        var lineText = sb.ToString();
        var match = System.Text.RegularExpressions.Regex.Match(lineText, @"https?://[^\s]+");
        if (match.Success)
        {
            var url = match.Value;
            NavigationRequested?.Invoke(this, url);
        }
    }

    private IReadOnlyList<TerminalLine> GetAllVisibleLines()
    {
        if (_vtc is null)
            return Array.Empty<TerminalLine>();

        var lines = new List<TerminalLine>();
        for (int i = 0; i < _vtc.VisibleRows; i++)
        {
            var line = _vtc.ViewPort.GetVisibleLine(i);
            if (line is not null)
                lines.Add(line);
        }
        return lines;
    }

    private TerminalCell PixelToCell(Point position)
    {
        if (_vtc is null)
            return new TerminalCell(0, 0);

        // If cell dimensions aren't measured yet, measure now
        if (_cellWidth <= 0 || _cellHeight <= 0)
            MeasureCells();

        if (_cellWidth <= 0 || _cellHeight <= 0)
            return new TerminalCell(0, 0);

        int col = Math.Clamp((int)(position.X / _cellWidth), 0, _vtc.VisibleColumns - 1);
        int row = Math.Clamp((int)(position.Y / _cellHeight), 0, _vtc.VisibleRows - 1);
        return new TerminalCell(col, row);
    }

    // ── VT sequence handler methods (issue #725) ──────────────────────────────────────────────

    internal void SetCursorShape(int shape) => CursorShape = shape;
    internal void SetPaletteColor(int index, Color color) => PaletteOverrides[index] = color;
    internal void ResetPaletteColor(int index) => PaletteOverrides.Remove(index);
    internal void SetDefaultFg(Color color) => DefaultFgOverride = color;
    internal void ResetDefaultFg() => DefaultFgOverride = null;
    internal void SetDefaultBg(Color color) => DefaultBgOverride = color;
    internal void ResetDefaultBg() => DefaultBgOverride = null;
    internal void SetCursorColor(Color color) => CursorColorOverride = color;
    internal void ResetCursorColor() => CursorColorOverride = null;
    internal void AddShellMark(string type, int? exitCode) => ShellMarks.Add(new ShellMark(type, exitCode));
    internal void SetWorkingDirectory(string path) => CurrentWorkingDirectory = path;
    internal void OpenHyperlink(string uri) => Hyperlinks.Add(new Hyperlink(uri));
    internal void CloseHyperlink() { /* Hyperlink closed by empty URI */ }
    internal void SetTitle(string title) => Title = title;
    internal void IncrementSynchronizedOutput() => SynchronizedOutputNestingLevel++;
    internal void DecrementSynchronizedOutput()
    {
        if (SynchronizedOutputNestingLevel > 0)
            SynchronizedOutputNestingLevel--;
    }

    // ── Test hook ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pushes <paramref name="bytes"/> directly into the VtNetCore processor without going through
    /// the async read loop. For test use only.
    /// </summary>
    internal void PushBytesForTest(byte[] bytes)
    {
        if (Session is null)
            return;

        var interceptor = new TerminalSequenceInterceptor(this, Session.Stream);
        var filtered = interceptor.Process(bytes);
        _dataConsumer?.Push(filtered);
    }
}
