using System.Globalization;
using System.Text.Json;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Llm.Core.Tests.Secrets;

public class CanonicalJsonTests
{
    private static string Encode(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return CanonicalJson.Encode(doc.RootElement);
    }

    [Fact]
    public void Encode_ObjectWithReorderedKeys_ProducesSameOutput()
    {
        var a = Encode("{\"b\":1,\"a\":2,\"c\":3}");
        var b = Encode("{\"c\":3,\"a\":2,\"b\":1}");

        Assert.Equal(a, b);
        Assert.Equal("{\"a\":2,\"b\":1,\"c\":3}", a);
    }

    [Fact]
    public void Encode_NestedObjects_SortsAtEveryLevel()
    {
        var a = Encode("{\"z\":{\"y\":1,\"x\":2},\"a\":{\"c\":3,\"b\":4}}");

        Assert.Equal("{\"a\":{\"b\":4,\"c\":3},\"z\":{\"x\":2,\"y\":1}}", a);
    }

    [Fact]
    public void Encode_NumberFormat_UsesInvariantCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // A culture that would render decimals with a comma if culture leaked in.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var encoded = Encode("{\"value\":1234.5}");
            Assert.Equal("{\"value\":1234.5}", encoded);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Encode_WhitespaceVariants_ProducesSameOutput()
    {
        var compact = Encode("{\"a\":1,\"b\":[2,3]}");
        var spaced = Encode("{\n  \"a\" : 1 ,\n  \"b\" : [ 2 , 3 ]\n}");

        Assert.Equal(compact, spaced);
        Assert.Equal("{\"a\":1,\"b\":[2,3]}", compact);
    }

    [Fact]
    public void Encode_PreservesArrayOrderAndScalarTypes()
    {
        var encoded = Encode("{\"arr\":[3,1,2],\"s\":\"x\",\"t\":true,\"f\":false,\"n\":null}");

        Assert.Equal("{\"arr\":[3,1,2],\"f\":false,\"n\":null,\"s\":\"x\",\"t\":true}", encoded);
    }
}
