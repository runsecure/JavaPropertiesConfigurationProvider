namespace Microsoft.Extensions.Configuration.JavaProperties;

internal enum JavaPropertiesReaderPhase : byte
{
    BeforeLine = 0,
    AfterKeyPendingValue = 1,
    EndOfFile = 2
}

/// <summary>
/// Holds the state required to resume a <see cref="JavaPropertiesReader"/> across multiple
/// buffer segments (for example when reading from a <see cref="System.IO.Stream"/> or
/// <see cref="System.IO.Pipelines.PipeReader"/> in chunks).
/// </summary>
public readonly struct JavaPropertiesReaderState
{
    internal readonly long BytesConsumedCore;
    internal readonly int LineNumberCore;
    internal readonly int BytePositionInLineCore;
    internal readonly JavaPropertiesReaderPhase Phase;
    internal readonly byte[]? PendingKey;
    internal readonly int PendingKeyLength;

    /// <summary>
    /// Initializes a new, default reader state representing the beginning of a document.
    /// </summary>
    public JavaPropertiesReaderState()
    {
        BytesConsumedCore = 0;
        LineNumberCore = 0;
        BytePositionInLineCore = 0;
        Phase = JavaPropertiesReaderPhase.BeforeLine;
        PendingKey = null;
        PendingKeyLength = 0;
    }

    internal JavaPropertiesReaderState(
        long bytesConsumed,
        int lineNumber,
        int bytePositionInLine,
        JavaPropertiesReaderPhase phase,
        byte[]? pendingKey,
        int pendingKeyLength)
    {
        BytesConsumedCore = bytesConsumed;
        LineNumberCore = lineNumber;
        BytePositionInLineCore = bytePositionInLine;
        Phase = phase;
        PendingKey = pendingKey;
        PendingKeyLength = pendingKeyLength;
    }

    /// <summary>The total number of bytes consumed so far.</summary>
    public long BytesConsumed => BytesConsumedCore;

    /// <summary>The zero-based line number the reader had reached.</summary>
    public int LineNumber => LineNumberCore;

    /// <summary>The zero-based byte offset within <see cref="LineNumber"/> the reader had reached.</summary>
    public int BytePositionInLine => BytePositionInLineCore;
}
