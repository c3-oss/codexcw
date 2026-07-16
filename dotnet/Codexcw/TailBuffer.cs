using System.Text;

namespace C3OSS.Codexcw;

/// <summary>
/// Keeps only the last <c>limit</c> bytes written to it. Writes go into a
/// fixed circular buffer, so a chatty stream costs no allocation per chunk.
/// </summary>
internal sealed class TailBuffer(int limit)
{
    private readonly object _lock = new();
    private readonly byte[] _buffer = new byte[Math.Max(0, limit)];
    private int _next;
    private int _count;

    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (limit <= 0 || bytes.IsEmpty)
        {
            return;
        }
        lock (_lock)
        {
            if (bytes.Length >= limit)
            {
                bytes[^limit..].CopyTo(_buffer);
                _next = 0;
                _count = limit;
                return;
            }
            var head = Math.Min(bytes.Length, limit - _next);
            bytes[..head].CopyTo(_buffer.AsSpan(_next));
            bytes[head..].CopyTo(_buffer);
            _next = (_next + bytes.Length) % limit;
            _count = Math.Min(limit, _count + bytes.Length);
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
            if (_count == 0)
            {
                return "";
            }
            var start = _count < limit ? 0 : _next;
            var ordered = new byte[_count];
            var head = Math.Min(_count, limit - start);
            _buffer.AsSpan(start, head).CopyTo(ordered);
            _buffer.AsSpan(0, _count - head).CopyTo(ordered.AsSpan(head));
            return Encoding.UTF8.GetString(ordered);
        }
    }
}
