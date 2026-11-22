using RicoRecovery.Core.Abstractions;

namespace RicoRecovery.Recovery.Carving;

internal static class SourceReader
{
    public static async Task<byte[]?> ReadAsync(IBlockSource source, long offset, int length, CancellationToken cancellationToken)
    {
        if (length < 0 || offset < 0 || offset > source.Length - length)
        {
            return null;
        }

        var buffer = new byte[length];
        var total = 0;

        try
        {
            while (total < length)
            {
                var read = await source.ReadAsync(offset + total, buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);

                if (read <= 0)
                {
                    return null;
                }

                total += read;
            }
        }
        catch (IOException)
        {
            return null;
        }

        return buffer;
    }

    public static async Task<byte[]> ReadAvailableAsync(IBlockSource source, long offset, int maxLength, CancellationToken cancellationToken)
    {
        var length = (int)Math.Min(maxLength, Math.Max(0L, source.Length - offset));

        if (length <= 0)
        {
            return Array.Empty<byte>();
        }

        return await ReadAsync(source, offset, length, cancellationToken).ConfigureAwait(false) ?? Array.Empty<byte>();
    }
}
