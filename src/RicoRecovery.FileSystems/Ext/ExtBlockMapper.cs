using System.Buffers.Binary;
using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;

namespace RicoRecovery.FileSystems.Ext;

internal sealed record ExtMapping(List<DataRun> Runs, bool Complete);

internal sealed record ExtExtent(long Logical, long Length, long Physical, bool Unwritten);

internal sealed class ExtRunBuilder(long partitionOffset, int blockSize)
{
    private readonly List<DataRun> runs = [];
    private long start = -1;
    private long length;
    private bool sparse;

    public long Blocks { get; private set; }

    public void Add(long block)
    {
        if (block == 0)
        {
            AddHoles(1);
        }
        else
        {
            AddRange(block, 1);
        }
    }

    public void AddRange(long firstBlock, long count)
    {
        if (count <= 0)
        {
            return;
        }

        Append(partitionOffset + firstBlock * blockSize, count * blockSize, false);
        Blocks += count;
    }

    public void AddHoles(long count)
    {
        if (count <= 0)
        {
            return;
        }

        Append(0, count * blockSize, true);
        Blocks += count;
    }

    public List<DataRun> Build()
    {
        Flush();

        return runs;
    }

    private void Append(long offset, long byteLength, bool isSparse)
    {
        if (start >= 0 && sparse == isSparse && (isSparse || offset == start + length))
        {
            length += byteLength;
            return;
        }

        Flush();
        start = offset;
        length = byteLength;
        sparse = isSparse;
    }

    private void Flush()
    {
        if (start < 0)
        {
            return;
        }

        runs.Add(new DataRun(start, length, sparse));
        start = -1;
        length = 0;
    }
}

internal sealed class ExtBlockMapper(IBlockSource source, long partitionOffset, ExtVolume volume)
{
    private const int MaxTreeDepth = 5;

    public async Task<ExtMapping> MapAsync(ExtInode inode, long maxBytes, CancellationToken cancellationToken)
    {
        if (inode.HasInlineData)
        {
            return new ExtMapping([], false);
        }

        var size = Math.Min(inode.Size, maxBytes);
        var needed = (size + volume.BlockSize - 1) / volume.BlockSize;
        var builder = new ExtRunBuilder(partitionOffset, volume.BlockSize);

        if (needed <= 0)
        {
            return new ExtMapping([], true);
        }

        var mapped = inode.UsesExtents
            ? await MapExtentsAsync(inode, needed, builder, cancellationToken).ConfigureAwait(false)
            : await MapPointersAsync(inode, needed, builder, cancellationToken).ConfigureAwait(false);

        return new ExtMapping(builder.Build(), mapped && builder.Blocks >= needed);
    }

    private async Task<bool> MapPointersAsync(ExtInode inode, long needed, ExtRunBuilder builder, CancellationToken cancellationToken)
    {
        for (var index = 0; index < 12; index++)
        {
            if (builder.Blocks >= needed)
            {
                return true;
            }

            var block = BinaryPrimitives.ReadUInt32LittleEndian(inode.Blocks.AsSpan(index * 4));

            if (block != 0 && block >= volume.BlocksCount)
            {
                return false;
            }

            builder.Add(block);
        }

        for (var level = 1; level <= 3; level++)
        {
            if (builder.Blocks >= needed)
            {
                return true;
            }

            var pointer = BinaryPrimitives.ReadUInt32LittleEndian(inode.Blocks.AsSpan((11 + level) * 4));

            if (!await AddIndirectAsync(pointer, level, needed, builder, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return builder.Blocks >= needed;
    }

    private async Task<bool> AddIndirectAsync(uint block, int level, long needed, ExtRunBuilder builder, CancellationToken cancellationToken)
    {
        var perBlock = volume.BlockSize / 4;

        if (block == 0)
        {
            builder.AddHoles(Math.Min(needed - builder.Blocks, Power(perBlock, level)));

            return true;
        }

        if (block >= volume.BlocksCount)
        {
            return false;
        }

        var data = await BlockReader.ReadAsync(source, partitionOffset + block * (long)volume.BlockSize, volume.BlockSize, cancellationToken).ConfigureAwait(false);

        if (data is null)
        {
            return false;
        }

        for (var index = 0; index < perBlock && builder.Blocks < needed; index++)
        {
            var pointer = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(index * 4));

            if (level == 1)
            {
                if (pointer != 0 && pointer >= volume.BlocksCount)
                {
                    return false;
                }

                builder.Add(pointer);
            }
            else if (!await AddIndirectAsync(pointer, level - 1, needed, builder, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> MapExtentsAsync(ExtInode inode, long needed, ExtRunBuilder builder, CancellationToken cancellationToken)
    {
        var extents = new List<ExtExtent>();

        if (!await CollectAsync(inode.Blocks, 0, inode.Blocks.Length, MaxTreeDepth, extents, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        extents.Sort((left, right) => left.Logical.CompareTo(right.Logical));

        long position = 0;

        foreach (var extent in extents)
        {
            if (position >= needed)
            {
                break;
            }

            if (extent.Logical < position)
            {
                continue;
            }

            if (extent.Logical > position)
            {
                var gap = Math.Min(extent.Logical, needed) - position;
                builder.AddHoles(gap);
                position += gap;

                if (position >= needed)
                {
                    break;
                }
            }

            var count = Math.Min(extent.Length, needed - position);

            if (extent.Unwritten)
            {
                builder.AddHoles(count);
            }
            else
            {
                builder.AddRange(extent.Physical, count);
            }

            position += count;
        }

        if (position < needed && !inode.IsDeleted)
        {
            builder.AddHoles(needed - position);
        }

        return true;
    }

    private async Task<bool> CollectAsync(
        byte[] node,
        int nodeOffset,
        int nodeLength,
        int remainingDepth,
        List<ExtExtent> extents,
        CancellationToken cancellationToken)
    {
        if (nodeLength < 12 || BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(nodeOffset)) != 0xF30A)
        {
            return false;
        }

        int entries = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(nodeOffset + 2));
        int depth = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(nodeOffset + 6));

        if (depth > remainingDepth || 12 + entries * 12 > nodeLength)
        {
            return false;
        }

        for (var index = 0; index < entries; index++)
        {
            var entry = nodeOffset + 12 + index * 12;

            if (depth == 0)
            {
                long logical = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(entry));
                int rawLength = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(entry + 4));
                long startHigh = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(entry + 6));
                long startLow = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(entry + 8));
                var unwritten = rawLength > 32768;
                var length = unwritten ? rawLength - 32768 : rawLength;
                var physical = (startHigh << 32) | startLow;

                if (length == 0 || physical + length > volume.BlocksCount)
                {
                    return false;
                }

                extents.Add(new ExtExtent(logical, length, physical, unwritten));
                continue;
            }

            long leafLow = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(entry + 4));
            long leafHigh = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(entry + 8));
            var leaf = (leafHigh << 32) | leafLow;

            if (leaf <= 0 || leaf >= volume.BlocksCount)
            {
                return false;
            }

            var child = await BlockReader.ReadAsync(source, partitionOffset + leaf * volume.BlockSize, volume.BlockSize, cancellationToken).ConfigureAwait(false);

            if (child is null || !await CollectAsync(child, 0, child.Length, depth - 1, extents, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    private static long Power(long value, int exponent)
    {
        var result = 1L;

        for (var index = 0; index < exponent; index++)
        {
            result *= value;
        }

        return result;
    }
}
