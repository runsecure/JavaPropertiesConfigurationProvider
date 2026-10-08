using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Configuration.JavaProperties;
using Xunit;

namespace JavaPropertiesConfigurationProvider.Tests;

public class SecurityRegressionTests
{
    private static Dictionary<string, string> ParseStream(byte[] data, int initialBufferSize = 8192)
    {
        var result = new Dictionary<string, string>();
        using var ms = new MemoryStream(data);
        JavaPropertiesStreamProcessor.ProcessProperties(
            ms,
            (k, v) => result[Encoding.UTF8.GetString(k)] = Encoding.UTF8.GetString(v),
            initialBufferSize);
        return result;
    }

    private static byte[] Bytes(params object[] parts)
    {
        var list = new List<byte>();
        foreach (object p in parts)
        {
            if (p is string s) list.AddRange(Encoding.UTF8.GetBytes(s));
            else list.Add(Convert.ToByte(p));
        }
        return list.ToArray();
    }

    // ---- Finding 1: escape before a non-ASCII byte must not swallow following lines ----

    [Theory]
    [InlineData(0xC3)] // 2-byte lead with no continuation
    [InlineData(0xE2)] // 3-byte lead with no continuation
    [InlineData(0xF0)] // 4-byte lead with no continuation
    [InlineData(0x80)] // stray continuation byte
    [InlineData(0xFF)] // never valid in UTF-8
    public void EscapedInvalidUtf8_DoesNotSwallowNextProperty(int badByte)
    {
        byte[] data = Bytes("key=a\\", badByte, "\nsecret=x\n");

        var result = ParseStream(data);

        Assert.Equal("x", result["secret"]);
        Assert.DoesNotContain("\n", result["key"]);
        Assert.StartsWith("a", result["key"]);
    }

    [Fact]
    public void EscapedInvalidUtf8_AtEndOfInput_DoesNotThrow()
    {
        var result = ParseStream(Bytes("key=a\\", 0xC3));
        Assert.True(result.ContainsKey("key"));
    }

    [Fact]
    public void EscapedValidMultiByteCharacter_DropsBackslash()
    {
        var result = ParseStream(Encoding.UTF8.GetBytes("k\\é=caf\\é\\😀\nnext=1\n"));

        Assert.Equal("café😀", result["ké"]);
        Assert.Equal("1", result["next"]);
    }

    [Fact]
    public void EscapedMultiByteCharacter_SplitAcrossStreamChunks()
    {
        // Small chunk size forces the multi-byte sequence to straddle buffer boundaries.
        string content = string.Concat(Enumerable.Repeat("k=\\é\\😀\n", 200)) + "last=ok\n";
        var result = ParseStream(Encoding.UTF8.GetBytes(content), initialBufferSize: 256);

        Assert.Equal("é😀", result["k"]);
        Assert.Equal("ok", result["last"]);
    }

    [Fact]
    public void EscapedInvalidUtf8_LineNumbersStayAccurate()
    {
        byte[] data = Bytes("key=a\\", 0xC3, "\nbad=\\u12\n");

        var ex = Assert.Throws<JavaPropertiesException>(() => ParseStream(data));
        Assert.Equal(1, ex.LineNumber);
    }

    // ---- Finding 2: writer must not split surrogate pairs ----

    public static IEnumerable<object[]> SurrogateOffsets() =>
        Enumerable.Range(60, 10).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(SurrogateOffsets))]
    public void Writer_SurrogatePairAtChunkBoundary_RoundTrips(int prefixLength)
    {
        string value = new string('a', prefixLength) + "😀" + "z";
        string key = new string('k', prefixLength) + "😀";

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new JavaPropertiesWriter(buffer);
        writer.WriteProperty(key, value);
        writer.Dispose();

        var reader = new JavaPropertiesReader(buffer.WrittenSpan, isFinalBlock: true);
        Assert.True(reader.ReadProperty());
        Assert.Equal(key, reader.GetKeyString());
        Assert.Equal(value, reader.GetString());
    }

    [Fact]
    public void Writer_CommentWithSurrogatePair_IsEncodedCorrectly()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new JavaPropertiesWriter(buffer);
        writer.WriteComment("smile 😀\nnext line");
        writer.Dispose();

        Assert.Equal("# smile 😀 next line\n", Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    [Fact]
    public void Writer_SurrogatePairAtChunkBoundary_StreamBacked_RoundTrips()
    {
        string value = new string('a', 63) + "😀z";
        using var ms = new MemoryStream();
        var writer = new JavaPropertiesWriter(ms);
        writer.WriteProperty("k", value);
        writer.Dispose();

        var reader = new JavaPropertiesReader(ms.ToArray(), isFinalBlock: true);
        Assert.True(reader.ReadProperty());
        Assert.Equal(value, reader.GetString());
    }

    // ---- Finding 4: pooled buffer is released and the reader becomes inert ----

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var segment = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = segment;
            return segment;
        }
    }

    [Fact]
    public void Reader_AfterDispose_MultiSegment_NoLongerExposesData()
    {
        var first = new Segment(Encoding.UTF8.GetBytes("password=hun"));
        Segment last = first.Append(Encoding.UTF8.GetBytes("ter2\nnext=1\n"));
        var seq = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        Assert.False(seq.IsSingleSegment);

        var reader = new JavaPropertiesReader(seq, isFinalBlock: true);
        Assert.True(reader.ReadProperty());
        Assert.Equal("hunter2", reader.GetString());
        JavaPropertiesReaderState state = reader.CurrentState;

        reader.Dispose();

        Assert.True(reader.ValueSpan.IsEmpty);
        Assert.False(reader.Read());
        Assert.False(reader.ReadProperty());
        Assert.True(state.BytesConsumed > 0);

        reader.Dispose(); // idempotent
    }

    // ---- Finding 5: PipeReader.CancelPendingRead is honoured ----

    [Fact]
    public async Task PipeReader_CancelPendingRead_ThrowsOperationCanceled()
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("a=1\nb=partial"));

        var seen = new List<string>();
        Task processing = JavaPropertiesPipeReader.ProcessPropertiesAsync(
            pipe.Reader,
            (k, v) => { lock (seen) seen.Add(Encoding.UTF8.GetString(k)); });

        // Wait until the first property has been handled and the reader is waiting for more data.
        for (int i = 0; i < 200 && seen.Count == 0; i++)
        {
            await Task.Delay(10);
        }
        Assert.Contains("a", seen);

        pipe.Reader.CancelPendingRead();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing.WaitAsync(TimeSpan.FromSeconds(10)));
        await pipe.Writer.CompleteAsync();
    }

    [Fact]
    public async Task PipeReader_CancelPendingRead_LeavesUnconsumedDataInPipe()
    {
        var pipe = new Pipe();
        pipe.Reader.CancelPendingRead();
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("a=1\n"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            JavaPropertiesPipeReader.ProcessPropertiesAsync(pipe.Reader, (_, _) => { })
                .WaitAsync(TimeSpan.FromSeconds(10)));

        await pipe.Writer.CompleteAsync();
        var results = new Dictionary<string, string>();
        await JavaPropertiesPipeReader.ProcessPropertiesAsync(pipe.Reader, (k, v) =>
            results[Encoding.UTF8.GetString(k)] = Encoding.UTF8.GetString(v));

        Assert.Equal("1", results["a"]);
    }
}
