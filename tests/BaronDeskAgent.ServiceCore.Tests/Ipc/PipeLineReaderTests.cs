using System.Text;
using BaronDesk.Shared.Ipc;

namespace BaronDeskAgent.ServiceCore.Tests.Ipc;

public sealed class PipeLineReaderTests
{
    [Fact]
    public async Task Reads_lines_until_end_of_stream()
    {
        using var reader = Reader("first\r\nsecond\nlast");

        Assert.Equal("first", await PipeLineReader.ReadLineAsync(reader, 100, CancellationToken.None));
        Assert.Equal("second", await PipeLineReader.ReadLineAsync(reader, 100, CancellationToken.None));
        Assert.Equal("last", await PipeLineReader.ReadLineAsync(reader, 100, CancellationToken.None));
        Assert.Null(await PipeLineReader.ReadLineAsync(reader, 100, CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_lines_longer_than_the_limit()
    {
        using var reader = Reader(new string('x', 101) + "\n");

        await Assert.ThrowsAsync<InvalidDataException>(() => PipeLineReader.ReadLineAsync(reader, 100, CancellationToken.None));
    }

    private static StreamReader Reader(string content) => new(new MemoryStream(Encoding.UTF8.GetBytes(content)));
}
