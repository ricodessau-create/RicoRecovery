using RicoRecovery.Core.Abstractions;

namespace RicoRecovery.FileSystems;

internal static class BlockReader
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
}
