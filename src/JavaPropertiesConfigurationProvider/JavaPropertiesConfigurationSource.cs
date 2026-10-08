using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.Configuration.JavaProperties;

/// <summary>
/// Represents a Java <c>.properties</c> file as an <see cref="IConfigurationSource"/>.
/// </summary>
public sealed class JavaPropertiesConfigurationSource : FileConfigurationSource
{
    /// <summary>
    /// When <see langword="true"/> (the default), periods (<c>.</c>) in property keys are replaced with
    /// <see cref="ConfigurationPath.KeyDelimiter"/> so that <c>server.port</c> maps to the configuration
    /// path <c>server:port</c>.
    /// </summary>
    public bool ReplacePeriodsWithKeyDelimiter { get; set; } = true;

    /// <summary>
    /// The maximum size, in bytes, of a single key, value or comment in the file. Files containing a larger
    /// entry fail to load with a <see cref="JavaPropertiesException"/>. Defaults to
    /// <see cref="JavaPropertiesReaderOptions.DefaultMaxTokenSize"/> (1 MiB), which protects against
    /// memory exhaustion from corrupted or malicious files. Raise it only if your files need larger values.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int MaxTokenSize
    {
        get => _readerOptions.MaxTokenSize;
        set => _readerOptions.MaxTokenSize = value;
    }

    private JavaPropertiesReaderOptions _readerOptions;

    internal JavaPropertiesReaderOptions ReaderOptions => _readerOptions;

    /// <inheritdoc />
    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new JavaPropertiesConfigurationProvider(this);
    }
}
