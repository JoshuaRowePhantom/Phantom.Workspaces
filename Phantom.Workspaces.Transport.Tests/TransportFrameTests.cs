using System.Text.Json;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Transport.Tests;

public class TransportFrameTests
{
    [Fact]
    public void TransportFrame_Serialization_RoundTrips()
    {
        var frames = new[]
        {
            new TransportFrame
            {
                Type = TransportFrame.Types.ChannelOpen,
                ChannelId = "ch-123",
                Request = JsonDocument.Parse("{}").RootElement
            },
            new TransportFrame
            {
                Type = TransportFrame.Types.ChannelOpenError,
                ChannelId = "ch-123",
                ErrorCode = "not-found",
                Message = "Channel not found"
            },
            new TransportFrame
            {
                Type = TransportFrame.Types.ChannelMessage,
                ChannelId = "ch-123",
                Payload = JsonDocument.Parse("{\"data\":\"test\"}").RootElement
            },
            new TransportFrame
            {
                Type = TransportFrame.Types.ChannelClose,
                ChannelId = "ch-123"
            },
            new TransportFrame
            {
                Type = TransportFrame.Types.StreamOpen,
                StreamId = "st-456",
                Request = JsonDocument.Parse("{}").RootElement
            },
            new TransportFrame
            {
                Type = TransportFrame.Types.StreamData,
                StreamId = "st-456",
                Data = "aGVsbG8="
            },
            new TransportFrame
            {
                Type = TransportFrame.Types.StreamClose,
                StreamId = "st-456"
            },
            new TransportFrame
            {
                Type = TransportFrame.Types.Keepalive
            },
            new TransportFrame
            {
                Type = TransportFrame.Types.TransportClose
            }
        };

        foreach (var frame in frames)
        {
            var json = JsonSerializer.Serialize(frame);
            var deserialized = JsonSerializer.Deserialize<TransportFrame>(json);

            Assert.NotNull(deserialized);
            Assert.Equal(frame.Type, deserialized.Type);
            Assert.Equal(frame.ChannelId, deserialized.ChannelId);
            Assert.Equal(frame.StreamId, deserialized.StreamId);
            Assert.Equal(frame.ErrorCode, deserialized.ErrorCode);
            Assert.Equal(frame.Message, deserialized.Message);
            Assert.Equal(frame.Data, deserialized.Data);

            if (frame.Payload.HasValue && deserialized.Payload.HasValue)
            {
                Assert.Equal(frame.Payload.Value.GetRawText(), deserialized.Payload.Value.GetRawText());
            }

            if (frame.Request.HasValue && deserialized.Request.HasValue)
            {
                Assert.Equal(frame.Request.Value.GetRawText(), deserialized.Request.Value.GetRawText());
            }
        }
    }

    [Fact]
    public void TransportFrame_Types_Constants_MatchWireStrings()
    {
        Assert.Equal("channel-open", TransportFrame.Types.ChannelOpen);
        Assert.Equal("channel-open-error", TransportFrame.Types.ChannelOpenError);
        Assert.Equal("channel-message", TransportFrame.Types.ChannelMessage);
        Assert.Equal("channel-close", TransportFrame.Types.ChannelClose);
        Assert.Equal("stream-open", TransportFrame.Types.StreamOpen);
        Assert.Equal("stream-data", TransportFrame.Types.StreamData);
        Assert.Equal("stream-close", TransportFrame.Types.StreamClose);
        Assert.Equal("keepalive", TransportFrame.Types.Keepalive);
        Assert.Equal("transport-close", TransportFrame.Types.TransportClose);
    }
}
