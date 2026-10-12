using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Configuration.JavaProperties;
using Xunit;

namespace JavaPropertiesConfigurationProvider.Tests;

public class PipeReaderTests
{
    [Fact]
    public async Task ProcessPropertiesAsync_SingleWrite_ReadsAllProperties()
    {
        var pipe = new Pipe();
        byte[] data = Encoding.UTF8.GetBytes("a=1\nb=2\nc=3\n");

        Task writerTask = Task.Run(async () =>
        {
            await pipe.Writer.WriteAsync(data);
            await pipe.Writer.CompleteAsync();
        });

        var results = new Dictionary<string, string>();
        await JavaPropertiesPipeReader.ProcessPropertiesAsync(pipe.Reader, (k, v) =>
        {
            results[Encoding.UTF8.GetString(k)] = Encoding.UTF8.GetString(v);
        });

        await writerTask;
        Assert.Equal("1", results["a"]);
        Assert.Equal("2", results["b"]);
        Assert.Equal("3", results["c"]);
    }

    [Fact]
    public async Task ProcessPropertiesAsync_ChunkedWrites_SplitAcrossTokens()
    {
        var pipe = new Pipe();
        string content = "server.port=8080\nmessage=hello\\\n world\nkey2=\\u0041\\uD83D\\uDE00\n";
        byte[] data = Encoding.UTF8.GetBytes(content);

        Task writerTask = Task.Run(async () =>
        {
            // Write one byte at a time to force many partial-buffer resumptions.
            foreach (byte b in data)
            {
                await pipe.Writer.WriteAsync(new[] { b });
            }
            await pipe.Writer.CompleteAsync();
        });

        var results = new Dictionary<string, string>();
        await JavaPropertiesPipeReader.ProcessPropertiesAsync(pipe.Reader, (k, v) =>
        {
            results[Encoding.UTF8.GetString(k)] = Encoding.UTF8.GetString(v);
        });

        await writerTask;
        Assert.Equal("8080", results["server.port"]);
        Assert.Equal("helloworld", results["message"]);
        Assert.Equal("A\U0001F600", results["key2"]);
    }

    [Fact]
    public async Task ProcessPropertiesAsync_EmptyInput_CompletesWithoutCallbacks()
    {
        var pipe = new Pipe();
        await pipe.Writer.CompleteAsync();

        int calls = 0;
        await JavaPropertiesPipeReader.ProcessPropertiesAsync(pipe.Reader, (_, _) => calls++);

        Assert.Equal(0, calls);
    }
}
