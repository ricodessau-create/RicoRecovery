using System.Buffers.Binary;
using RicoRecovery.Core.Abstractions;

namespace RicoRecovery.FileSystems.ExFat;

internal static class ExFatBootSector
{
    public static async Task<ExFatVolume?> ReadAsync(IBlockSource source, long partitionOffset, CancellationToken cancellationToken)
    {
        var primary = await BlockReader.ReadAsync(source, partitionOffset, 512, cancellationToken).ConfigureAwait(false);
        var volume = primary is null ? null : TryParse(primary);

        if (volume is not null)
        {
            return volume;
        }

        var backup = await BlockReader.ReadAsync(source, partitionOffset + 12L * source.SectorSize, 512, cancellationToken).ConfigureAwait(false);
        var backupVolume = backup is null ? null : TryParse(backup);

        return backupVolume is null ? null : backupVolume with { FromBackup = true };
    }

    public static ExFatVolume? TryParse(byte[] sector)
    {
        if (sector.Length < 512 || BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(510)) != 0xAA55)
        {
            return null;
        }

        if (!sector.AsSpan(3, 8).SequenceEqual("EXFAT   "u8) || sector.AsSpan(11, 53).ContainsAnyExcept((byte)0))
        {
            return null;
        }

        var bytesPerSectorShift = (int)sector[108];
        var sectorsPerClusterShift = (int)sector[109];

        if (bytesPerSectorShift < 9 || bytesPerSectorShift > 12 || sectorsPerClusterShift > 25 - bytesPerSectorShift)
        {
            return null;
        }

        var volumeSectors = BinaryPrimitives.ReadInt64LittleEndian(sector.AsSpan(72));
        var fatOffset = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(80));
        var fatLength = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(84));
        var heapOffset = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(88));
        var clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(92));
        var rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(96));
        var fatCount = (int)sector[110];

        if (volumeSectors <= 0 || fatOffset < 1 || fatLength == 0 || heapOffset <= fatOffset)
        {
            return null;
        }

        if (clusterCount == 0 || clusterCount > 0xFFFFFFF5 || rootCluster < 2 || rootCluster > clusterCount + 1)
        {
            return null;
        }

        if (fatCount < 1 || fatCount > 2)
        {
            return null;
        }

        if (heapOffset + ((long)clusterCount << sectorsPerClusterShift) > volumeSectors)
        {
            return null;
        }

        if (((long)fatLength << bytesPerSectorShift) < ((long)clusterCount + 2) * 4)
        {
            return null;
        }

        return new ExFatVolume(
            1 << bytesPerSectorShift,
            1 << sectorsPerClusterShift,
            volumeSectors,
            fatOffset,
            fatLength,
            heapOffset,
            clusterCount,
            rootCluster,
            false);
    }
}
