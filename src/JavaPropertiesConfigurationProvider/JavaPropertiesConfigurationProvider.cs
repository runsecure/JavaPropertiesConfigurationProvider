using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.Configuration.JavaProperties;

/// <summary>
/// An <see cref="Microsoft.Extensions.Configuration.FileConfigurationProvider"/> that loads configuration
/// values from a Java <c>.properties</c> file.
/// </summary>
public sealed class JavaPropertiesConfigurationProvider : FileConfigurationProvider
{
    /// <summary>Initializes a new provider for the given <see cref="JavaPropertiesConfigurationSource"/>.</summary>
    public JavaPropertiesConfigurationProvider(JavaPropertiesConfigurationSource source) : base(source)
    {
    }

    private JavaPropertiesConfigurationSource TypedSource => (JavaPropertiesConfigurationSource)Source;

    /// <inheritdoc />
    public override void Load(Stream stream)
    {
        bool replacePeriods = TypedSource.ReplacePeriodsWithKeyDelimiter;
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        JavaPropertiesStreamProcessor.ProcessProperties(stream, (keyBytes, valueBytes) =>
        {
            string key = Utf8ValueParsing.GetString(keyBytes);
            string value = Utf8ValueParsing.GetString(valueBytes);

            if (replacePeriods)
            {
                key = key.Replace(".", ConfigurationPath.KeyDelimiter);
            }

            // Duplicate keys: last value wins.
            data[key] = value;
        }, options: TypedSource.ReaderOptions);

        Data = data;
    }
}
