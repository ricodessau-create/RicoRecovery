using System.Buffers.Binary;
using RicoRecovery.Core.Abstractions;

namespace RicoRecovery.FileSystems.Ext;

internal sealed record ExtGroup(long BlockBitmap, long InodeBitmap, long InodeTable, ushort Flags, uint ItableUnused);

internal sealed class ExtGroupTable
{
    private const long MaxTableBytes = 64L * 1024 * 1024;

    private readonly ExtGroup[] groups;

    private ExtGroupTable(ExtGroup[] groups)
    {
        this.groups = groups;
    }

    public int Count => groups.Length;

    public ExtGroup this[int index] => groups[index];

    public static async Task<ExtGroupTable?> ReadAsync(IBlockSource source, long partitionOffset, ExtVolume volume, CancellationToken cancellationToken)
    {
        var groupCount = volume.GroupCount;
        var length = (long)groupCount * volume.DescriptorSize;

        if (groupCount < 1 || length > MaxTableBytes)
        {
            return null;
        }

        var data = await BlockReader.ReadAsync(source, partitionOffset + volume.GroupDescriptorOffset, (int)length, cancellationToken).ConfigureAwait(false);

        if (data is null)
        {
            return null;
        }

        var groups = new ExtGroup[groupCount];

        for (var index = 0; index < groupCount; index++)
        {
            groups[index] = ParseGroup(data, index * volume.DescriptorSize, volume.DescriptorSize);
        }

        return new ExtGroupTable(groups);
    }

    private static ExtGroup ParseGroup(byte[] data, int offset, int size)
    {
        long blockBitmap = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
        long inodeBitmap = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
        long inodeTable = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8));
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 18));
        uint unused = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 28));

        if (size >= 64)
        {
            blockBitmap |= (long)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 32)) << 32;
            inodeBitmap |= (long)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 36)) << 32;
            inodeTable |= (long)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 40)) << 32;
            unused |= (uint)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 50)) << 16;
        }

        return new ExtGroup(blockBitmap, inodeBitmap, inodeTable, flags, unused);
    }
}
