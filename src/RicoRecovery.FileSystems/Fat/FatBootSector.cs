using System.Buffers.Binary;
using System.Text;
using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;

namespace RicoRecovery.FileSystems.Fat;

internal static class FatBootSector
{
    public static async Task<FatVolume?> ReadAsync(IBlockSource source, long partitionOffset, CancellationToken cancellationToken)
    {
        var primary = await BlockReader.ReadAsync(source, partitionOffset, 512, cancellationToken).ConfigureAwait(false);
        var volume = primary is null ? null : TryParse(primary);

        if (volume is not null)
        {
            return volume;
        }

        var backup = await BlockReader.ReadAsync(source, partitionOffset + 6L * source.SectorSize, 512, cancellationToken).ConfigureAwait(false);
        var backupVolume = backup is null ? null : TryParse(backup);

        return backupVolume is { Type: FileSystemType.Fat32 } ? backupVolume with { FromBackup = true } : null;
    }

    public static FatVolume? TryParse(byte[] sector)
    {
        if (sector.Length < 512 || BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(510)) != 0xAA55)
        {
            return null;
        }

        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(11));
        int sectorsPerCluster = sector[13];
        int reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(14));
        int fatCount = sector[16];
        int rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(17));
        long totalSectors = BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(19));
        long fatSectors = BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(22));

        if (totalSectors == 0)
        {
            totalSectors = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(32));
        }

        var usesFat32Layout = fatSectors == 0;

        if (usesFat32Layout)
        {
            fatSectors = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(36));
        }

        if (bytesPerSector < 512 || bytesPerSector > 4096 || !IsPowerOfTwo(bytesPerSector))
        {
            return null;
        }

        if (sectorsPerCluster > 128 || !IsPowerOfTwo(sectorsPerCluster))
        {
            return null;
        }

        if (reservedSectors == 0 || fatCount < 1 || fatCount > 2 || fatSectors == 0 || totalSectors == 0)
        {
            return null;
        }

        var rootDirectorySectors = ((long)rootEntries * 32 + bytesPerSector - 1) / bytesPerSector;
        var dataSectors = totalSectors - (reservedSectors + fatCount * fatSectors + rootDirectorySectors);

        if (dataSectors <= 0)
        {
            return null;
        }

        var clusterCount = dataSectors / sectorsPerCluster;

        if (clusterCount < 4085 || clusterCount > 0x0FFFFFF5)
        {
            return null;
        }

        var type = clusterCount < 65525 ? FileSystemType.Fat16 : FileSystemType.Fat32;
        uint rootCluster = 0;

        if (type == FileSystemType.Fat32)
        {
            if (rootEntries != 0 || !usesFat32Layout)
            {
                return null;
            }

            rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(44));

            if (rootCluster < 2 || rootCluster > clusterCount + 1)
            {
                return null;
            }
        }
        else if (rootEntries == 0 || usesFat32Layout)
        {
            return null;
        }

        var entrySize = type == FileSystemType.Fat16 ? 2 : 4;

        if (fatSectors * bytesPerSector < (clusterCount + 2) * entrySize)
        {
            return null;
        }

        return new FatVolume(
            type,
            bytesPerSector,
            sectorsPerCluster,
            reservedSectors,
            fatCount,
            rootEntries,
            fatSectors,
            totalSectors,
            (uint)clusterCount,
            rootCluster,
            ReadLabel(sector, type),
            false);
    }

    private static string ReadLabel(byte[] sector, FileSystemType type)
    {
        var signatureOffset = type == FileSystemType.Fat16 ? 38 : 66;
        var labelOffset = type == FileSystemType.Fat16 ? 43 : 71;

        if (sector[signatureOffset] != 0x29)
        {
            return string.Empty;
        }

        var label = Encoding.Latin1.GetString(sector, labelOffset, 11).Trim();

        return label == "NO NAME" ? string.Empty : label;
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
}
