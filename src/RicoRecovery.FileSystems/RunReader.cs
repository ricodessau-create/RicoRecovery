using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;

namespace RicoRecovery.FileSystems;

internal static class RunReader
{
    private const int ChunkBytes = 1 << 20;

    public static async Task<byte[]> ReadAllAsync(IBlockSource source, IReadOnlyList<DataRun> runs, long maxBytes, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();

        foreach (var run in runs)
        {
            var remaining = Math.Min(run.Length, maxBytes - stream.Length);
            var offset = run.Offset;

            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunk = (int)Math.Min(remaining, ChunkBytes);

                if (run.IsSparse)
                {
                    stream.Write(new byte[chunk]);
                }
                else
                {
                    var data = await BlockReader.ReadAsync(source, offset, chunk, cancellationToken).ConfigureAwait(false);

                    if (data is null)
                    {
                        return stream.ToArray();
                    }

                    stream.Write(data);
                    offset += chunk;
                }

                remaining -= chunk;
            }

            if (stream.Length >= maxBytes)
            {
                break;
            }
        }

        return stream.ToArray();
    }
}
