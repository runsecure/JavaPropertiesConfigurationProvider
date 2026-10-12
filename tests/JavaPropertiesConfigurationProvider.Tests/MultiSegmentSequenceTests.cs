using System.Buffers;
using System.Text;
using Microsoft.Extensions.Configuration.JavaProperties;
using Xunit;

namespace JavaPropertiesConfigurationProvider.Tests;

public class MultiSegmentSequenceTests
{
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

    private static ReadOnlySequence<byte> BuildMultiSegment(params string[] chunks)
    {
        Segment first = new(Encoding.UTF8.GetBytes(chunks[0]));
        Segment last = first;
        for (int i = 1; i < chunks.Length; i++)
        {
            last = last.Append(Encoding.UTF8.GetBytes(chunks[i]));
        }
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    [Fact]
    public void MultiSegmentSequence_KeySplitAcrossSegments()
    {
        ReadOnlySequence<byte> seq = BuildMultiSegment("server.po", "rt=8080\n");
        Assert.False(seq.IsSingleSegment);

        var reader = new JavaPropertiesReader(seq, isFinalBlock: true);
        try
        {
            Assert.True(reader.ReadProperty());
            Assert.Equal("server.port", reader.GetKeyString());
            Assert.Equal("8080", reader.GetString());
        }
        finally
        {
            reader.Dispose();
        }
    }

    [Fact]
    public void MultiSegmentSequence_ValueSplitAcrossManySegments()
    {
        ReadOnlySequence<byte> seq = BuildMultiSegment("a", "=", "h", "e", "l", "l", "o", "\n", "b", "=", "2", "\n");

        var reader = new JavaPropertiesReader(seq, isFinalBlock: true);
        try
        {
            var results = new Dictionary<string, string>();
            while (reader.ReadProperty())
            {
                results[reader.GetKeyString()] = reader.GetString();
            }
            Assert.Equal("hello", results["a"]);
            Assert.Equal("2", results["b"]);
        }
        finally
        {
            reader.Dispose();
        }
    }

    [Fact]
    public void MultiSegmentSequence_UnicodeEscapeSplitAcrossSegments()
    {
        ReadOnlySequence<byte> seq = BuildMultiSegment("key=\\u00", "41\n");

        var reader = new JavaPropertiesReader(seq, isFinalBlock: true);
        try
        {
            Assert.True(reader.ReadProperty());
            Assert.Equal("A", reader.GetString());
        }
        finally
        {
            reader.Dispose();
        }
    }
}
