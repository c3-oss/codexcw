using System.Text;

namespace C3OSS.Codexcw.Tests;

public sealed class TailBufferTests
{
    [Fact]
    public void KeepsOnlyTheTail()
    {
        var buffer = new TailBuffer(4);
        buffer.Write(Encoding.UTF8.GetBytes("0123456789"));
        Assert.Equal("6789", buffer.ToString());
    }

    [Fact]
    public void KeepsTailAcrossManyWrites()
    {
        var buffer = new TailBuffer(8);
        for (var i = 0; i < 100; i++)
        {
            buffer.Write(Encoding.UTF8.GetBytes($"chunk-{i:00} "));
        }
        Assert.Equal("hunk-99 ", buffer.ToString());
    }

    [Fact]
    public void ZeroLimitDiscardsEverything()
    {
        var buffer = new TailBuffer(0);
        buffer.Write(Encoding.UTF8.GetBytes("data"));
        Assert.Equal("", buffer.ToString());
    }
}

public sealed class JsonlReaderTests
{
    private static async Task<List<string>> ReadAll(string input, int maxBytes = 1024)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        var lines = new List<string>();
        await foreach (var line in JsonlReader.ReadLinesAsync(stream, maxBytes))
        {
            lines.Add(line);
        }
        return lines;
    }

    [Fact]
    public async Task SplitsLinesAndStripsCarriageReturns()
    {
        var lines = await ReadAll("one\r\ntwo\nthree");
        Assert.Equal(["one", "two", "three"], lines);
    }

    [Fact]
    public async Task EmitsFinalUnterminatedLine()
    {
        Assert.Equal(["only"], await ReadAll("only"));
        Assert.Empty(await ReadAll(""));
    }

    [Fact]
    public async Task LineOverLimitThrows()
    {
        await Assert.ThrowsAsync<LineTooLongException>(() => ReadAll(new string('x', 32) + "\nok\n", maxBytes: 16));
    }

    [Fact]
    public async Task LineAtLimitIsAccepted()
    {
        var lines = await ReadAll(new string('x', 16) + "\n", maxBytes: 16);
        Assert.Equal(16, Assert.Single(lines).Length);
    }
}
