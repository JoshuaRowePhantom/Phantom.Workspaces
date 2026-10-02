using System.Text.Json;

namespace Phantom.Workspaces.Transport.Tests;

public sealed class TransportErrorDetailsTests
{
    [Fact]
    public void TransportErrorDetails_DeepAndLongCause_IsBoundedAndPreservesFirstCauses()
    {
        Exception cause = new InvalidOperationException("leaf");
        for (var i = 0; i < 12; i++)
            cause = new InvalidOperationException(new string('X', 12000), cause);
        var details = TransportErrorDetails.FromException(cause);
        var json = JsonSerializer.Serialize(details);
        Assert.True(json.Length < 40000);
        Assert.Equal(4, Count(details));
        Assert.StartsWith("XXXX", details.Inner?.Message);
        Assert.DoesNotContain("leaf", json);

        static int Count(TransportErrorDetails value)
            => value.Inner is null ? 1 : 1 + Count(value.Inner);
    }

    [Fact]
    public void TransportErrorDetails_MalformedOrMissingDetails_FallsBackToLegacyError()
    {
        Assert.Null(TransportErrorDetails.Read(JsonSerializer.SerializeToElement(
            new { type = "channel-open-error" })));
        var malformed = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["error-details"] = new { message = "missing type" },
        });
        Assert.Null(TransportErrorDetails.Read(malformed));
    }
}
