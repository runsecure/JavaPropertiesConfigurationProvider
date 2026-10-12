using Microsoft.Extensions.Configuration;
using JavaPropertiesConfigurationProvider.Tests.TestSupport;
using Xunit;

namespace JavaPropertiesConfigurationProvider.Tests;

public class PropertiesFileBuilderTests
{
    [Fact]
    public void Add_IsFluentAndLastWriteWins()
    {
        using var builder = PropertiesFileBuilder.Create()
            .Add("server.port", "8080")
            .Add("server.name", "demo")
            .Add("server.port", "9090");

        Assert.Equal(2, builder.Properties.Count);
        Assert.Equal("9090", builder.Properties["server.port"]);
    }

    [Fact]
    public void WriteToFile_ProducesReadableProperties()
    {
        using var builder = PropertiesFileBuilder.Create()
            .Add("a.b", "1")
            .Add("greeting", "hello world");

        string path = builder.WriteToFile();

        Assert.True(File.Exists(path));
        string content = File.ReadAllText(path);
        Assert.Contains("a.b=1", content);
        Assert.Contains("greeting=hello\\ world", content);
    }

    [Fact]
    public void BuildProvider_LoadsWrittenProperties()
    {
        using var builder = PropertiesFileBuilder.Create()
            .Add("server.port", "8080")
            .Add("database.timeout", "30");

        using var provider = builder.BuildProvider();

        // Periods are replaced with the configuration key delimiter by default.
        Assert.True(provider.TryGet("server:port", out string? port));
        Assert.Equal("8080", port);
        Assert.True(provider.TryGet("database:timeout", out string? timeout));
        Assert.Equal("30", timeout);
    }

    [Fact]
    public void BuildConfigurationRoot_MapsNestedKeysByDefault()
    {
        using var builder = PropertiesFileBuilder.Create()
            .Add("server.port", "8080")
            .Add("database.connection.timeout", "30");

        IConfigurationRoot config = builder.BuildConfigurationRoot();

        Assert.Equal("8080", config["server:port"]);
        Assert.Equal("30", config["database:connection:timeout"]);
    }

    [Fact]
    public void BuildConfigurationRoot_ConfigureSourceCanDisablePeriodMapping()
    {
        using var builder = PropertiesFileBuilder.Create()
            .Add("server.port", "8080");

        IConfigurationRoot config = builder.BuildConfigurationRoot(source =>
        {
            source.ReplacePeriodsWithKeyDelimiter = false;
        });

        Assert.Equal("8080", config["server.port"]);
        Assert.Null(config["server:port"]);
    }

    [Fact]
    public void FromDictionary_SeedsProperties()
    {
        var seed = new Dictionary<string, string>
        {
            ["x"] = "1",
            ["y"] = "2",
        };

        using var builder = PropertiesFileBuilder.FromDictionary(seed).Add("z", "3");

        IConfigurationRoot config = builder.BuildConfigurationRoot();
        Assert.Equal("1", config["x"]);
        Assert.Equal("2", config["y"]);
        Assert.Equal("3", config["z"]);
    }

    [Fact]
    public void Dispose_DeletesWrittenFile()
    {
        string path;
        using (var builder = PropertiesFileBuilder.Create().Add("a", "1"))
        {
            path = builder.WriteToFile();
            Assert.True(File.Exists(path));
        }

        Assert.False(File.Exists(path));
    }
}
