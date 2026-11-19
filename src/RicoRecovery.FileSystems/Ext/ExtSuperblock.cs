using System.Buffers.Binary;
using System.Text;
using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;

namespace RicoRecovery.FileSystems.Ext;

internal static class ExtSuperblock
{
    private static readonly int[] BackupBlockSizes = [1024, 2048, 4096];

    public static async Task<ExtVolume?> ReadAsync(IBlockSource source, long partitionOffset, CancellationToken cancellationToken)
    {
        var primary = await BlockReader.ReadAsync(source, partitionOffset + 1024, 1024, cancellationToken).ConfigureAwait(false);
        var volume = primary is null ? null : TryParse(primary, null);

        if (volume is not null)
        {
            return volume;
        }

        foreach (var blockSize in BackupBlockSizes)
        {
            var block = 8L * blockSize + (blockSize == 1024 ? 1 : 0);
            var data = await BlockReader.ReadAsync(source, partitionOffset + block * blockSize, 1024, cancellationToken).ConfigureAwait(false);
            var candidate = data is null ? null : TryParse(data, block);

            if (candidate is not null && candidate.BlockSize == blockSize)
            {
                return candidate with { FromBackup = true };
            }
        }

        return null;
    }

    public static ExtVolume? TryParse(byte[] superblock, long? backupBlock)
    {
        if (superblock.Length < 1024 || BinaryPrimitives.ReadUInt16LittleEndian(superblock.AsSpan(56)) != 0xEF53)
        {
            return null;
        }

        var logBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(24));

        if (logBlockSize > 6)
        {
            return null;
        }

        var blockSize = 1024 << (int)logBlockSize;
        var inodesCount = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(0));
        var blocksLow = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(4));
        var firstDataBlock = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(20));
        var blocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(32));
        var inodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(40));
        var revision = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(76));
        var compatible = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(92));
        var incompatible = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(96));
        var readOnlyCompatible = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(100));
        uint firstInode = 11;
        var inodeSize = 128;

        if (revision >= 1)
        {
            firstInode = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(84));
            inodeSize = BinaryPrimitives.ReadUInt16LittleEndian(superblock.AsSpan(88));
        }

        if ((incompatible & 0x9) != 0)
        {
            return null;
        }

        if (blocksPerGroup == 0 || inodesPerGroup == 0 || inodesCount == 0 || blocksLow == 0)
        {
            return null;
        }

        if (blocksPerGroup > (uint)blockSize * 8 || !IsPowerOfTwo(inodeSize) || inodeSize < 128 || inodeSize > blockSize)
        {
            return null;
        }

        var descriptorSize = 32;
        long blocks = blocksLow;

        if ((incompatible & 0x80) != 0)
        {
            descriptorSize = BinaryPrimitives.ReadUInt16LittleEndian(superblock.AsSpan(254));

            if (descriptorSize < 64 || descriptorSize > 1024 || !IsPowerOfTwo(descriptorSize))
            {
                return null;
            }

            blocks |= (long)BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(336)) << 32;
        }

        var groupCount = (blocks - firstDataBlock + blocksPerGroup - 1) / blocksPerGroup;

        if (groupCount < 1 || groupCount > 8_000_000 || groupCount * inodesPerGroup < inodesCount)
        {
            return null;
        }

        var type = (incompatible & 0x2C0) != 0 || (readOnlyCompatible & 0x778) != 0
            ? FileSystemType.Ext4
            : (compatible & 0x4) != 0 ? FileSystemType.Ext3 : FileSystemType.Ext2;

        var descriptorBlock = backupBlock is { } backup ? backup + 1 : firstDataBlock + 1L;
        var label = Encoding.UTF8.GetString(superblock, 120, 16).TrimEnd('\0');

        return new ExtVolume(
            type,
            blockSize,
            inodesCount,
            blocks,
            firstDataBlock,
            blocksPerGroup,
            inodesPerGroup,
            firstInode,
            inodeSize,
            descriptorSize,
            incompatible,
            readOnlyCompatible,
            label,
            descriptorBlock * blockSize,
            false);
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
}
