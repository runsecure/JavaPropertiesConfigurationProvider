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

    /// <inheritdoc />
    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new JavaPropertiesConfigurationProvider(this);
    }
}
