using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.JavaProperties;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace JavaPropertiesConfigurationProvider.Tests;

public class MaxTokenSizeTests
{
    private const int OneMiB = 1024 * 1024;

    /// <summary>
    /// Yields a fixed prefix followed by a repeating pattern until <paramref name="totalLength"/> bytes, without
    /// ever holding the whole stream in memory. Used to simulate oversized or hostile input cheaply.
    /// </summary>
    private sealed class GeneratedStream(byte[] prefix, byte[] pattern, long totalLength, byte[]? suffix = null) : Stream
    {
        private readonly byte[] _suffix = suffix ?? [];
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => totalLength + _suffix.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int written = 0;
            while (written < buffer.Length && _position < Length)
            {
                long p = _position;
                buffer[written++] = p < prefix.Length
                    ? prefix[p]
                    : p < totalLength
                        ? pattern[(p - prefix.Length) % pattern.Length]
                        : _suffix[p - totalLength];
                _position++;
            }
            return written;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static Dictionary<string, string> ParseStream(Stream stream, JavaPropertiesReaderOptions options = default)
    {
        var result = new Dictionary<string, string>();
        JavaPropertiesStreamProcessor.ProcessProperties(
            stream,
            (k, v) => result[Encoding.UTF8.GetString(k)] = Encoding.UTF8.GetString(v),
            options: options);
        return result;
    }

    private static bool ThrowsWhileReading(ReadOnlySpan<byte> data, JavaPropertiesReaderState state)
    {
        var reader = new JavaPropertiesReader(data, isFinalBlock: true, state);
        try
        {
            while (reader.Read()) { }
            return false;
        }
        catch (JavaPropertiesException)
        {
            return true;
        }
    }

