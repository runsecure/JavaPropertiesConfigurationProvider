using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.Configuration.JavaProperties;

/// <summary>
/// Extension methods for registering Java <c>.properties</c> files as an <see cref="IConfigurationBuilder"/> source.
/// </summary>
public static class JavaPropertiesConfigurationExtensions
{
    /// <summary>Adds a Java <c>.properties</c> file to the configuration builder.</summary>
    public static IConfigurationBuilder AddJavaProperties(this IConfigurationBuilder builder, string path)
        => AddJavaProperties(builder, source =>
        {
            source.Path = path;
        });

    /// <summary>Adds a Java <c>.properties</c> file to the configuration builder using the specified configuration.</summary>
    public static IConfigurationBuilder AddJavaProperties(
        this IConfigurationBuilder builder,
        Action<JavaPropertiesConfigurationSource> configureSource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureSource);

        var source = new JavaPropertiesConfigurationSource();
        configureSource(source);
        return builder.Add(source);
    }
}
