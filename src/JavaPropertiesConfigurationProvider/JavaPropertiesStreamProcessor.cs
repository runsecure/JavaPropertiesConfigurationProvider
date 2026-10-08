using System.Text;

namespace Microsoft.Extensions.Configuration.JavaProperties;

/// <summary>
/// Drives a <see cref="JavaPropertiesReader"/> over a <see cref="Stream"/> in bounded-size chunks,
/// growing its working buffer only as far as required by the single longest key/value/comment in
/// the document. Peak memory is therefore bounded by <see cref="JavaPropertiesReaderOptions.MaxTokenSize"/>
/// rather than by total file size. UTF-16 input is detected via its byte-order mark and transcoded to
/// UTF-8 incrementally, so it is subject to the same bound.
/// </summary>
internal static class JavaPropertiesStreamProcessor
{
    public delegate void PropertyCallback(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value);

    public static void ProcessProperties(
        Stream stream,
        PropertyCallback callback,
        int initialBufferSize = 8192,
        JavaPropertiesReaderOptions options = default)
    {
        byte[] head = new byte[3];
        int headRead = 0;
        while (headRead < head.Length)
        {
            int n = stream.Read(head, headRead, head.Length - headRead);
            if (n == 0)
            {
                break;
            }
            headRead += n;
        }

        ByteOrderMark bom = Utf8Normalizer.DetectBom(head.AsSpan(0, headRead), out int bomLength);

        if (bom is ByteOrderMark.Utf16LittleEndian or ByteOrderMark.Utf16BigEndian)
        {
            Encoding sourceEncoding = bom == ByteOrderMark.Utf16LittleEndian ? Encoding.Unicode : Encoding.BigEndianUnicode;
            var remainder = new PrefixedReadStream(head.AsMemory(bomLength, headRead - bomLength), stream);
            using Stream utf8 = Encoding.CreateTranscodingStream(remainder, sourceEncoding, Encoding.UTF8, leaveOpen: true);
            ProcessChunked(utf8, ReadOnlySpan<byte>.Empty, callback, initialBufferSize, options);
            return;
        }

        int prefixStart = bom == ByteOrderMark.Utf8 ? bomLength : 0;
        ProcessChunked(stream, head.AsSpan(prefixStart, headRead - prefixStart), callback, initialBufferSize, options);
    }

    private static void ProcessChunked(
        Stream stream,
        ReadOnlySpan<byte> prefix,
        PropertyCallback callback,
        int initialBufferSize,
        JavaPropertiesReaderOptions options)
    {
        byte[] buffer = new byte[Math.Max(Math.Max(256, initialBufferSize), prefix.Length)];
        prefix.CopyTo(buffer);
        int filled = prefix.Length;

        long bufferStartOffset = 0;
        var state = new JavaPropertiesReaderState(options);
        bool endOfStream = false;

        while (true)
        {
            if (!endOfStream && filled < buffer.Length)
            {
                int read = stream.Read(buffer, filled, buffer.Length - filled);
                if (read == 0)
                {
                    endOfStream = true;
                }
                else
                {
                    filled += read;
                }
            }

            var reader = new JavaPropertiesReader(buffer.AsSpan(0, filled), endOfStream, state);
            while (reader.ReadProperty())
            {
                callback(reader.KeySpan, reader.ValueSpan);
            }

            if (reader.TokenType == JavaPropertiesTokenType.EndOfFile)
            {
                return;
            }

            state = reader.CurrentState;

            int consumedRelative = checked((int)(state.BytesConsumed - bufferStartOffset));
            int remaining = filled - consumedRelative;
            if (remaining > 0)
            {
                buffer.AsSpan(consumedRelative, remaining).CopyTo(buffer);
            }
            filled = remaining;
            bufferStartOffset = state.BytesConsumed;

            if (endOfStream)
            {
                throw new JavaPropertiesException(
                    "Unexpected end of input while parsing a property.",
                    state.BytesConsumed,
                    state.LineNumber,
                    state.BytePositionInLine);
            }

            if (filled == buffer.Length)
            {
                // The reader throws once an incomplete token exceeds MaxTokenSize, so this growth is bounded
                // to roughly twice that limit no matter how large or hostile the input is.
                Array.Resize(ref buffer, buffer.Length * 2);
            }
        }
    }

    /// <summary>A read-only stream that yields a small prefix (bytes already read while sniffing the BOM) and then the inner stream.</summary>
    private sealed class PrefixedReadStream(ReadOnlyMemory<byte> prefix, Stream inner) : Stream
    {
        private ReadOnlyMemory<byte> _prefix = prefix;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (!_prefix.IsEmpty)
            {
                int n = Math.Min(buffer.Length, _prefix.Length);
                _prefix.Span.Slice(0, n).CopyTo(buffer);
                _prefix = _prefix.Slice(n);
                return n;
            }
            return inner.Read(buffer);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
