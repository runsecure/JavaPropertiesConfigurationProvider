using System.Buffers;
using System.Text;

namespace Microsoft.Extensions.Configuration.JavaProperties;

/// <summary>
/// A forward-only, span-based writer that produces UTF-8 encoded Java <c>.properties</c> output,
/// automatically escaping characters that require it so the result round-trips through
/// <see cref="JavaPropertiesReader"/>.
/// </summary>
public ref struct JavaPropertiesWriter
{
    private readonly IBufferWriter<byte>? _bufferWriter;
    private readonly Stream? _stream;
    private readonly JavaPropertiesWriterOptions _options;

    private byte[]? _streamBuffer;
    private int _streamBufferLength;

    private readonly byte _separator;
    private readonly byte[] _newLineBytes;

    /// <summary>Constructs a writer over an <see cref="IBufferWriter{Byte}"/>.</summary>
    public JavaPropertiesWriter(IBufferWriter<byte> bufferWriter, JavaPropertiesWriterOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(bufferWriter);
        _bufferWriter = bufferWriter;
        _stream = null;
        _options = NormalizeOptions(options);
        _separator = (byte)_options.KeyValueSeparator;
        _newLineBytes = Encoding.UTF8.GetBytes(_options.NewLine);
        _streamBuffer = null;
        _streamBufferLength = 0;
    }

    /// <summary>Constructs a writer over a <see cref="Stream"/>.</summary>
    public JavaPropertiesWriter(Stream stream, JavaPropertiesWriterOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _bufferWriter = null;
        _stream = stream;
        _options = NormalizeOptions(options);
        _separator = (byte)_options.KeyValueSeparator;
        _newLineBytes = Encoding.UTF8.GetBytes(_options.NewLine);
        _streamBuffer = new byte[4096];
        _streamBufferLength = 0;
    }

    private static JavaPropertiesWriterOptions NormalizeOptions(JavaPropertiesWriterOptions options)
    {
        if (options.NewLine is null)
        {
            options.NewLine = "\n";
        }
        if (options.KeyValueSeparator != '=' && options.KeyValueSeparator != ':')
        {
            options.KeyValueSeparator = '=';
        }
        return options;
    }

    /// <summary>Writes a single key/value property, escaping characters as necessary.</summary>
    public void WriteProperty(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        WriteEscaped(key, isKey: true);
        WriteByte(_separator);
        WriteEscaped(value, isKey: false);
        WriteBytes(_newLineBytes);
    }

    /// <summary>Writes a comment line. Any embedded line breaks are replaced with a single space.</summary>
    public void WriteComment(string comment)
    {
        ArgumentNullException.ThrowIfNull(comment);

        WriteByte((byte)'#');
        if (comment.Length > 0)
        {
            WriteByte((byte)' ');
        }

        foreach (char c in comment)
        {
            if (c is '\r' or '\n')
            {
                WriteByte((byte)' ');
            }
            else
            {
                WriteUtf8Char(c);
            }
        }

        WriteBytes(_newLineBytes);
    }

    /// <summary>Flushes any buffered output to the underlying <see cref="Stream"/> or <see cref="IBufferWriter{Byte}"/>.</summary>
    public void Flush()
    {
        if (_stream is not null && _streamBufferLength > 0)
        {
            _stream.Write(_streamBuffer.AsSpan(0, _streamBufferLength));
            _streamBufferLength = 0;
        }
        _stream?.Flush();
    }

    /// <summary>Flushes any buffered output. Equivalent to calling <see cref="Flush"/>.</summary>
    public void Dispose() => Flush();

    private void WriteEscaped(string text, bool isKey)
    {
        int runStart = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            ReadOnlySpan<byte> escape = default;

            switch (c)
            {
                case '\\': escape = "\\\\"u8; break;
                case ':': escape = "\\:"u8; break;
                case '=': escape = "\\="u8; break;
                case ' ': escape = "\\ "u8; break;
                case '\t': escape = "\\t"u8; break;
                case '\n': escape = "\\n"u8; break;
                case '\r': escape = "\\r"u8; break;
                case '\f': escape = "\\f"u8; break;
                case '#' when isKey && i == 0: escape = "\\#"u8; break;
                case '!' when isKey && i == 0: escape = "\\!"u8; break;
                default:
                    if (char.IsControl(c))
                    {
                        FlushRun(text, ref runStart, i);
                        WriteUnicodeEscape(c);
                        runStart = i + 1;
                        continue;
                    }
                    continue;
            }

            FlushRun(text, ref runStart, i);
            WriteBytes(escape);
            runStart = i + 1;
        }

        FlushRun(text, ref runStart, text.Length);
    }

    private void FlushRun(string text, ref int runStart, int end)
    {
        if (end <= runStart)
        {
            return;
        }
        WriteUtf8(text.AsSpan(runStart, end - runStart));
    }

    private void WriteUnicodeEscape(char c)
    {
        Span<byte> buf = stackalloc byte[6];
        buf[0] = (byte)'\\';
        buf[1] = (byte)'u';
        WriteHex4((ushort)c, buf.Slice(2, 4));
        WriteBytes(buf);
    }

    private static void WriteHex4(ushort value, Span<byte> dest)
    {
        const string hexDigits = "0123456789abcdef";
        dest[0] = (byte)hexDigits[(value >> 12) & 0xF];
        dest[1] = (byte)hexDigits[(value >> 8) & 0xF];
        dest[2] = (byte)hexDigits[(value >> 4) & 0xF];
        dest[3] = (byte)hexDigits[value & 0xF];
    }

    private void WriteUtf8Char(char c)
    {
        Span<char> chars = stackalloc char[1] { c };
        WriteUtf8(chars);
    }

    private void WriteUtf8(scoped ReadOnlySpan<char> chars)
    {
        const int MaxCharsPerChunk = 64;
        Span<byte> tmp = stackalloc byte[MaxCharsPerChunk * 4];

        while (!chars.IsEmpty)
        {
            int take = Math.Min(chars.Length, MaxCharsPerChunk);
            ReadOnlySpan<char> slice = chars.Slice(0, take);
            int written = Encoding.UTF8.GetBytes(slice, tmp);
            WriteBytes(tmp.Slice(0, written));
            chars = chars.Slice(take);
        }
    }

    private void WriteByte(byte b)
    {
        Span<byte> single = stackalloc byte[1] { b };
        WriteBytes(single);
    }

    private void WriteBytes(scoped ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        if (_bufferWriter is not null)
        {
            Span<byte> span = _bufferWriter.GetSpan(bytes.Length);
            bytes.CopyTo(span);
            _bufferWriter.Advance(bytes.Length);
            return;
        }

        // Stream-backed path: buffer, flushing to the stream as needed.
        if (bytes.Length >= _streamBuffer!.Length)
        {
            if (_streamBufferLength > 0)
            {
                _stream!.Write(_streamBuffer.AsSpan(0, _streamBufferLength));
                _streamBufferLength = 0;
            }
            _stream!.Write(bytes);
            return;
        }

        if (_streamBufferLength + bytes.Length > _streamBuffer.Length)
        {
            _stream!.Write(_streamBuffer.AsSpan(0, _streamBufferLength));
            _streamBufferLength = 0;
        }

        bytes.CopyTo(_streamBuffer.AsSpan(_streamBufferLength));
        _streamBufferLength += bytes.Length;
    }
}
