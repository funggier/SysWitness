namespace SysWitness.Mcp;

/// <summary>Bounds a frame while reading, rather than allocating an unlimited ReadLine string first.</summary>
internal sealed class BoundedLineReader(Stream input, int maximumBytes)
{
    private readonly byte[] chunk = new byte[4096];
    private int position;
    private int available;

    public async ValueTask<(byte[] Data, bool InvalidFrame)?> ReadAsync(CancellationToken cancellationToken)
    {
        using var line = new MemoryStream();
        var invalid = false;
        while (true)
        {
            if (position == available)
            {
                available = await input.ReadAsync(chunk, cancellationToken);
                position = 0;
                if (available == 0)
                    return line.Length == 0 && !invalid ? null : ([], true);
            }
            var end = Array.IndexOf(chunk, (byte)'\n', position, available - position);
            var length = (end < 0 ? available : end) - position;
            if (!invalid)
            {
                if (line.Length + length > maximumBytes) invalid = true;
                else line.Write(chunk, position, length);
            }
            position += length;
            if (end < 0) continue;
            position++;
            if (invalid) return ([], true);
            var bytes = line.ToArray();
            if (bytes.Length > 0 && bytes[^1] == '\r') Array.Resize(ref bytes, bytes.Length - 1);
            return (bytes, false);
        }
    }
}
