using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Configuration.JavaProperties;
using Xunit;

namespace JavaPropertiesConfigurationProvider.Tests;

public class PerformanceTests
{
    [Fact]
    public void LargeFile_OverOneHundredMegabytes_ParsesCorrectly()
    {
        string path = Path.Combine(Path.GetTempPath(), $"jpcp-large-{Guid.NewGuid():N}.properties");
        try
        {
            const int targetBytes = 110 * 1024 * 1024;
            long written = 0;
            int count = 0;

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var writer = new StreamWriter(fs, new UTF8Encoding(false), 1 << 20))
            {
                while (written < targetBytes)
                {
                    string line = $"key.{count}.setting=value-{count}-{new string('x', 20)}\n";
                    writer.Write(line);
                    written += line.Length;
                    count++;
                }
            }

            var sw = Stopwatch.StartNew();
            using var stream = File.OpenRead(path);
            int seen = 0;
            long totalKeyBytes = 0;
            JavaPropertiesStreamProcessor.ProcessProperties(stream, (key, value) =>
            {
                seen++;
                totalKeyBytes += key.Length;
            });
            sw.Stop();

            Assert.Equal(count, seen);
            Assert.True(totalKeyBytes > 0);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"Parsing took too long: {sw.Elapsed}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LongPropertyValue_ParsesCorrectly()
    {
        string longValue = new string('a', 5_000_000);
        byte[] data = Encoding.UTF8.GetBytes($"key={longValue}\n");
        var reader = new JavaPropertiesReader(data, isFinalBlock: true);
        Assert.True(reader.ReadProperty());
        Assert.Equal(longValue, reader.GetString());
    }

    [Fact]
    public void ManyProperties_ReaderParsesCorrectly()
    {
        var sb = new StringBuilder();
        const int count = 50_000;
        for (int i = 0; i < count; i++)
        {
            sb.Append("k").Append(i).Append('=').Append(i).Append('\n');
        }

        byte[] data = Encoding.UTF8.GetBytes(sb.ToString());
        var reader = new JavaPropertiesReader(data, isFinalBlock: true);
        var last = new Dictionary<string, int>();
        int seen = 0;
        while (reader.ReadProperty())
        {
            seen++;
            last[reader.GetKeyString()] = reader.GetInt32();
        }

        Assert.Equal(count, seen);
        Assert.Equal(count - 1, last[$"k{count - 1}"]);
    }
}
