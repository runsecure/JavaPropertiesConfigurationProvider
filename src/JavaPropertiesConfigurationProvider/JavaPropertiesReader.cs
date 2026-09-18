using System.Buffers;

namespace Microsoft.Extensions.Configuration.JavaProperties;

/// <summary>
/// A high-performance, low-allocation, forward-only reader for UTF-8 encoded Java <c>.properties</c>
/// data. Mirrors the design philosophy of <see cref="System.Text.Json.Utf8JsonReader"/>: it is a
/// stack-only (<see langword="ref struct"/>) type that can be resumed across buffer boundaries via
/// <see cref="JavaPropertiesReaderState"/>.
/// </summary>
public ref struct JavaPropertiesReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private readonly bool _isFinalBlock;

    private readonly long _consumedBaseline;
    private int _index;
    private int _lineNumber;
    private int _bytePositionInLine;
    private JavaPropertiesReaderPhase _phase;

    private JavaPropertiesTokenType _tokenType;
    private ReadOnlySpan<byte> _valueSpan;

    private byte[]? _scratch;
    private int _scratchLength;

    private byte[]? _keyBuffer;
    private int _keyBufferLength;

    private byte[]? _rentedSequenceBuffer;

    /// <summary>
    /// Constructs a new <see cref="JavaPropertiesReader"/> over a single contiguous UTF-8 encoded buffer.
    /// </summary>
    /// <param name="utf8Data">The UTF-8 encoded properties data to read.</param>
    /// <param name="isFinalBlock">
    /// <see langword="true"/> if <paramref name="utf8Data"/> contains the remainder of the document;
    /// <see langword="false"/> if more data may follow in a subsequent buffer.
    /// </param>
    /// <param name="state">The state to resume from, or <see langword="default"/> to start a new document.</param>
    public JavaPropertiesReader(ReadOnlySpan<byte> utf8Data, bool isFinalBlock, JavaPropertiesReaderState state = default)
    {
        _buffer = utf8Data;
        _isFinalBlock = isFinalBlock;

        _consumedBaseline = state.BytesConsumedCore;
        _index = 0;
        _lineNumber = state.LineNumberCore;
        _bytePositionInLine = state.BytePositionInLineCore;
        _phase = state.Phase;

        _tokenType = JavaPropertiesTokenType.None;
        _valueSpan = default;

        _scratch = null;
        _scratchLength = 0;

        _keyBuffer = state.PendingKey;
        _keyBufferLength = state.PendingKeyLength;

        _rentedSequenceBuffer = null;
    }

    /// <summary>
    /// Constructs a new <see cref="JavaPropertiesReader"/> over a possibly multi-segment UTF-8 encoded sequence.
    /// </summary>
    /// <remarks>
    /// When <paramref name="utf8Data"/> spans more than one segment, its contents are copied into a single
    /// contiguous rented buffer so that the core tokenizer can operate on a flat span. Callers that need to
    /// avoid this copy should prefer the <see cref="JavaPropertiesReader(ReadOnlySpan{byte}, bool, JavaPropertiesReaderState)"/>
    /// overload together with their own buffering strategy (the Stream/PipeReader based helpers already do this for you).
    /// </remarks>
    public JavaPropertiesReader(in ReadOnlySequence<byte> utf8Data, bool isFinalBlock, JavaPropertiesReaderState state = default)
        : this(GetSpan(utf8Data, out byte[]? rented), isFinalBlock, state)
    {
        _rentedSequenceBuffer = rented;
    }

    private static ReadOnlySpan<byte> GetSpan(in ReadOnlySequence<byte> data, out byte[]? rented)
    {
        if (data.IsSingleSegment)
        {
            rented = null;
            return data.FirstSpan;
        }

        int length = checked((int)data.Length);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        data.CopyTo(buffer);
        rented = buffer;
        return buffer.AsSpan(0, length);
    }

    /// <summary>Releases any pooled memory rented for multi-segment input.</summary>
    public void Dispose()
    {
        if (_rentedSequenceBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_rentedSequenceBuffer);
            _rentedSequenceBuffer = null;
        }
    }

    /// <summary>The type of the most recently read token.</summary>
    public readonly JavaPropertiesTokenType TokenType => _tokenType;

    /// <summary>The raw (already unescaped) UTF-8 bytes of the current token.</summary>
    public readonly ReadOnlySpan<byte> ValueSpan => _valueSpan;

    /// <summary>
    /// Reserved for future zero-copy multi-segment support. In the current implementation token values are
    /// always materialized into a contiguous buffer and exposed via <see cref="ValueSpan"/>, so this is
    /// always empty.
    /// </summary>
    public readonly ReadOnlySequence<byte> ValueSequence => default;

    /// <summary>The total number of bytes consumed from the input so far, across all buffers fed to this reader chain.</summary>
    public readonly long BytesConsumed => _consumedBaseline + _index;

    /// <summary>The zero-based line number the reader has reached.</summary>
    public readonly int LineNumber => _lineNumber;

    /// <summary>The zero-based byte offset within <see cref="LineNumber"/> the reader has reached.</summary>
    public readonly int BytePositionInLine => _bytePositionInLine;

    /// <summary>
    /// The key bytes for the property currently positioned at a <see cref="JavaPropertiesTokenType.PropertyValue"/>
    /// token. Used together with <see cref="ValueSpan"/> by <see cref="ReadProperty"/>.
    /// </summary>
    public readonly ReadOnlySpan<byte> KeySpan => _keyBuffer is null ? default : _keyBuffer.AsSpan(0, _keyBufferLength);

    /// <summary>
    /// Captures the state required to resume reading from a subsequent buffer via
    /// <see cref="JavaPropertiesReader(ReadOnlySpan{byte}, bool, JavaPropertiesReaderState)"/>.
    /// </summary>
    public readonly JavaPropertiesReaderState CurrentState
    {
        get
        {
            byte[]? pendingKey = null;
            int pendingKeyLength = 0;
            if (_phase == JavaPropertiesReaderPhase.AfterKeyPendingValue)
            {
                pendingKey = _keyBuffer;
                pendingKeyLength = _keyBufferLength;
            }

            return new JavaPropertiesReaderState(BytesConsumed, _lineNumber, _bytePositionInLine, _phase, pendingKey, pendingKeyLength);
        }
    }

    /// <summary>
    /// Advances the reader to the next token. Returns <see langword="false"/> when the current buffer does not
    /// contain enough data to complete the next token and the reader was not constructed with
    /// <c>isFinalBlock: true</c>; in that case the caller should supply more data (via <see cref="CurrentState"/>)
    /// and try again.
    /// </summary>
    public bool Read()
    {
        if (_phase == JavaPropertiesReaderPhase.EndOfFile)
        {
            return false;
        }

        int pos = _index;
        int line = _lineNumber;
        int col = _bytePositionInLine;

        if (_phase == JavaPropertiesReaderPhase.AfterKeyPendingValue)
        {
            return ReadValue(ref pos, ref line, ref col);
        }

        return ReadNextLine(ref pos, ref line, ref col);
    }

    /// <summary>
    /// Reads a full property (key and value) at once, skipping over comments. Returns <see langword="false"/>
    /// either at end of input or when more data is required (mirroring <see cref="Read"/>'s resumability contract).
    /// </summary>
    public bool ReadProperty()
    {
        while (true)
        {
            if (!Read())
            {
                return false;
            }

            if (_tokenType == JavaPropertiesTokenType.PropertyValue)
            {
                return true;
            }

            if (_tokenType == JavaPropertiesTokenType.EndOfFile)
            {
                return false;
            }

            // PropertyName or Comment: keep reading until we reach the value.
        }
    }

    /// <summary>Decodes the current token's value as a UTF-16 <see cref="string"/>.</summary>
    public readonly string GetString() => Utf8ValueParsing.GetString(_valueSpan);

    /// <summary>Decodes the current token's key as a UTF-16 <see cref="string"/>.</summary>
    public readonly string GetKeyString() => Utf8ValueParsing.GetString(KeySpan);

    /// <summary>Parses the current token's value as a <see cref="bool"/>.</summary>
    public readonly bool GetBoolean() => Utf8ValueParsing.GetBoolean(_valueSpan);

    /// <summary>Parses the current token's value as an <see cref="int"/>.</summary>
    public readonly int GetInt32() => Utf8ValueParsing.GetInt32(_valueSpan);

    /// <summary>Parses the current token's value as a <see cref="long"/>.</summary>
    public readonly long GetInt64() => Utf8ValueParsing.GetInt64(_valueSpan);

    /// <summary>Parses the current token's value as a <see cref="double"/>.</summary>
    public readonly double GetDouble() => Utf8ValueParsing.GetDouble(_valueSpan);

    private enum ScanStopReason
    {
        Whitespace,
        Separator,
        LineEnd,
        EndOfFile
    }

    private bool ReadNextLine(ref int pos, ref int line, ref int col)
    {
        ReadOnlySpan<byte> buf = _buffer;

        while (true)
        {
            if (pos >= buf.Length)
            {
                if (!_isFinalBlock)
                {
                    return false;
                }

                _index = pos;
                _lineNumber = line;
                _bytePositionInLine = col;
                _tokenType = JavaPropertiesTokenType.EndOfFile;
                _valueSpan = default;
                _phase = JavaPropertiesReaderPhase.EndOfFile;
                return true;
            }

            byte c = buf[pos];

            if (c is (byte)'\r' or (byte)'\n')
            {
                if (!TryConsumeLineTerminator(buf, ref pos, ref col, ref line))
                {
                    return false;
                }
                continue;
            }

            if (c is (byte)' ' or (byte)'\t' or (byte)'\f')
            {
                pos++;
                col++;
                continue;
            }

            if (c is (byte)'#' or (byte)'!')
            {
                return ReadComment(buf, ref pos, ref line, ref col);
            }

            return ReadKeyAndSeparator(buf, ref pos, ref line, ref col);
        }
    }

    private bool TryConsumeLineTerminator(ReadOnlySpan<byte> buf, ref int pos, ref int col, ref int line)
    {
        byte c = buf[pos];
        if (c == (byte)'\r')
        {
            int next = pos + 1;
            if (next >= buf.Length)
            {
                if (!_isFinalBlock)
                {
                    return false;
                }
                pos = next;
            }
            else if (buf[next] == (byte)'\n')
            {
                pos = next + 1;
            }
            else
            {
                pos = next;
            }
        }
        else
        {
            pos++;
        }

        line++;
        col = 0;
        return true;
    }

    private bool ReadComment(ReadOnlySpan<byte> buf, ref int pos, ref int line, ref int col)
    {
        int start = pos + 1;
        int scan = start;
        int scanCol = col + 1;

        while (true)
        {
            if (scan >= buf.Length)
            {
                if (!_isFinalBlock)
                {
                    return false;
                }
                break;
            }

            byte c = buf[scan];
            if (c is (byte)'\r' or (byte)'\n')
            {
                break;
            }

            scan++;
            scanCol++;
        }

        ReadOnlySpan<byte> commentText = buf.Slice(start, scan - start);
        pos = scan;
        col = scanCol;

        if (pos < buf.Length)
        {
            if (!TryConsumeLineTerminator(buf, ref pos, ref col, ref line))
            {
                return false;
            }
        }

        _index = pos;
        _lineNumber = line;
        _bytePositionInLine = col;
        _tokenType = JavaPropertiesTokenType.Comment;
        _valueSpan = commentText;
        _phase = JavaPropertiesReaderPhase.BeforeLine;
        return true;
    }

    private bool ReadKeyAndSeparator(ReadOnlySpan<byte> buf, ref int pos, ref int line, ref int col)
    {
        if (!ScanText(buf, ref pos, ref line, ref col, stopAtSeparators: true, out ScanStopReason reason))
        {
            return false;
        }

        SetKeyBuffer(_valueSpan);

        switch (reason)
        {
            case ScanStopReason.LineEnd:
            case ScanStopReason.EndOfFile:
                // No separator: value is empty. Leave pos at the terminator/EOF for the value phase to consume.
                break;

            case ScanStopReason.Separator:
                pos++;
                col++;
                if (!SkipSeparatorWhitespace(buf, ref pos, ref col))
                {
                    return false;
                }
                break;

            case ScanStopReason.Whitespace:
                if (!SkipSeparatorWhitespace(buf, ref pos, ref col))
                {
                    return false;
                }
                if (pos < buf.Length && (buf[pos] == (byte)'=' || buf[pos] == (byte)':'))
                {
                    pos++;
                    col++;
                    if (!SkipSeparatorWhitespace(buf, ref pos, ref col))
                    {
                        return false;
                    }
                }
                break;
        }

        _index = pos;
        _lineNumber = line;
        _bytePositionInLine = col;
        _tokenType = JavaPropertiesTokenType.PropertyName;
        _valueSpan = _keyBuffer.AsSpan(0, _keyBufferLength);
        _phase = JavaPropertiesReaderPhase.AfterKeyPendingValue;
        return true;
    }

    private bool SkipSeparatorWhitespace(ReadOnlySpan<byte> buf, ref int pos, ref int col)
    {
        while (true)
        {
            if (pos >= buf.Length)
            {
                if (!_isFinalBlock)
                {
                    return false;
                }
                return true;
            }

            byte c = buf[pos];
            if (c is (byte)' ' or (byte)'\t' or (byte)'\f')
            {
                pos++;
                col++;
                continue;
            }

            return true;
        }
    }

    private bool ReadValue(ref int pos, ref int line, ref int col)
    {
        ReadOnlySpan<byte> buf = _buffer;

        if (!ScanText(buf, ref pos, ref line, ref col, stopAtSeparators: false, out ScanStopReason reason))
        {
            return false;
        }

        if (reason == ScanStopReason.LineEnd)
        {
            if (!TryConsumeLineTerminator(buf, ref pos, ref col, ref line))
            {
                return false;
            }
        }

        _index = pos;
        _lineNumber = line;
        _bytePositionInLine = col;
        _tokenType = JavaPropertiesTokenType.PropertyValue;
        _phase = JavaPropertiesReaderPhase.BeforeLine;
        return true;
    }

    private bool ScanText(
        ReadOnlySpan<byte> buf,
        ref int pos,
        ref int line,
        ref int col,
        bool stopAtSeparators,
        out ScanStopReason stopReason)
    {
        int rawStart = pos;
        bool usedOutput = false;
        _scratchLength = 0;
        stopReason = ScanStopReason.EndOfFile;

        while (true)
        {
            if (pos >= buf.Length)
            {
                if (!_isFinalBlock)
                {
                    return false;
                }
                stopReason = ScanStopReason.EndOfFile;
                break;
            }

            byte c = buf[pos];

            if (c is (byte)'\r' or (byte)'\n')
            {
                stopReason = ScanStopReason.LineEnd;
                break;
            }

            if (c == (byte)'\\')
            {
                int next = pos + 1;
                if (next >= buf.Length)
                {
                    if (!_isFinalBlock)
                    {
                        return false;
                    }
                    throw Error("Dangling escape sequence at end of input.", pos, line, col);
                }

                byte n = buf[next];
                if (n is (byte)'\r' or (byte)'\n')
                {
                    if (!usedOutput)
                    {
                        AppendScratch(buf.Slice(rawStart, pos - rawStart));
                        usedOutput = true;
                    }

                    int p = pos + 1;
                    int c2 = col + 1;
                    int l2 = line;

                    if (buf[p] == (byte)'\r')
                    {
                        int afterCr = p + 1;
                        if (afterCr >= buf.Length)
                        {
                            if (!_isFinalBlock)
                            {
                                return false;
                            }
                            p = afterCr;
                        }
                        else if (buf[afterCr] == (byte)'\n')
                        {
                            p = afterCr + 1;
                        }
                        else
                        {
                            p = afterCr;
                        }
                    }
                    else
                    {
                        p++;
                    }

                    l2++;
                    c2 = 0;

                    while (true)
                    {
                        if (p >= buf.Length)
                        {
                            if (!_isFinalBlock)
                            {
                                return false;
                            }
                            break;
                        }

                        byte wc = buf[p];
                        if (wc is (byte)' ' or (byte)'\t' or (byte)'\f')
                        {
                            p++;
                            c2++;
                            continue;
                        }
                        break;
                    }

                    pos = p;
                    col = c2;
                    line = l2;
                    rawStart = pos;
                    continue;
                }
                else
                {
                    if (!usedOutput)
                    {
                        AppendScratch(buf.Slice(rawStart, pos - rawStart));
                        usedOutput = true;
                    }

                    if (!DecodeEscape(buf, ref pos, ref col, line))
                    {
                        return false;
                    }
                    rawStart = pos;
                    continue;
                }
            }

            if (stopAtSeparators && (c is (byte)' ' or (byte)'\t' or (byte)'\f' or (byte)'=' or (byte)':'))
            {
                stopReason = (c is (byte)'=' or (byte)':') ? ScanStopReason.Separator : ScanStopReason.Whitespace;
                break;
            }

            if (usedOutput)
            {
                AppendScratch(c);
            }
            pos++;
            col++;
        }

        _valueSpan = usedOutput ? _scratch.AsSpan(0, _scratchLength) : buf.Slice(rawStart, pos - rawStart);
        return true;
    }

    private bool DecodeEscape(ReadOnlySpan<byte> buf, ref int pos, ref int col, int line)
    {
        byte e = buf[pos + 1];
        switch (e)
        {
            case (byte)'t':
                AppendScratch((byte)'\t');
                pos += 2; col += 2;
                return true;
            case (byte)'n':
                AppendScratch((byte)'\n');
                pos += 2; col += 2;
                return true;
            case (byte)'r':
                AppendScratch((byte)'\r');
                pos += 2; col += 2;
                return true;
            case (byte)'f':
                AppendScratch((byte)'\f');
                pos += 2; col += 2;
                return true;
            case (byte)'u':
                return DecodeUnicodeEscape(buf, ref pos, ref col, line);
            default:
                if (e < 0x80)
                {
                    AppendScratch(e);
                    pos += 2; col += 2;
                    return true;
                }
                else
                {
                    int totalLen = Utf8SequenceTotalLength(e);
                    if (totalLen == 0)
                    {
                        throw Error("Invalid UTF-8 sequence following escape.", pos, line, col);
                    }
                    if (pos + 1 + totalLen > buf.Length)
                    {
                        if (!_isFinalBlock)
                        {
                            return false;
                        }
                        throw Error("Truncated UTF-8 sequence following escape.", pos, line, col);
                    }
                    AppendScratch(buf.Slice(pos + 1, totalLen));
                    pos += 1 + totalLen;
                    col += 1 + totalLen;
                    return true;
                }
        }
    }

    private static int Utf8SequenceTotalLength(byte leadByte)
    {
        if ((leadByte & 0xE0) == 0xC0) return 2;
        if ((leadByte & 0xF0) == 0xE0) return 3;
        if ((leadByte & 0xF8) == 0xF0) return 4;
        return 0;
    }

    private bool DecodeUnicodeEscape(ReadOnlySpan<byte> buf, ref int pos, ref int col, int line)
    {
        int hexStart = pos + 2;
        if (hexStart + 4 > buf.Length)
        {
            if (!_isFinalBlock)
            {
                return false;
            }
            throw Error("Invalid \\u Unicode escape: expected 4 hex digits.", pos, line, col);
        }

        int high = ParseHex4(buf.Slice(hexStart, 4), pos, line, col);
        int consumed = 6;

        if (high is >= 0xD800 and <= 0xDBFF)
        {
            int p2 = pos + consumed;
            if (p2 + 6 > buf.Length)
            {
                if (!_isFinalBlock)
                {
                    return false;
                }
                throw Error("Invalid surrogate pair: missing low surrogate.", pos, line, col);
            }

            if (buf[p2] != (byte)'\\' || buf[p2 + 1] != (byte)'u')
            {
                throw Error("Invalid surrogate pair: high surrogate not followed by a \\u escape.", pos, line, col);
            }

            int low = ParseHex4(buf.Slice(p2 + 2, 4), pos, line, col);
            if (low is < 0xDC00 or > 0xDFFF)
            {
                throw Error("Invalid surrogate pair: low surrogate out of range.", pos, line, col);
            }

            int codepoint = 0x10000 + ((high - 0xD800) << 10) + (low - 0xDC00);
            Span<byte> utf8 = stackalloc byte[4];
            int n = EncodeUtf8(codepoint, utf8);
            AppendScratch(utf8.Slice(0, n));
            pos += consumed + 6;
            col += consumed + 6;
            return true;
        }

        if (high is >= 0xDC00 and <= 0xDFFF)
        {
            throw Error("Invalid surrogate pair: unexpected low surrogate.", pos, line, col);
        }

        Span<byte> u8 = stackalloc byte[3];
        int m = EncodeUtf8(high, u8);
        AppendScratch(u8.Slice(0, m));
        pos += consumed;
        col += consumed;
        return true;
    }

    private int ParseHex4(ReadOnlySpan<byte> hex, int pos, int line, int col)
    {
        int value = 0;
        for (int i = 0; i < 4; i++)
        {
            int digit = HexDigit(hex[i]);
            if (digit < 0)
            {
                throw Error("Invalid \\u Unicode escape: expected 4 hex digits.", pos, line, col);
            }
            value = (value << 4) | digit;
        }
        return value;
    }

    private static int HexDigit(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10,
        _ => -1
    };

    private static int EncodeUtf8(int codepoint, Span<byte> dest)
    {
        if (codepoint <= 0x7F)
        {
            dest[0] = (byte)codepoint;
            return 1;
        }
        if (codepoint <= 0x7FF)
        {
            dest[0] = (byte)(0xC0 | (codepoint >> 6));
            dest[1] = (byte)(0x80 | (codepoint & 0x3F));
            return 2;
        }
        if (codepoint <= 0xFFFF)
        {
            dest[0] = (byte)(0xE0 | (codepoint >> 12));
            dest[1] = (byte)(0x80 | ((codepoint >> 6) & 0x3F));
            dest[2] = (byte)(0x80 | (codepoint & 0x3F));
            return 3;
        }
        dest[0] = (byte)(0xF0 | (codepoint >> 18));
        dest[1] = (byte)(0x80 | ((codepoint >> 12) & 0x3F));
        dest[2] = (byte)(0x80 | ((codepoint >> 6) & 0x3F));
        dest[3] = (byte)(0x80 | (codepoint & 0x3F));
        return 4;
    }

    private void AppendScratch(byte b)
    {
        EnsureScratchCapacity(1);
        _scratch![_scratchLength++] = b;
    }

    private void AppendScratch(scoped ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }
        EnsureScratchCapacity(bytes.Length);
        bytes.CopyTo(_scratch.AsSpan(_scratchLength));
        _scratchLength += bytes.Length;
    }

    private void EnsureScratchCapacity(int additional)
    {
        int required = _scratchLength + additional;
        if (_scratch is null)
        {
            _scratch = new byte[Math.Max(64, required)];
        }
        else if (_scratch.Length < required)
        {
            int newSize = Math.Max(_scratch.Length * 2, required);
            byte[] newBuffer = new byte[newSize];
            _scratch.AsSpan(0, _scratchLength).CopyTo(newBuffer);
            _scratch = newBuffer;
        }
    }

    private void SetKeyBuffer(scoped ReadOnlySpan<byte> src)
    {
        if (_keyBuffer is null || _keyBuffer.Length < src.Length)
        {
            _keyBuffer = new byte[Math.Max(32, src.Length)];
        }
        src.CopyTo(_keyBuffer);
        _keyBufferLength = src.Length;
    }

    private readonly JavaPropertiesException Error(string message, int pos, int line, int col) => new(message, _consumedBaseline + pos, line, col);
}
