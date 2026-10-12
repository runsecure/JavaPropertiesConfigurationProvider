using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration.JavaProperties;

BenchmarkRunner.Run<ReaderBenchmarks>();

[MemoryDiagnoser]
public class ReaderBenchmarks
{
    private byte[] _propertiesData = null!;
    private byte[] _jsonData = null!;

    [Params(1_000, 50_000)]
    public int PropertyCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var props = new StringBuilder();
        var json = new StringBuilder();
        json.Append('{');
        for (int i = 0; i < PropertyCount; i++)
        {
            props.Append("key.").Append(i).Append("=value-").Append(i).Append('\n');
            if (i > 0)
            {
                json.Append(',');
            }
            json.Append("\"key.").Append(i).Append("\":\"value-").Append(i).Append('"');
        }
        json.Append('}');

        _propertiesData = Encoding.UTF8.GetBytes(props.ToString());
        _jsonData = Encoding.UTF8.GetBytes(json.ToString());
    }

    [Benchmark(Baseline = true)]
    public int JavaPropertiesReader_ReadAllProperties()
    {
        var reader = new JavaPropertiesReader(_propertiesData, isFinalBlock: true);
        int count = 0;
        while (reader.ReadProperty())
        {
            count += reader.KeySpan.Length + reader.ValueSpan.Length;
        }
        return count;
    }

    [Benchmark]
    public int Utf8JsonReader_ReadAllProperties()
    {
        var reader = new Utf8JsonReader(_jsonData);
        int count = 0;
        while (reader.Read())
        {
            if (reader.TokenType is JsonTokenType.PropertyName or JsonTokenType.String)
            {
                count += reader.HasValueSequence ? (int)reader.ValueSequence.Length : reader.ValueSpan.Length;
            }
        }
        return count;
    }
}
