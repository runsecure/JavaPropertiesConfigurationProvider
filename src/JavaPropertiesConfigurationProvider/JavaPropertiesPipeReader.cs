using System.Buffers;
using System.IO.Pipelines;

namespace Microsoft.Extensions.Configuration.JavaProperties;

/// <summary>
/// Drives a <see cref="JavaPropertiesReader"/> over a <see cref="PipeReader"/>, correctly reporting
/// consumed/examined positions back to the pipe so unconsumed data is preserved across reads.
/// </summary>
public static class JavaPropertiesPipeReader
{
    /// <summary>Invoked once per property as it becomes available.</summary>
    public delegate void PropertyCallback(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value);

    /// <summary>Reads and processes every property from <paramref name="pipeReader"/> until the pipe completes.</summary>
    public static async Task ProcessPropertiesAsync(
        PipeReader pipeReader,
        PropertyCallback callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pipeReader);
        ArgumentNullException.ThrowIfNull(callback);

        var state = new JavaPropertiesReaderState();

        while (true)
        {
            ReadResult result = await pipeReader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;
            long baselineBeforeAttempt = state.BytesConsumedCore;
            bool isFinalBlock = result.IsCompleted;

            var reader = new JavaPropertiesReader(buffer, isFinalBlock, state);
            try
            {
                while (reader.ReadProperty())
                {
                    callback(reader.KeySpan, reader.ValueSpan);
                }

                bool reachedEnd = reader.TokenType == JavaPropertiesTokenType.EndOfFile;
                state = reader.CurrentState;

                long bufferRelativeConsumed = state.BytesConsumed - baselineBeforeAttempt;
                SequencePosition consumed = buffer.GetPosition(bufferRelativeConsumed);
                pipeReader.AdvanceTo(consumed, buffer.End);

                if (reachedEnd)
                {
                    return;
                }
            }
            finally
            {
                reader.Dispose();
            }

            if (isFinalBlock)
            {
                throw new JavaPropertiesException(
                    "Unexpected end of input while parsing a property.",
                    state.BytesConsumed,
                    state.LineNumber,
                    state.BytePositionInLine);
            }
        }
    }
}
