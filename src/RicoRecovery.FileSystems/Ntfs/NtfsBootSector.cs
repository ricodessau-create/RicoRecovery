using System.Buffers.Binary;
using RicoRecovery.Core.Abstractions;

namespace RicoRecovery.FileSystems.Ntfs;

internal static class NtfsBootSector
{
    public static async Task<NtfsVolume?> ReadAsync(IBlockSource source, long partitionOffset, CancellationToken cancellationToken)
    {
        var sector = await BlockReader.ReadAsync(source, partitionOffset, 512, cancellationToken).ConfigureAwait(false);

        return sector is null ? null : TryParse(sector);
    }

    public static NtfsVolume? TryParse(byte[] sector)
    {
        if (sector.Length < 512 || BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(510)) != 0xAA55)
        {
            return null;
        }

        if (!sector.AsSpan(3, 8).SequenceEqual("NTFS    "u8))
        {
            return null;
        }

        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(11));
        int rawClusterSectors = sector[13];
        var totalSectors = BinaryPrimitives.ReadInt64LittleEndian(sector.AsSpan(40));
        var mftCluster = BinaryPrimitives.ReadInt64LittleEndian(sector.AsSpan(48));
        var mirrorCluster = BinaryPrimitives.ReadInt64LittleEndian(sector.AsSpan(56));
        var rawRecord = (sbyte)sector[64];

        if (bytesPerSector < 512 || bytesPerSector > 4096 || !IsPowerOfTwo(bytesPerSector))
        {
            return null;
        }

        var sectorsPerCluster = rawClusterSectors;

        if (rawClusterSectors > 128)
        {
            var shift = 256 - rawClusterSectors;

            if (shift < 8 || shift > 12)
            {
                return null;
            }

            sectorsPerCluster = 1 << shift;
        }

        if (!IsPowerOfTwo(sectorsPerCluster))
        {
            return null;
        }

        var clusterBytes = bytesPerSector * sectorsPerCluster;
        var recordSize = rawRecord > 0 ? rawRecord * clusterBytes : 1 << -rawRecord;

        if (recordSize < 512 || recordSize > 65536 || !IsPowerOfTwo(recordSize))
        {
            return null;
        }

        var totalClusters = totalSectors / sectorsPerCluster;

        if (totalSectors <= 0 || mftCluster <= 0 || mirrorCluster <= 0)
        {
            return null;
        }

        if (mftCluster >= totalClusters || mirrorCluster >= totalClusters)
        {
            return null;
        }

        return new NtfsVolume(bytesPerSector, sectorsPerCluster, totalSectors, mftCluster, mirrorCluster, recordSize);
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
}
