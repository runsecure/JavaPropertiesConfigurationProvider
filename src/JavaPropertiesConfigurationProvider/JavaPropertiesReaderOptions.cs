namespace Microsoft.Extensions.Configuration.JavaProperties;

/// <summary>
/// Options that control the limits enforced by <see cref="JavaPropertiesReader"/> and the stream, pipe and
/// configuration helpers built on it.
/// </summary>
/// <remarks>
/// The defaults are deliberately conservative so that parsing untrusted or corrupted input cannot exhaust
/// memory. Raise <see cref="MaxTokenSize"/> only if your files legitimately contain larger values.
/// </remarks>
public struct JavaPropertiesReaderOptions
{
    /// <summary>The default value of <see cref="MaxTokenSize"/>: 1 MiB.</summary>
    public const int DefaultMaxTokenSize = 1024 * 1024;

    private int _maxTokenSize;

    /// <summary>
    /// The maximum size, in bytes of raw input, of a single key, value or comment. The size is measured as the
    /// text appears in the file, so it includes escape sequences and line continuations but not the separator
    /// or the final line terminator. The same limit applies separately to the whitespace around a key/value
    /// separator. Input that exceeds the limit causes a <see cref="JavaPropertiesException"/>.
    /// Defaults to <see cref="DefaultMaxTokenSize"/> (1 MiB).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int MaxTokenSize
    {
        readonly get => _maxTokenSize == 0 ? DefaultMaxTokenSize : _maxTokenSize;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxTokenSize = value;
        }
    }
}
