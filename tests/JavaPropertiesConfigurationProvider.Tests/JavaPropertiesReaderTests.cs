using System.Text;
using Microsoft.Extensions.Configuration.JavaProperties;
using Xunit;

namespace JavaPropertiesConfigurationProvider.Tests;

public class JavaPropertiesReaderTests
{
    private readonly record struct Token(JavaPropertiesTokenType Type, string? Key, string Text);

    private static List<Token> ReadAll(string content)
    {
        byte[] data = Encoding.UTF8.GetBytes(content);
        var results = new List<Token>();
        var reader = new JavaPropertiesReader(data, isFinalBlock: true);
        while (reader.Read())
        {
            string? key = reader.TokenType == JavaPropertiesTokenType.PropertyValue ? reader.GetKeyString() : null;
            results.Add(new Token(reader.TokenType, key, reader.GetString()));
            if (reader.TokenType == JavaPropertiesTokenType.EndOfFile)
            {
                break;
            }
        }
        return results;
    }

    private static List<Token> ReadAllChunked(string content, int feedChunk)
    {
        byte[] full = Encoding.UTF8.GetBytes(content);
        var results = new List<Token>();
        var state = new JavaPropertiesReaderState();
        long fed = 0;
        int guard = 0;

        while (true)
        {
            if (++guard > 100_000)
            {
                throw new InvalidOperationException("Chunked read did not terminate.");
            }

            fed = Math.Min(full.Length, fed + Math.Max(1, feedChunk));
            bool isFinalBlock = fed >= full.Length;
            var window = full.AsSpan((int)state.BytesConsumed, (int)(fed - state.BytesConsumed));

            var reader = new JavaPropertiesReader(window, isFinalBlock, state);
            bool reachedEnd = false;
            while (reader.Read())
            {
                string? key = reader.TokenType == JavaPropertiesTokenType.PropertyValue ? reader.GetKeyString() : null;
                results.Add(new Token(reader.TokenType, key, reader.GetString()));
                if (reader.TokenType == JavaPropertiesTokenType.EndOfFile)
                {
                    reachedEnd = true;
                    break;
                }
            }

            state = reader.CurrentState;

            if (reachedEnd)
            {
                return results;
            }

            if (isFinalBlock)
            {
                throw new InvalidOperationException("Final block reached but reader never produced EndOfFile.");
            }
        }
    }

