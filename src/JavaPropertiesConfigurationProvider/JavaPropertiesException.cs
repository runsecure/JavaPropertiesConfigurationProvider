namespace Microsoft.Extensions.Configuration.JavaProperties;

/// <summary>
/// Represents an error that occurs while reading or writing Java .properties content.
/// </summary>
public sealed class JavaPropertiesException : Exception
{
    /// <summary>
    /// The total number of bytes consumed from the source before the error was detected.
    /// </summary>
    public long BytesConsumed { get; }

    /// <summary>
    /// The zero-based line number at which the error occurred.
    /// </summary>
    public int LineNumber { get; }

    /// <summary>
    /// The zero-based byte position within <see cref="LineNumber"/> at which the error occurred.
    /// </summary>
    public int BytePositionInLine { get; }

    public JavaPropertiesException(string message)
        : base(message)
    {
    }

    public JavaPropertiesException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public JavaPropertiesException(string message, long bytesConsumed, int lineNumber, int bytePositionInLine)
        : base(FormatMessage(message, lineNumber, bytePositionInLine))
    {
        BytesConsumed = bytesConsumed;
        LineNumber = lineNumber;
        BytePositionInLine = bytePositionInLine;
    }

    public JavaPropertiesException(string message, long bytesConsumed, int lineNumber, int bytePositionInLine, Exception? innerException)
        : base(FormatMessage(message, lineNumber, bytePositionInLine), innerException)
    {
        BytesConsumed = bytesConsumed;
        LineNumber = lineNumber;
        BytePositionInLine = bytePositionInLine;
    }

    private static string FormatMessage(string message, int lineNumber, int bytePositionInLine)
        => $"{message} LineNumber: {lineNumber} | BytePositionInLine: {bytePositionInLine}.";
}
