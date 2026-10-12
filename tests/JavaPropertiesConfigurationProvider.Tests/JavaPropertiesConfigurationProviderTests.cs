using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.JavaProperties;
using Microsoft.Extensions.FileProviders;
using JavaPropertiesConfigurationProvider.Tests.TestSupport;
using Xunit;

namespace JavaPropertiesConfigurationProvider.Tests;

public class JavaPropertiesConfigurationProviderTests
{
    [Fact]
    public void BasicLoad_MapsNestedKeys()
    {
        using var builder = PropertiesFileBuilder.Create()
            .Add("server.port", "8080")
            .Add("database.connection.timeout", "30");

        IConfigurationRoot config = builder.BuildConfigurationRoot();

        Assert.Equal("8080", config["server:port"]);
        Assert.Equal("30", config["database:connection:timeout"]);
    }

    [Fact]
    public void ReplacePeriodsWithKeyDelimiter_CanBeDisabled()
    {
        using var builder = PropertiesFileBuilder.Create()
            .Add("server.port", "8080");

        IConfigurationRoot config = builder.BuildConfigurationRoot(source => source.ReplacePeriodsWithKeyDelimiter = false);

        Assert.Equal("8080", config["server.port"]);
        Assert.Null(config["server:port"]);
    }

    [Fact]
    public void DuplicateKeys_LastWins()
    {
        // A Dictionary-backed builder can't represent literal duplicate lines, so this test
        // writes the raw file content directly to exercise the parser/provider's real behavior.
        string path = Path.Combine(Path.GetTempPath(), $"jpcp-{Guid.NewGuid():N}.properties");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("port=8080\nport=9090\n"));
        try
        {
            IConfigurationRoot config = new ConfigurationBuilder()
                .AddJavaProperties(source =>
                {
                    source.FileProvider = new PhysicalFileProvider(Path.GetDirectoryName(path)!);
                    source.Path = Path.GetFileName(path);
                })
                .Build();

            Assert.Equal("9090", config["port"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Reload_PicksUpFileChanges()
    {
        using var builder = PropertiesFileBuilder.Create().Add("a", "1");
        string path = builder.WriteToFile();

        using var provider = builder.BuildProvider(source => source.ReloadOnChange = false);
        Assert.True(provider.TryGet("a", out string? value));
        Assert.Equal("1", value);

        builder.Add("a", "2").WriteToFile(path);
        provider.Load();
        Assert.True(provider.TryGet("a", out value));
        Assert.Equal("2", value);
    }

    [Fact]
    public void MissingOptionalFile_LoadsEmptyConfiguration()
    {
        string path = Path.Combine(Path.GetTempPath(), $"jpcp-missing-{Guid.NewGuid():N}.properties");

        IConfigurationRoot config = new ConfigurationBuilder()
            .AddJavaProperties(source =>
            {
                source.Path = path;
                source.Optional = true;
            })
            .Build();

        Assert.Null(config["anything"]);
    }

    [Fact]
    public void LargePropertyCount_LoadsCorrectly()
    {
        const int count = 5000;
        using var builder = PropertiesFileBuilder.Create();
        for (int i = 0; i < count; i++)
        {
            builder.Add($"key{i}", $"value{i}");
        }

        IConfigurationRoot config = builder.BuildConfigurationRoot();

        Assert.Equal("value0", config["key0"]);
        Assert.Equal($"value{count - 1}", config[$"key{count - 1}"]);
    }

    [Fact]
    public void ProcessProperties_HandlesInputLargerThanInitialBuffer()
    {
        var sb = new StringBuilder();
        sb.Append("first=1\n");
        sb.Append("bigvalue=").Append('x', 50_000).Append('\n');
        sb.Append("last=done\n");

        byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
        using var stream = new MemoryStream(bytes);

        var results = new Dictionary<string, string>();
        JavaPropertiesStreamProcessor.ProcessProperties(stream, (k, v) =>
        {
            results[Encoding.UTF8.GetString(k)] = Encoding.UTF8.GetString(v);
        }, initialBufferSize: 64);

        Assert.Equal("1", results["first"]);
        Assert.Equal(new string('x', 50_000), results["bigvalue"]);
        Assert.Equal("done", results["last"]);
    }
}
