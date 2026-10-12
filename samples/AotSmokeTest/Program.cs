using System.Buffers;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.JavaProperties;
using Microsoft.Extensions.FileProviders;

const string content = """
    # sample properties
    server.port=8080
    server.name=demo\ server
    database.connection.timeout=30
    greeting=ABC
    """;

byte[] utf8 = Encoding.UTF8.GetBytes(content);

// 1. Low-level reader
var reader = new JavaPropertiesReader(utf8, isFinalBlock: true);
int tokenCount = 0;
while (reader.Read())
{
    tokenCount++;
    if (reader.TokenType == JavaPropertiesTokenType.EndOfFile)
    {
        break;
    }
}

// 2. ReadProperty
var reader2 = new JavaPropertiesReader(utf8, isFinalBlock: true);
int propertyCount = 0;
while (reader2.ReadProperty())
{
    propertyCount++;
}

// 3. Writer
var buffer = new ArrayBufferWriter<byte>();
var writer = new JavaPropertiesWriter(buffer);
writer.WriteComment("generated");
writer.WriteProperty("a.b.c", "hello world");
writer.Dispose();

// 4. Configuration provider
string tempFile = Path.Combine(Path.GetTempPath(), $"aotsmoke-{Guid.NewGuid():N}.properties");
File.WriteAllBytes(tempFile, utf8);
try
{
    IConfigurationRoot config = new ConfigurationBuilder()
        .AddJavaProperties(source =>
        {
            source.FileProvider = new PhysicalFileProvider(Path.GetTempPath());
            source.Path = Path.GetFileName(tempFile);
        })
        .Build();

    string? mappedPort = config["server:port"];

    Console.WriteLine($"tokens={tokenCount} properties={propertyCount} writerBytes={buffer.WrittenCount} configPort={mappedPort}");
}
finally
{
    File.Delete(tempFile);
}