    private static void AssertChunkedMatchesFull(string content)
    {
        List<Token> expected = ReadAll(content);
        foreach (int chunkSize in new[] { 1, 2, 3, 4, 5, 7, 11 })
        {
            List<Token> actual = ReadAllChunked(content, chunkSize);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void EmptyInput_ProducesOnlyEndOfFile()
    {
        List<Token> tokens = ReadAll(string.Empty);
        Assert.Single(tokens);
        Assert.Equal(JavaPropertiesTokenType.EndOfFile, tokens[0].Type);
    }

    [Fact]
    public void SingleProperty_EqualsSeparator()
    {
        List<Token> tokens = ReadAll("server.port=8080");
        Assert.Equal(JavaPropertiesTokenType.PropertyName, tokens[0].Type);
        Assert.Equal("server.port", tokens[0].Text);
        Assert.Equal(JavaPropertiesTokenType.PropertyValue, tokens[1].Type);
        Assert.Equal("8080", tokens[1].Text);
        Assert.Equal(JavaPropertiesTokenType.EndOfFile, tokens[2].Type);
    }

    [Theory]
    [InlineData("key=value", "key", "value")]
    [InlineData("key = value", "key", "value")]
    [InlineData("key:value", "key", "value")]
    [InlineData("key : value", "key", "value")]
    [InlineData("key value", "key", "value")]
    [InlineData("  key=value", "key", "value")]
    [InlineData("key=", "key", "")]
    [InlineData("key", "key", "")]
    public void SeparatorVariants(string content, string expectedKey, string expectedValue)
    {
        List<Token> tokens = ReadAll(content);
        Assert.Equal("PropertyName", tokens[0].Type.ToString());
        Assert.Equal(expectedKey, tokens[0].Text);
        Assert.Equal(expectedValue, tokens[1].Text);
    }

    [Fact]
    public void MultipleProperties()
    {
        List<Token> tokens = ReadAll("a=1\nb=2\nc=3\n");
        var props = tokens.Where(t => t.Type == JavaPropertiesTokenType.PropertyValue).ToList();
        Assert.Equal(3, props.Count);
        Assert.Equal(("a", "1"), (props[0].Key, props[0].Text));
        Assert.Equal(("b", "2"), (props[1].Key, props[1].Text));
        Assert.Equal(("c", "3"), (props[2].Key, props[2].Text));
    }

    [Theory]
    [InlineData("# comment\nkey=value")]
    [InlineData("! comment\nkey=value")]
    [InlineData("   # indented comment\nkey=value")]
    public void Comments_AreRecognized(string content)
    {
        List<Token> tokens = ReadAll(content);
        Assert.Equal(JavaPropertiesTokenType.Comment, tokens[0].Type);
        Assert.Equal(JavaPropertiesTokenType.PropertyName, tokens[1].Type);
    }

    [Fact]
    public void BlankLines_AreSkipped()
    {
        List<Token> tokens = ReadAll("\n\n   \nkey=value\n\n");
        Assert.Equal(JavaPropertiesTokenType.PropertyName, tokens[0].Type);
    }

    [Theory]
    [InlineData(@"key=hello\ world", "hello world")]
    [InlineData(@"value=a=b", "a=b")]
    [InlineData(@"value=a\:b", "a:b")]
    [InlineData(@"value=a\=b", "a=b")]
    [InlineData("value=a\\tb", "a\tb")]
    [InlineData("value=a\\nb", "a\nb")]
    [InlineData("value=a\\rb", "a\rb")]
    [InlineData("value=a\\fb", "a\fb")]
    [InlineData(@"value=a\\b", "a\\b")]
    public void Escapes_InValue(string content, string expectedValue)
    {
        List<Token> tokens = ReadAll(content);
        Assert.Equal(expectedValue, tokens[1].Text);
    }

    [Fact]
    public void Escapes_InKey_SeparatorCharactersCanBeEscaped()
    {
        List<Token> tokens = ReadAll(@"a\:b=c");
        Assert.Equal("a:b", tokens[0].Text);
        Assert.Equal("c", tokens[1].Text);
    }

    [Fact]
    public void UnicodeEscape_Basic()
    {
        List<Token> tokens = ReadAll(@"key=A");
        Assert.Equal("A", tokens[1].Text);
    }

    [Fact]
    public void UnicodeEscape_SurrogatePair()
    {
        List<Token> tokens = ReadAll(@"key=😀");
        Assert.Equal("\U0001F600", tokens[1].Text);
    }

    [Fact]
    public void MalformedUnicodeEscape_Throws()
    {
        var ex = Assert.Throws<JavaPropertiesException>(() => ReadAll(@"key=\u12G4"));
        Assert.True(ex.LineNumber >= 0);
    }

    [Fact]
    public void DanglingEscape_Throws()
    {
        Assert.Throws<JavaPropertiesException>(() => ReadAll(@"value=\"));
    }

    [Fact]
    public void UnpairedHighSurrogate_Throws()
    {
        Assert.Throws<JavaPropertiesException>(() => ReadAll(@"key=\uD83Dx"));
    }

    [Fact]
    public void UnpairedLowSurrogate_Throws()
    {
        Assert.Throws<JavaPropertiesException>(() => ReadAll(@"key=\uDE00"));
    }

    [Fact]
    public void LineContinuation_JoinsLines_StrippingLeadingWhitespace()
    {
        // Java discards the backslash, the line terminator, and ALL leading whitespace
        // on the continuation line -- no separator is inserted.
        List<Token> tokens = ReadAll("message=hello\\\n world");
        Assert.Equal("helloworld", tokens[1].Text);
    }

    [Fact]
    public void LineContinuation_StripsAllLeadingWhitespace()
    {
        List<Token> tokens = ReadAll("message=hello\\\n     world");
        Assert.Equal("helloworld", tokens[1].Text);
    }

    [Fact]
    public void LineContinuation_EscapedLeadingSpaceIsPreserved()
    {
        List<Token> tokens = ReadAll("message=hello\\\n\\ world");
        Assert.Equal("hello world", tokens[1].Text);
    }

    [Fact]
    public void LineContinuation_MultipleLines()
    {
        List<Token> tokens = ReadAll("message=a\\\nb\\\nc");
        Assert.Equal("abc", tokens[1].Text);
    }

    [Fact]
    public void DuplicateKeys_LastWinsWhenUsingReadProperty()
    {
        byte[] data = Encoding.UTF8.GetBytes("port=8080\nport=9090\n");
        var reader = new JavaPropertiesReader(data, isFinalBlock: true);
        var dict = new Dictionary<string, string>();
        while (reader.ReadProperty())
        {
            dict[reader.GetKeyString()] = reader.GetString();
        }
        Assert.Equal("9090", dict["port"]);
    }

    [Fact]
    public void ReadProperty_ExposesKeyAndValueTogether()
    {
        byte[] data = Encoding.UTF8.GetBytes("server.port=8080\ndb.timeout=30\n");
        var reader = new JavaPropertiesReader(data, isFinalBlock: true);

        Assert.True(reader.ReadProperty());
        Assert.Equal("server.port", reader.GetKeyString());
        Assert.Equal("8080", reader.GetString());

        Assert.True(reader.ReadProperty());
        Assert.Equal("db.timeout", reader.GetKeyString());
        Assert.Equal("30", reader.GetString());

        Assert.False(reader.ReadProperty());
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    public void GetBoolean_ParsesValue(string content, bool expected)
    {
        byte[] data = Encoding.UTF8.GetBytes($"k={content}");
        var reader = new JavaPropertiesReader(data, isFinalBlock: true);
        Assert.True(reader.ReadProperty());
        Assert.Equal(expected, reader.GetBoolean());
    }

    [Fact]
    public void GetInt32AndInt64AndDouble()
    {
        byte[] data = Encoding.UTF8.GetBytes("i=42\nl=9999999999\nd=3.14\n");
        var reader = new JavaPropertiesReader(data, isFinalBlock: true);

        Assert.True(reader.ReadProperty());
        Assert.Equal(42, reader.GetInt32());

        Assert.True(reader.ReadProperty());
        Assert.Equal(9999999999L, reader.GetInt64());

        Assert.True(reader.ReadProperty());
        Assert.Equal(3.14, reader.GetDouble());
    }

    [Theory]
    [InlineData("a=1\nb=2\nc=3\n")]
    [InlineData("# comment\nkey=value\n! another\nfoo:bar\n")]
    [InlineData("key=hello\\ world\nkey2=\\u0041\\uD83D\\uDE00\n")]
    [InlineData("message=hello\\\n world\nnext=1\n")]
    [InlineData("a\\:b\\=c=value\n")]
    [InlineData("\n\n  \n# c\n\nkey=v\n\n")]
    public void ChunkedReading_MatchesFullBufferReading(string content)
    {
        AssertChunkedMatchesFull(content);
    }

    [Fact]
    public void NoTrailingNewline_StillProducesProperty()
    {
        List<Token> tokens = ReadAll("key=value");
        Assert.Equal("value", tokens[1].Text);
        Assert.Equal(JavaPropertiesTokenType.EndOfFile, tokens[2].Type);
    }

    [Fact]
    public void CrLf_And_Cr_And_Lf_AllRecognized()
    {
        List<Token> crlf = ReadAll("a=1\r\nb=2\r\n");
        List<Token> lf = ReadAll("a=1\nb=2\n");
        List<Token> cr = ReadAll("a=1\rb=2\r");

        Assert.Equal(lf.Select(t => (t.Type, t.Text)), crlf.Select(t => (t.Type, t.Text)));
        Assert.Equal(lf.Select(t => (t.Type, t.Text)), cr.Select(t => (t.Type, t.Text)));
    }
}
