using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;

namespace RicoRecovery.FileSystems;

public sealed record ContentCopyResult(long BytesWritten, long UnreadableBytes);

public static class EntryContentReader
{
    private const int ChunkBytes = 1 << 20;

    public static async Task<ContentCopyResult> CopyToAsync(
        IBlockSource source,
        FileSystemEntry entry,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(destination);

        if (entry.IsDirectory)
        {
            return new ContentCopyResult(0, 0);
        }

        if (entry.ResidentData is not null)
        {
            var length = (int)Math.Min(entry.Size, entry.ResidentData.Length);
            await destination.WriteAsync(entry.ResidentData.AsMemory(0, length), cancellationToken).ConfigureAwait(false);

            return new ContentCopyResult(length, 0);
        }

        var remaining = entry.Size;
        long written = 0;
        long unreadable = 0;

        foreach (var run in entry.DataRuns)
        {
            if (remaining <= 0)
            {
                break;
            }

            var runRemaining = Math.Min(run.Length, remaining);
            var offset = run.Offset;

            while (runRemaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunk = (int)Math.Min(runRemaining, ChunkBytes);
                byte[] data;

                if (run.IsSparse)
                {
                    data = new byte[chunk];
                }
                else
                {
                    var result = await ReadTolerantAsync(source, offset, chunk, cancellationToken).ConfigureAwait(false);
                    data = result.Data;
                    unreadable += result.Unreadable;
                }

                await destination.WriteAsync(data, cancellationToken).ConfigureAwait(false);

                written += chunk;
                offset += chunk;
                runRemaining -= chunk;
                remaining -= chunk;
            }
        }

        return new ContentCopyResult(written, unreadable);
    }

    private static async Task<(byte[] Data, long Unreadable)> ReadTolerantAsync(
        IBlockSource source,
        long offset,
        int length,
        CancellationToken cancellationToken)
    {
        var whole = await BlockReader.ReadAsync(source, offset, length, cancellationToken).ConfigureAwait(false);

        if (whole is not null)
        {
            return (whole, 0);
        }

        var data = new byte[length];
        long unreadable = 0;
        var step = source.SectorSize;

        for (var position = 0; position < length; position += step)
        {
            var count = Math.Min(step, length - position);
            var part = await BlockReader.ReadAsync(source, offset + position, count, cancellationToken).ConfigureAwait(false);

            if (part is null)
            {
                unreadable += count;
            }
            else
            {
                part.CopyTo(data, position);
            }
        }

        return (data, unreadable);
    }
}
