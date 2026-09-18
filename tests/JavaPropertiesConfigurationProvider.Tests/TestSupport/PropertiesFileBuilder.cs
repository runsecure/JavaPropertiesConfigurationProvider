using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.JavaProperties;
using Microsoft.Extensions.FileProviders;
using ProviderType = Microsoft.Extensions.Configuration.JavaProperties.JavaPropertiesConfigurationProvider;

namespace JavaPropertiesConfigurationProvider.Tests.TestSupport;

/// <summary>
/// Fluently accumulates key/value pairs, writes them out as a Java <c>.properties</c> file via
/// <see cref="JavaPropertiesWriter"/>, and loads the result back through the configuration provider
/// (or a fully built <see cref="IConfigurationRoot"/>) for use in tests.
/// </summary>
internal sealed class PropertiesFileBuilder : IDisposable
{
    private readonly Dictionary<string, string> _properties = new(StringComparer.Ordinal);
    private string? _writtenPath;

    public static PropertiesFileBuilder Create() => new();

    public static PropertiesFileBuilder FromDictionary(IEnumerable<KeyValuePair<string, string>> values)
        => new PropertiesFileBuilder().AddRange(values);

    /// <summary>The properties accumulated so far, in insertion order.</summary>
    public IReadOnlyDictionary<string, string> Properties => _properties;

    public PropertiesFileBuilder Add(string key, string value)
    {
        _properties[key] = value;
        return this;
    }

    public PropertiesFileBuilder AddRange(IEnumerable<KeyValuePair<string, string>> values)
    {
        foreach (KeyValuePair<string, string> kvp in values)
        {
            _properties[kvp.Key] = kvp.Value;
        }
        return this;
    }

    /// <summary>Writes the accumulated properties to disk using <see cref="JavaPropertiesWriter"/>.</summary>
    /// <param name="path">The file to write to; a new temp file is created when omitted.</param>
    /// <returns>The path written to.</returns>
    public string WriteToFile(string? path = null)
    {
        path ??= Path.Combine(Path.GetTempPath(), $"jpcp-{Guid.NewGuid():N}.properties");

        using (FileStream stream = File.Create(path))
        {
            var writer = new JavaPropertiesWriter(stream);
            foreach (KeyValuePair<string, string> kvp in _properties)
            {
                writer.WriteProperty(kvp.Key, kvp.Value);
            }
            writer.Dispose();
        }

        _writtenPath = path;
        return path;
    }

    /// <summary>
    /// Writes the accumulated properties (if not already written) and loads them into a standalone
    /// <see cref="ProviderType"/>, with <see cref="FileConfigurationSource.FileProvider"/> already
    /// pointed at the written file's directory.
    /// </summary>
    public ProviderType BuildProvider(Action<JavaPropertiesConfigurationSource>? configureSource = null)
    {
        string path = _writtenPath ?? WriteToFile();

        var source = new JavaPropertiesConfigurationSource
        {
            FileProvider = new PhysicalFileProvider(Path.GetDirectoryName(path)!),
            Path = Path.GetFileName(path),
        };
        configureSource?.Invoke(source);

        var provider = new ProviderType(source);
        provider.Load();
        return provider;
    }

    /// <summary>
    /// Writes the accumulated properties (if not already written) and builds a complete
    /// <see cref="IConfigurationRoot"/> around them.
    /// </summary>
    public IConfigurationRoot BuildConfigurationRoot(Action<JavaPropertiesConfigurationSource>? configureSource = null)
    {
        string path = _writtenPath ?? WriteToFile();

        return new ConfigurationBuilder()
            .AddJavaProperties(source =>
            {
                source.FileProvider = new PhysicalFileProvider(Path.GetDirectoryName(path)!);
                source.Path = Path.GetFileName(path);
                configureSource?.Invoke(source);
            })
            .Build();
    }

    /// <summary>Deletes the file written by <see cref="WriteToFile"/>, if any.</summary>
    public void Dispose()
    {
        if (_writtenPath is not null && File.Exists(_writtenPath))
        {
            File.Delete(_writtenPath);
        }
    }
}
