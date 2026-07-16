using System.Text;

namespace C3OSS.Codexcw;

/// <summary>Keeps only the last <c>limit</c> bytes written to it.</summary>
internal sealed class TailBuffer(int limit)
{
    private readonly object _lock = new();
    private byte[] _data = [];

    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (limit <= 0 || bytes.IsEmpty)
        {
            return;
        }
        lock (_lock)
        {
            var combined = new byte[_data.Length + bytes.Length];
            _data.CopyTo(combined, 0);
            bytes.CopyTo(combined.AsSpan(_data.Length));
            _data = combined.Length > limit ? combined[^limit..] : combined;
        }
    }

    public async Task PumpAsync(Stream source, CancellationToken cancellationToken)
    {
        var chunk = new byte[8 * 1024];
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }
                Write(chunk.AsSpan(0, read));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The stream was force-closed by the bounded drain or the run was
            // cancelled; whatever was captured so far is the tail.
        }
    }

    public override string ToString()
    {
        lock (_lock)
        {
            return Encoding.UTF8.GetString(_data);
        }
    }
}
