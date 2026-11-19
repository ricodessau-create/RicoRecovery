using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;

namespace RicoRecovery.FileSystems.Ext;

internal sealed class ExtBlockBitmapCache
{
    private const int MaxCachedGroups = 256;

    private readonly IBlockSource source;
    private readonly long partitionOffset;
    private readonly ExtVolume volume;
    private readonly ExtGroupTable groups;
    private readonly Dictionary<int, byte[]?> cache = [];

    public ExtBlockBitmapCache(IBlockSource source, long partitionOffset, ExtVolume volume, ExtGroupTable groups)
    {
        this.source = source;
        this.partitionOffset = partitionOffset;
        this.volume = volume;
        this.groups = groups;
    }

    public async Task<DataAvailability> EvaluateAsync(IReadOnlyList<DataRun> runs, CancellationToken cancellationToken)
    {
        long total = 0;
        long allocated = 0;

        foreach (var run in runs)
        {
            if (run.IsSparse)
            {
                continue;
            }

            var first = (run.Offset - partitionOffset) / volume.BlockSize;
            var count = run.Length / volume.BlockSize;

            for (long index = 0; index < count; index++)
            {
                var state = await IsAllocatedAsync(first + index, cancellationToken).ConfigureAwait(false);

                if (state is null)
                {
                    return DataAvailability.Unknown;
                }

                total++;

                if (state.Value)
                {
                    allocated++;
                }
            }
        }

        if (allocated == 0)
        {
            return DataAvailability.Unallocated;
        }

        return allocated == total ? DataAvailability.Reallocated : DataAvailability.PartiallyReallocated;
    }

    private async Task<bool?> IsAllocatedAsync(long block, CancellationToken cancellationToken)
    {
        var relative = block - volume.FirstDataBlock;

        if (relative < 0)
        {
            return null;
        }

        var group = (int)(relative / volume.BlocksPerGroup);

        if (group >= groups.Count || (volume.HasGroupChecksums && (groups[group].Flags & 0x2) != 0))
        {
            return null;
        }

        var bit = (int)(relative % volume.BlocksPerGroup);

        if (!cache.TryGetValue(group, out var bitmap))
        {
            bitmap = await BlockReader.ReadAsync(source, partitionOffset + groups[group].BlockBitmap * volume.BlockSize, volume.BlockSize, cancellationToken).ConfigureAwait(false);

            if (cache.Count >= MaxCachedGroups)
            {
                cache.Clear();
            }

            cache[group] = bitmap;
        }

        if (bitmap is null || (bit >> 3) >= bitmap.Length)
        {
            return null;
        }

        return (bitmap[bit >> 3] & (1 << (bit & 7))) != 0;
    }
}
