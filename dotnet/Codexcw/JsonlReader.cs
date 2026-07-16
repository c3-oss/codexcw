using System.Runtime.CompilerServices;
using System.Text;

namespace C3OSS.Codexcw;

internal sealed class LineTooLongException(int maxBytes)
    : Exception($"line exceeds the {maxBytes}-byte scan limit");

/// <summary>
/// Splits a byte stream into lines with a hard per-line length cap, the
/// equivalent of Go's bufio.Scanner buffer limit.
/// </summary>
internal static class JsonlReader
{
    public static async IAsyncEnumerable<string> ReadLinesAsync(
        Stream stream,
        int maxBytes,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var chunk = new byte[64 * 1024];
        var pending = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (chunk[i] != (byte)'\n')
                {
                    continue;
                }
                pending.Write(chunk, start, i - start);
                if (pending.Length > maxBytes)
                {
                    throw new LineTooLongException(maxBytes);
                }
                yield return TakeLine(pending);
                start = i + 1;
            }
            pending.Write(chunk, start, read - start);
            if (pending.Length > maxBytes)
            {
                throw new LineTooLongException(maxBytes);
            }
        }
        if (pending.Length > 0)
        {
            yield return TakeLine(pending);
        }
    }

    private static string TakeLine(MemoryStream pending)
    {
        var bytes = pending.GetBuffer().AsSpan(0, (int)pending.Length);
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
        {
            bytes = bytes[..^1];
        }
        var line = Encoding.UTF8.GetString(bytes);
        pending.SetLength(0);
        return line;
    }
}