    private static long AllocatedDuring(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // ---- Options ----

    [Fact]
    public void Options_DefaultIsOneMiB()
    {
        Assert.Equal(OneMiB, JavaPropertiesReaderOptions.DefaultMaxTokenSize);
        Assert.Equal(OneMiB, default(JavaPropertiesReaderOptions).MaxTokenSize);
        Assert.Equal(OneMiB, new JavaPropertiesReaderOptions().MaxTokenSize);
        Assert.Equal(OneMiB, new JavaPropertiesReaderState().Options.MaxTokenSize);
        Assert.Equal(OneMiB, new JavaPropertiesConfigurationSource().MaxTokenSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Options_RejectNonPositiveLimits(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JavaPropertiesReaderOptions { MaxTokenSize = value });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JavaPropertiesConfigurationSource { MaxTokenSize = value });
    }

    [Fact]
    public void Options_AreCarriedAcrossResumedReaders()
    {
        var state = new JavaPropertiesReaderState(new JavaPropertiesReaderOptions { MaxTokenSize = 10 });
        var reader = new JavaPropertiesReader("key=val"u8, isFinalBlock: false, state);
        reader.Read();
        Assert.Equal(10, reader.CurrentState.Options.MaxTokenSize);

        Assert.True(ThrowsWhileReading("key=valuetoolong\n"u8, reader.CurrentState));
    }

    // ---- Span reader ----

    [Fact]
    public void Reader_ValueExactlyAtLimit_IsAccepted()
    {
        byte[] data = Encoding.UTF8.GetBytes("k=" + new string('a', OneMiB) + "\n");
        var reader = new JavaPropertiesReader(data, isFinalBlock: true);
        Assert.True(reader.ReadProperty());
        Assert.Equal(OneMiB, reader.ValueSpan.Length);
    }

    [Fact]
    public void Reader_ValueOneByteOverLimit_IsRejectedWithPosition()
    {
        byte[] data = Encoding.UTF8.GetBytes("ok=1\nk=" + new string('a', OneMiB + 1) + "\n");
        var reader = new JavaPropertiesReader(data, isFinalBlock: true);
        Assert.True(reader.ReadProperty());

        JavaPropertiesException ex = null!;
        try { reader.ReadProperty(); }
        catch (JavaPropertiesException e) { ex = e; }

        Assert.NotNull(ex);
        Assert.Equal(1, ex.LineNumber);
        Assert.Contains("MaxTokenSize", ex.Message);
    }

    [Fact]
    public void Reader_EscapedValueOverLimit_IsRejected()
    {
        // Escapes decode to fewer bytes, but the limit applies to raw input size.
        string escaped = string.Concat(Enumerable.Repeat("\\u0041", OneMiB / 6 + 1));
        var reader = new JavaPropertiesReader(Encoding.UTF8.GetBytes("k=" + escaped), isFinalBlock: true);
        try
        {
            reader.ReadProperty();
            Assert.Fail("Expected JavaPropertiesException");
        }
        catch (JavaPropertiesException) { }
    }

    [Fact]
    public void Reader_KeyAndCommentOverLimit_AreRejected()
    {
        var small = new JavaPropertiesReaderState(new JavaPropertiesReaderOptions { MaxTokenSize = 16 });

        Assert.True(ThrowsWhileReading(Encoding.UTF8.GetBytes(new string('k', 17) + "=v"), small));
        Assert.True(ThrowsWhileReading(Encoding.UTF8.GetBytes("#" + new string('c', 17)), small));

        var okReader = new JavaPropertiesReader(Encoding.UTF8.GetBytes(new string('k', 16) + "=v\n#short\n"), true, small);
        int tokens = 0;
        while (okReader.Read()) tokens++;
        Assert.True(tokens >= 3);
    }

    [Fact]
    public void Reader_RaisedLimit_AcceptsLargerValue()
    {
        var options = new JavaPropertiesReaderOptions { MaxTokenSize = 4 * OneMiB };
        byte[] data = Encoding.UTF8.GetBytes("k=" + new string('a', 3 * OneMiB));
        var reader = new JavaPropertiesReader(data, isFinalBlock: true, new JavaPropertiesReaderState(options));
        Assert.True(reader.ReadProperty());
        Assert.Equal(3 * OneMiB, reader.ValueSpan.Length);
    }

    // ---- Stream processor: memory stays bounded for hostile input ----

    [Fact]
    public void Stream_EndlessValue_FailsFastWithBoundedMemory()
    {
        using var stream = new GeneratedStream("k="u8.ToArray(), "a"u8.ToArray(), totalLength: 512L * OneMiB);
        long allocated = AllocatedDuring(() => Assert.Throws<JavaPropertiesException>(() => ParseStream(stream)));

        Assert.True(stream.Position < 8L * OneMiB, $"Read {stream.Position} bytes before failing");
        Assert.True(allocated < 16L * OneMiB, $"Allocated {allocated} bytes");
    }

    [Theory]
    [InlineData("k=", " ")]              // whitespace after the separator
    [InlineData("#", "c")]               // endless comment
    [InlineData("k=", "a\\\n")]          // endless line continuations
    [InlineData("", "k")]                // endless key
    public void Stream_EndlessTokenVariants_FailFast(string prefix, string pattern)
    {
        using var stream = new GeneratedStream(Encoding.UTF8.GetBytes(prefix), Encoding.UTF8.GetBytes(pattern), 256L * OneMiB);
        Assert.Throws<JavaPropertiesException>(() => ParseStream(stream));
        Assert.True(stream.Position < 8L * OneMiB, $"Read {stream.Position} bytes before failing");
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData(" ")]
    public void Stream_HugeRunOfBlankInput_ParsesWithBoundedMemory(string pattern)
    {
        using var stream = new GeneratedStream([], Encoding.UTF8.GetBytes(pattern), 64L * OneMiB, suffix: "\na=1\n"u8.ToArray());

        Dictionary<string, string> result = null!;
        long allocated = AllocatedDuring(() => result = ParseStream(stream));

        Assert.Equal("1", result["a"]);
        Assert.True(allocated < 4L * OneMiB, $"Allocated {allocated} bytes");
    }

    [Fact]
    public void Stream_Utf16_EndlessValue_FailsFastInsteadOfBufferingWholeFile()
    {
        byte[] prefix = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("k=")];
        using var stream = new GeneratedStream(prefix, Encoding.Unicode.GetBytes("a"), 512L * OneMiB);

        Assert.Throws<JavaPropertiesException>(() => ParseStream(stream));
        Assert.True(stream.Position < 16L * OneMiB, $"Read {stream.Position} bytes before failing");
    }

