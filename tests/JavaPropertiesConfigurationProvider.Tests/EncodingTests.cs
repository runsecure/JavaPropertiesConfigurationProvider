using System.Text;
using Microsoft.Extensions.Configuration.JavaProperties;
using Xunit;

namespace JavaPropertiesConfigurationProvider.Tests;

public class EncodingTests
{
    private const string Content = "server.port=8080\ngreeting=café\n";

    private static byte[] EncodeWithPreamble(Encoding encoding)
    {
        byte[] preamble = encoding.GetPreamble();
        byte[] body = encoding.GetBytes(Content);
        byte[] result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }

    private static Dictionary<string, string> ProcessToDictionary(Stream stream)
    {
        var results = new Dictionary<string, string>();
        JavaPropertiesStreamProcessor.ProcessProperties(stream, (k, v) =>
        {
            results[Encoding.UTF8.GetString(k)] = Encoding.UTF8.GetString(v);
        });
        return results;
    }

    [Fact]
    public void Utf8_NoBom_ParsesCorrectly()
    {
        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(Content);
        using var stream = new MemoryStream(bytes);
        Dictionary<string, string> results = ProcessToDictionary(stream);

        Assert.Equal("8080", results["server.port"]);
        Assert.Equal("café", results["greeting"]);
    }

    [Fact]
    public void Utf8_WithBom_ParsesCorrectly()
    {
        byte[] bytes = EncodeWithPreamble(new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        using var stream = new MemoryStream(bytes);
        Dictionary<string, string> results = ProcessToDictionary(stream);

        Assert.Equal("8080", results["server.port"]);
        Assert.Equal("café", results["greeting"]);
    }

    [Fact]
    public void Utf16LE_ParsesCorrectly()
    {
        byte[] bytes = EncodeWithPreamble(new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        using var stream = new MemoryStream(bytes);
        Dictionary<string, string> results = ProcessToDictionary(stream);

        Assert.Equal("8080", results["server.port"]);
        Assert.Equal("café", results["greeting"]);
    }

    [Fact]
    public void Utf16BE_ParsesCorrectly()
    {
        byte[] bytes = EncodeWithPreamble(new UnicodeEncoding(bigEndian: true, byteOrderMark: true));
        using var stream = new MemoryStream(bytes);
        Dictionary<string, string> results = ProcessToDictionary(stream);

        Assert.Equal("8080", results["server.port"]);
        Assert.Equal("café", results["greeting"]);
    }
}
