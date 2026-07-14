using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Gui.Shared.Controls;
using Phantom.Workspaces.Gui.Shared.Models;
using Phantom.Workspaces.Gui.Shared.ViewModels;
using Xunit;
using VtMouseMode = Phantom.Workspaces.Gui.Shared.Encoding.VtMouseMode;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Gui.Shared.Tests;

public sealed class TerminalMouseModeStateTests
{
    private static byte[] ToBytes(string escapeSequence)
    {
        return System.Text.Encoding.UTF8.GetBytes(escapeSequence);
    }

    [Fact]
    public void TerminalMouseModeState_X10Enable_SetsX10Mode()
    {
        var state = new TerminalMouseModeState();
        state.Apply(ToBytes("\x1b[?1000h"));
        Assert.Equal(VtMouseTrackingMode.X10, state.TrackingMode);
    }

    [Fact]
    public void TerminalMouseModeState_ButtonTrackingEnable_SetsButtonMode()
    {
        var state = new TerminalMouseModeState();
        state.Apply(ToBytes("\x1b[?1002h"));
        Assert.Equal(VtMouseTrackingMode.Button, state.TrackingMode);
    }

    [Fact]
    public void TerminalMouseModeState_AllMotionEnable_SetsAllMotionMode()
    {
        var state = new TerminalMouseModeState();
        state.Apply(ToBytes("\x1b[?1003h"));
        Assert.Equal(VtMouseTrackingMode.AllMotion, state.TrackingMode);
    }

    [Fact]
    public void TerminalMouseModeState_SgrExtendedEnable_SetsSgrEncoding()
    {
        var state = new TerminalMouseModeState();
        state.Apply(ToBytes("\x1b[?1006h"));
        Assert.True(state.SgrEncoding);
    }

    [Fact]
    public void TerminalMouseModeState_UrxvtEnable_SetsUrxvtEncoding()
    {
        var state = new TerminalMouseModeState();
        state.Apply(ToBytes("\x1b[?1015h"));
        Assert.True(state.UrxvtEncoding);
    }

    [Fact]
    public void TerminalMouseModeState_Disable_ClearsMode()
    {
        var state = new TerminalMouseModeState();
        state.Apply(ToBytes("\x1b[?1000h"));
        Assert.Equal(VtMouseTrackingMode.X10, state.TrackingMode);

        state.Apply(ToBytes("\x1b[?1000l"));
        Assert.Equal(VtMouseTrackingMode.None, state.TrackingMode);
    }

    [Fact]
    public void TerminalMouseModeState_LastModeWins()
    {
        var state = new TerminalMouseModeState();
        state.Apply(ToBytes("\x1b[?1000h"));
        Assert.Equal(VtMouseTrackingMode.X10, state.TrackingMode);

        state.Apply(ToBytes("\x1b[?1002h"));
        Assert.Equal(VtMouseTrackingMode.Button, state.TrackingMode);
    }

    [Fact]
    public void TerminalMouseModeState_EffectiveMode_NoneWhenNoTrackingActive()
    {
        var state = new TerminalMouseModeState();
        Assert.Null(state.EffectiveMode);
    }

    [Fact]
    public void TerminalMouseModeState_EffectiveMode_SgrWhenSgrEncodingAndButtonTracking()
    {
        var state = new TerminalMouseModeState();
        state.Apply(ToBytes("\x1b[?1002h\x1b[?1006h"));
        Assert.Equal(VtMouseMode.Sgr, state.EffectiveMode);
    }

    [Fact]
    public void TerminalMouseModeState_EffectiveMode_UrxvtWhenUrxvtEncodingAndX10()
    {
        var state = new TerminalMouseModeState();
        state.Apply(ToBytes("\x1b[?1000h\x1b[?1015h"));
        Assert.Equal(VtMouseMode.Urxvt, state.EffectiveMode);
    }

    [Fact]
    public void TerminalMouseModeState_EffectiveMode_MapsX10ToX10Mode()
    {
        var state = new TerminalMouseModeState();
        state.Apply(ToBytes("\x1b[?1000h"));
        Assert.Equal(VtMouseMode.X10, state.EffectiveMode);
    }

    [Fact]
    public void TerminalMouseModeState_EffectiveMode_MapsButtonToButtonTracking()
    {
        var state = new TerminalMouseModeState();
        state.Apply(ToBytes("\x1b[?1002h"));
        Assert.Equal(VtMouseMode.ButtonTracking, state.EffectiveMode);
    }

    [Fact]
    public void TerminalMouseModeState_EffectiveMode_MapsAllMotionToAllMotion()
    {
        var state = new TerminalMouseModeState();
        state.Apply(ToBytes("\x1b[?1003h"));
        Assert.Equal(VtMouseMode.AllMotion, state.EffectiveMode);
    }

    [Fact]
    public void TerminalMouseModeState_Reset_ClearsAllState()
    {
        var state = new TerminalMouseModeState();
        state.Apply(ToBytes("\x1b[?1002h\x1b[?1006h\x1b[?1015h"));
        Assert.Equal(VtMouseTrackingMode.Button, state.TrackingMode);
        Assert.True(state.SgrEncoding);
        Assert.True(state.UrxvtEncoding);

        state.Reset();
        Assert.Equal(VtMouseTrackingMode.None, state.TrackingMode);
        Assert.False(state.SgrEncoding);
        Assert.False(state.UrxvtEncoding);
        Assert.Null(state.EffectiveMode);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task TerminalControl_PushMouseModeEnable_UpdatesMouseModeState()
    {
        var stream = new TestStream();
        var vm = new TerminalSessionViewModel
        {
            Stream = stream,
            ResizeCallback = static (_, _, _) => ValueTask.CompletedTask,
        };

        var control = new TerminalControl { Session = vm };

        // Push button-tracking enable sequence
        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("\x1b[?1002h"));

        // Wait for read loop to process (state-driven: poll until the expected state is observed)
        var timeout = TimeSpan.FromSeconds(5);
        var deadline = DateTime.UtcNow + timeout;
        while (control.MouseModeState.TrackingMode != VtMouseTrackingMode.Button)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("TerminalControl did not update MouseModeState within timeout");
            await Task.Yield();
        }

        // Verify internal mouse mode state was updated
        Assert.Equal(VtMouseTrackingMode.Button, control.MouseModeState.TrackingMode);
    }

    /// <summary>Simple stream for testing that allows async writes to be read by the control.</summary>
    private sealed class TestStream : Stream
    {
        private readonly MemoryStream _buffer = new();
        private readonly SemaphoreSlim _dataAvailable = new(0);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await _dataAvailable.WaitAsync(cancellationToken);
            lock (_buffer)
            {
                _buffer.Position = 0;
                int read = _buffer.Read(buffer, offset, count);
                _buffer.SetLength(0);
                _buffer.Position = 0;
                return read;
            }
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            lock (_buffer)
            {
                _buffer.Write(buffer, offset, count);
            }
            _dataAvailable.Release();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