    [Fact]
    public void Stream_Utf16_SplitAcrossChunks_ParsesCorrectly()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 5000; i++) sb.Append("key").Append(i).Append("=välue😀").Append(i).Append('\n');
        byte[] data = [.. new UnicodeEncoding(bigEndian: true, byteOrderMark: true).GetPreamble(),
                       .. Encoding.BigEndianUnicode.GetBytes(sb.ToString())];

        var result = ParseStream(new MemoryStream(data));

        Assert.Equal(5000, result.Count);
        Assert.Equal("välue😀4999", result["key4999"]);
    }

    [Fact]
    public void Stream_RaisedLimit_AcceptsLargerValue()
    {
        using var stream = new GeneratedStream("k="u8.ToArray(), "a"u8.ToArray(), 3L * OneMiB, suffix: "\n"u8.ToArray());
        var result = ParseStream(stream, new JavaPropertiesReaderOptions { MaxTokenSize = 4 * OneMiB });
        Assert.Equal(3 * OneMiB - 2, result["k"].Length);
    }

    // ---- PipeReader ----

    [Fact]
    public async Task Pipe_EndlessValue_FailsFast()
    {
        var pipe = new Pipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        long written = 0;

        Task writer = Task.Run(async () =>
        {
            byte[] chunk = Encoding.UTF8.GetBytes(new string('a', 64 * 1024));
            await pipe.Writer.WriteAsync("k="u8.ToArray());
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    FlushResult r = await pipe.Writer.WriteAsync(chunk);
                    written += chunk.Length;
                    if (r.IsCompleted) break;
                }
            }
            finally
            {
                await pipe.Writer.CompleteAsync();
            }
        });

        await Assert.ThrowsAsync<JavaPropertiesException>(() =>
            JavaPropertiesPipeReader.ProcessPropertiesAsync(pipe.Reader, (_, _) => { }, cts.Token));
        await pipe.Reader.CompleteAsync();
        await writer;

        Assert.True(written < 8L * OneMiB, $"Wrote {written} bytes before failing");
    }

    [Fact]
    public async Task Pipe_RaisedLimit_AcceptsLargerValue()
    {
        var pipe = new Pipe();
        Task writer = Task.Run(async () =>
        {
            await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("k=" + new string('a', 2 * OneMiB) + "\n"));
            await pipe.Writer.CompleteAsync();
        });

        int length = 0;
        await JavaPropertiesPipeReader.ProcessPropertiesAsync(
            pipe.Reader,
            (_, v) => length = v.Length,
            new JavaPropertiesReaderOptions { MaxTokenSize = 4 * OneMiB });
        await writer;

        Assert.Equal(2 * OneMiB, length);
    }

    // ---- Configuration provider ----

    [Fact]
    public void ConfigurationSource_DefaultLimit_RejectsOversizedFile_AndCanBeRaised()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"jpcp-limit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "big.properties"), "small=1\nbig=" + new string('a', 2 * OneMiB) + "\n");

            var ex = Assert.ThrowsAny<Exception>(() => new ConfigurationBuilder()
                .AddJavaProperties(s =>
                {
                    s.FileProvider = new PhysicalFileProvider(dir);
                    s.Path = "big.properties";
                })
                .Build());
            Assert.True(ex is JavaPropertiesException || ex.InnerException is JavaPropertiesException, ex.ToString());

            IConfigurationRoot config = new ConfigurationBuilder()
                .AddJavaProperties(s =>
                {
                    s.FileProvider = new PhysicalFileProvider(dir);
                    s.Path = "big.properties";
                    s.MaxTokenSize = 4 * OneMiB;
                })
                .Build();
            Assert.Equal(2 * OneMiB, config["big"]!.Length);
            Assert.Equal("1", config["small"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
