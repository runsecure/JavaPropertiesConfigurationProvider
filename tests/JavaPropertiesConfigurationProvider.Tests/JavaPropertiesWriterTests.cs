using System.Buffers;
using System.Text;
using Microsoft.Extensions.Configuration.JavaProperties;
using Xunit;

namespace JavaPropertiesConfigurationProvider.Tests;

public class JavaPropertiesWriterTests
{
    private static string WriteToString(Action<JavaPropertiesWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new JavaPropertiesWriter(buffer);
        write(writer);
        writer.Dispose();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static Dictionary<string, string> ReadBack(string text)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        var reader = new JavaPropertiesReader(data, isFinalBlock: true);
        var result = new Dictionary<string, string>();
        while (reader.ReadProperty())
        {
            result[reader.GetKeyString()] = reader.GetString();
        }
        return result;
    }

    [Fact]
    public void WriteProperty_Basic()
    {
        string output = WriteToString(w => w.WriteProperty("server.port", "8080"));
        Assert.Equal("server.port=8080\n", output);
    }

    [Fact]
    public void WriteComment_Basic()
    {
        string output = WriteToString(w => w.WriteComment("hello"));
        Assert.Equal("# hello\n", output);
    }

    [Theory]
    [InlineData("key with space", "value")]
    [InlineData(" leadingspace", "value")]
    [InlineData("trailingspace ", "value")]
    [InlineData("key:with:colons", "value")]
    [InlineData("key=with=equals", "value")]
    [InlineData("key\\with\\backslash", "value")]
    [InlineData("#hashkey", "value")]
    [InlineData("!bangkey", "value")]
    [InlineData("normalkey", "line1\nline2")]
    [InlineData("normalkey", "tab\ttab")]
    [InlineData("normalkey", "cr\rreturn")]
    [InlineData("normalkey", "form\ffeed")]
    [InlineData("normalkey", "back\\slash")]
    [InlineData("normalkey", "col:on")]
    [InlineData("normalkey", "eq=uals")]
    [InlineData("normalkey", "unicode: café 😀")]
    [InlineData("normalkey", "control:")]
    public void RoundTrip_KeyAndValue(string key, string value)
    {
        string output = WriteToString(w => w.WriteProperty(key, value));
        Dictionary<string, string> parsed = ReadBack(output);
        Assert.Equal(value, parsed[key]);
    }

    [Fact]
    public void WriteToStream_Works()
    {
        using var ms = new MemoryStream();
        var writer = new JavaPropertiesWriter(ms);
        writer.WriteComment("a comment");
        writer.WriteProperty("a", "1");
        writer.WriteProperty("b", "2");
        writer.Dispose();

        string text = Encoding.UTF8.GetString(ms.ToArray());
        Dictionary<string, string> parsed = ReadBack(text);
        Assert.Equal("1", parsed["a"]);
        Assert.Equal("2", parsed["b"]);
        Assert.StartsWith("# a comment", text);
    }

    [Fact]
    public void WriteToStream_LargeValueExceedingInternalBuffer()
    {
        using var ms = new MemoryStream();
        var writer = new JavaPropertiesWriter(ms);
        string bigValue = new string('x', 10_000);
        writer.WriteProperty("big", bigValue);
        writer.Dispose();

        string text = Encoding.UTF8.GetString(ms.ToArray());
        Dictionary<string, string> parsed = ReadBack(text);
        Assert.Equal(bigValue, parsed["big"]);
    }

    [Fact]
    public void WriteMultipleProperties_AllRoundTrip()
    {
        var props = new (string Key, string Value)[]
        {
            ("server.port", "8080"),
            ("database.connection.timeout", "30"),
            ("app.name", "My App"),
            ("special", "a=b:c\\d"),
        };

        string output = WriteToString(w =>
        {
            foreach ((string key, string value) in props)
            {
                w.WriteProperty(key, value);
            }
        });

        Dictionary<string, string> parsed = ReadBack(output);
        foreach ((string key, string value) in props)
        {
            Assert.Equal(value, parsed[key]);
        }
    }

    [Fact]
    public void CustomSeparator_Colon()
    {
        var options = new JavaPropertiesWriterOptions { KeyValueSeparator = ':' };
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new JavaPropertiesWriter(buffer, options);
        writer.WriteProperty("key", "value");
        writer.Dispose();

        string output = Encoding.UTF8.GetString(buffer.WrittenSpan);
        Assert.Equal("key:value\n", output);
    }
}
