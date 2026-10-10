using System.Text;

namespace DownloadFaster.Core.Proxy;

/// <summary>
/// Reads from a connection with its own buffer, so the proxy can parse HTTP heads and SOCKS
/// handshakes and still hand every byte that followed them on to the other side.
/// </summary>
internal sealed class ProxyReader(Stream stream, int bufferSize = 16 * 1024)
{
    private readonly byte[] _buffer = new byte[bufferSize];
    private byte[]? _copyBuffer;
    private int _start;
    private int _end;

    public Stream Stream { get; } = stream;

    public int Buffered => _end - _start;

    /// <summary>
    /// Reads an HTTP head up to and including the blank line. Returns null if the connection
    /// closed before the first byte; throws if it closed midway or the head is too large.
    /// </summary>
    public async Task<HttpHead?> ReadHeadAsync(CancellationToken cancellationToken, int maxBytes = 64 * 1024)
    {
        var head = new List<byte>(512);
        int matched = 0;
        while (true)
        {
            if (_start == _end && !await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                return head.Count == 0 ? null : throw new IOException("The connection closed in the middle of an HTTP header.");
            }

            while (_start < _end)
            {
                byte b = _buffer[_start++];

                // Ignore stray line breaks between messages, as RFC 9112 allows.
                if (head.Count == 0 && b is (byte)'\r' or (byte)'\n')
                {
                    continue;
                }

                head.Add(b);
                matched = b == (matched is 0 or 2 ? '\r' : '\n') ? matched + 1 : b == '\r' ? 1 : 0;
                if (matched == 4)
                {
                    return HttpHead.Parse(Encoding.Latin1.GetString(head.ToArray()));
                }

                if (head.Count > maxBytes)
                {
                    throw new InvalidDataException("HTTP header too large.");
                }
            }
        }
    }

    /// <summary>Reads one CRLF-terminated line (without the CRLF).</summary>
    public async Task<string> ReadLineAsync(CancellationToken cancellationToken, int maxBytes = 8 * 1024)
    {
        var line = new StringBuilder();
        while (true)
        {
            if (_start == _end && !await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new IOException("The connection closed in the middle of a line.");
            }

            while (_start < _end)
            {
                char c = (char)_buffer[_start++];
                if (c == '\n')
                {
                    return line.ToString().TrimEnd('\r');
                }

                line.Append(c);
                if (line.Length > maxBytes)
                {
                    throw new InvalidDataException("Line too long.");
                }
            }
        }
    }

    public async Task<byte[]> ReadExactAsync(int count, CancellationToken cancellationToken)
    {
        var result = new byte[count];
        int filled = 0;
        while (filled < count)
        {
            int read = await ReadAsync(result.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("The connection closed early.");
            }

            filled += read;
        }

        return result;
    }

    public async Task<byte> ReadByteAsync(CancellationToken cancellationToken) =>
        (await ReadExactAsync(1, cancellationToken).ConfigureAwait(false))[0];

    /// <summary>Returns the next byte without consuming it, or -1 if the connection closed.</summary>
    public async Task<int> PeekByteAsync(CancellationToken cancellationToken)
    {
        if (_start == _end && !await FillAsync(cancellationToken).ConfigureAwait(false))
        {
            return -1;
        }

        return _buffer[_start];
    }

    /// <summary>Returns already-buffered bytes first, then reads from the connection.</summary>
    public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (_start < _end)
        {
            int count = Math.Min(destination.Length, _end - _start);
            _buffer.AsMemory(_start, count).CopyTo(destination);
            _start += count;
            return count;
        }

        return await Stream.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Copies exactly <paramref name="count"/> bytes to <paramref name="destination"/>.</summary>
    public async Task CopyExactAsync(long count, Stream destination, Func<int, ValueTask>? onBytes, CancellationToken cancellationToken)
    {
        byte[] chunk = _copyBuffer ??= new byte[64 * 1024];
        while (count > 0)
        {
            int read = await ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, count)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("The connection closed before the message body was complete.");
            }

            await destination.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            count -= read;
            if (onBytes is not null)
            {
                await onBytes(read).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Copies a chunked body, framing included, so the receiver sees it unchanged.</summary>
    public async Task CopyChunkedAsync(Stream destination, Func<int, ValueTask>? onBytes, CancellationToken cancellationToken)
    {
        while (true)
        {
            string sizeLine = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            await WriteLineAsync(destination, sizeLine, cancellationToken).ConfigureAwait(false);
            string hex = sizeLine.Split(';', 2)[0].Trim();
            if (!long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out long size) || size < 0)
            {
                throw new InvalidDataException("Bad chunk size.");
            }

            if (size == 0)
            {
                // Trailer fields, then the final empty line.
                while (true)
                {
                    string trailer = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    await WriteLineAsync(destination, trailer, cancellationToken).ConfigureAwait(false);
                    if (trailer.Length == 0)
                    {
                        return;
                    }
                }
            }

            await CopyExactAsync(size, destination, onBytes, cancellationToken).ConfigureAwait(false);
            string end = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            await WriteLineAsync(destination, end, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Copies everything until the connection closes. Returns the byte count.</summary>
    public async Task<long> CopyToEndAsync(Stream destination, Func<int, ValueTask>? onBytes, CancellationToken cancellationToken)
    {
        byte[] chunk = _copyBuffer ??= new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
            if (onBytes is not null)
            {
                await onBytes(read).ConfigureAwait(false);
            }
        }

        return total;
    }

    private static Task WriteLineAsync(Stream destination, string line, CancellationToken cancellationToken) =>
        destination.WriteAsync(Encoding.Latin1.GetBytes(line + "\r\n"), cancellationToken).AsTask();

    private async Task<bool> FillAsync(CancellationToken cancellationToken)
    {
        _start = 0;
        _end = await Stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
        return _end > 0;
    }
}

/// <summary>A parsed HTTP request or response head.</summary>
internal sealed class HttpHead
{
    private HttpHead(string startLine, List<KeyValuePair<string, string>> headers)
    {
        StartLine = startLine;
        Headers = headers;
    }

    public string StartLine { get; set; }

    public List<KeyValuePair<string, string>> Headers { get; }

    public static HttpHead Parse(string text)
    {
        string[] lines = text.Split("\r\n");
        var headers = new List<KeyValuePair<string, string>>();
        foreach (string line in lines.Skip(1))
        {
            if (line.Length == 0)
            {
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                throw new InvalidDataException("Malformed HTTP header line.");
            }

            headers.Add(new(line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }

        return new HttpHead(lines[0], headers);
    }

    public string? Get(string name) =>
        Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    public bool HasToken(string name, string token) =>
        Headers.Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(h => h.Value.Split(','))
            .Any(v => v.Trim().Equals(token, StringComparison.OrdinalIgnoreCase));

    public void Remove(string name) =>
        Headers.RemoveAll(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase));

    public void Set(string name, string value)
    {
        Remove(name);
        Headers.Add(new(name, value));
    }

    public byte[] ToBytes()
    {
        var sb = new StringBuilder(StartLine).Append("\r\n");
        foreach (var (name, value) in Headers)
        {
            sb.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        return Encoding.Latin1.GetBytes(sb.Append("\r\n").ToString());
    }
}
