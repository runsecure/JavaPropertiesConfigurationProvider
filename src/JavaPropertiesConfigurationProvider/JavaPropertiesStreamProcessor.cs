using System.Text;

namespace Microsoft.Extensions.Configuration.JavaProperties;

/// <summary>
/// Drives a <see cref="JavaPropertiesReader"/> over a <see cref="Stream"/> in bounded-size chunks,
/// growing its working buffer only as far as required by the single longest key/value/comment in
/// the document. This keeps peak memory usage decoupled from total file size for the common (UTF-8)
/// case. UTF-16 input is detected via its byte-order mark and transcoded to UTF-8 up front, which
/// requires buffering the whole document in memory.
/// </summary>
internal static class JavaPropertiesStreamProcessor
{
    public delegate void PropertyCallback(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value);

    public static void ProcessProperties(Stream stream, PropertyCallback callback, int initialBufferSize = 8192)
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
            using var ms = new MemoryStream();
            ms.Write(head, bomLength, headRead - bomLength);
            stream.CopyTo(ms);
            byte[] raw = ms.ToArray();

            Encoding sourceEncoding = bom == ByteOrderMark.Utf16LittleEndian ? Encoding.Unicode : Encoding.BigEndianUnicode;
            byte[] utf8 = Encoding.UTF8.GetBytes(sourceEncoding.GetString(raw));

            ProcessAllAtOnce(utf8, callback);
            return;
        }

        int prefixStart = bom == ByteOrderMark.Utf8 ? bomLength : 0;
        ProcessChunked(stream, head.AsSpan(prefixStart, headRead - prefixStart), callback, initialBufferSize);
    }

    private static void ProcessAllAtOnce(byte[] utf8, PropertyCallback callback)
    {
        var reader = new JavaPropertiesReader(utf8, isFinalBlock: true);
        while (reader.ReadProperty())
        {
            callback(reader.KeySpan, reader.ValueSpan);
        }
    }

    private static void ProcessChunked(Stream stream, ReadOnlySpan<byte> prefix, PropertyCallback callback, int initialBufferSize)
    {
        byte[] buffer = new byte[Math.Max(Math.Max(256, initialBufferSize), prefix.Length)];
        prefix.CopyTo(buffer);
        int filled = prefix.Length;

        long bufferStartOffset = 0;
        var state = new JavaPropertiesReaderState();
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
                Array.Resize(ref buffer, buffer.Length * 2);
            }
        }
    }
}
