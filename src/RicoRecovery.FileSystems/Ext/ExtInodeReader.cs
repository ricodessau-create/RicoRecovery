using RicoRecovery.Core.Abstractions;

namespace RicoRecovery.FileSystems.Ext;

internal sealed class ExtInodeReader
{
    private const int MaxCachedBlocks = 256;

    private readonly IBlockSource source;
    private readonly long partitionOffset;
    private readonly ExtVolume volume;
    private readonly ExtGroupTable groups;
    private readonly Dictionary<long, byte[]?> cache = [];

    public ExtInodeReader(IBlockSource source, long partitionOffset, ExtVolume volume, ExtGroupTable groups)
    {
        this.source = source;
        this.partitionOffset = partitionOffset;
        this.volume = volume;
        this.groups = groups;
    }

    public async Task<ExtInode?> ReadAsync(uint number, CancellationToken cancellationToken)
    {
        if (number == 0 || number > volume.InodesCount)
        {
            return null;
        }

        var group = (int)((number - 1) / volume.InodesPerGroup);
        var index = (number - 1) % volume.InodesPerGroup;

        if (group >= groups.Count || groups[group].InodeTable <= 0)
        {
            return null;
        }

        var byteOffset = groups[group].InodeTable * volume.BlockSize + (long)index * volume.InodeSize;
        var blockIndex = byteOffset / volume.BlockSize;
        var within = (int)(byteOffset % volume.BlockSize);

        if (!cache.TryGetValue(blockIndex, out var block))
        {
            block = await BlockReader.ReadAsync(source, partitionOffset + blockIndex * volume.BlockSize, volume.BlockSize, cancellationToken).ConfigureAwait(false);

            if (cache.Count >= MaxCachedBlocks)
            {
                cache.Clear();
            }

            cache[blockIndex] = block;
        }

        return block is null ? null : ExtInode.Parse(number, block, within);
    }
}
