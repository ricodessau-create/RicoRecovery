using System.Buffers.Binary;
using System.Text;
using RicoRecovery.Storage.Partitioning;

namespace RicoRecovery.Tests.TestSupport;

internal sealed record GptPartitionSpec(Guid Type, long FirstLba, long LastLba, string Name);

internal static class DiskImageBuilder
{
    public const int SectorSize = 512;
    public const int GptEntryCount = 128;
    public const int GptEntrySize = 128;
    public const int GptEntrySectors = GptEntryCount * GptEntrySize / SectorSize;

    public static long BackupEntriesLba(int sectors) => sectors - 1 - GptEntrySectors;

    public static void WriteMbrSignature(byte[] image, long sectorIndex)
    {
        var offset = (int)(sectorIndex * SectorSize);
        image[offset + 510] = 0x55;
        image[offset + 511] = 0xAA;
    }

    public static void WriteMbrEntry(byte[] image, long sectorIndex, int slot, byte type, uint start, uint count, bool active)
    {
        var offset = (int)(sectorIndex * SectorSize) + 446 + slot * 16;

        image[offset] = active ? (byte)0x80 : (byte)0;
        image[offset + 4] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 8), start);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 12), count);
    }

    public static byte[] CreateGptImage(int sectors, params GptPartitionSpec[] partitions)
    {
        var image = new byte[sectors * SectorSize];

        WriteMbrEntry(image, 0, 0, 0xEE, 1, (uint)(sectors - 1), false);
        WriteMbrSignature(image, 0);

        var entries = new byte[GptEntryCount * GptEntrySize];

        for (var index = 0; index < partitions.Length; index++)
        {
            var span = entries.AsSpan(index * GptEntrySize, GptEntrySize);
            partitions[index].Type.TryWriteBytes(span[..16]);
            Guid.NewGuid().TryWriteBytes(span.Slice(16, 16));
            BinaryPrimitives.WriteInt64LittleEndian(span[32..], partitions[index].FirstLba);
            BinaryPrimitives.WriteInt64LittleEndian(span[40..], partitions[index].LastLba);
            Encoding.Unicode.GetBytes(partitions[index].Name).AsSpan().CopyTo(span[56..]);
        }

        var entriesCrc = Crc32.Compute(entries);
        var lastLba = sectors - 1;
        var backupEntriesLba = BackupEntriesLba(sectors);

        entries.CopyTo(image, 2 * SectorSize);
        entries.CopyTo(image, (int)(backupEntriesLba * SectorSize));

        WriteGptHeader(image, 1, 1, lastLba, 2, sectors, entriesCrc);
        WriteGptHeader(image, lastLba, lastLba, 1, backupEntriesLba, sectors, entriesCrc);

        return image;
    }

    private static void WriteGptHeader(byte[] image, long headerLba, long currentLba, long otherLba, long entriesLba, int sectors, uint entriesCrc)
    {
        var offset = (int)(headerLba * SectorSize);
        var span = image.AsSpan(offset, 92);

        Encoding.ASCII.GetBytes("EFI PART").AsSpan().CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 0x00010000);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], 92);
        BinaryPrimitives.WriteInt64LittleEndian(span[24..], currentLba);
        BinaryPrimitives.WriteInt64LittleEndian(span[32..], otherLba);
        BinaryPrimitives.WriteInt64LittleEndian(span[40..], 2 + GptEntrySectors);
        BinaryPrimitives.WriteInt64LittleEndian(span[48..], sectors - 2 - GptEntrySectors);
        Guid.NewGuid().TryWriteBytes(span.Slice(56, 16));
        BinaryPrimitives.WriteInt64LittleEndian(span[72..], entriesLba);
        BinaryPrimitives.WriteUInt32LittleEndian(span[80..], GptEntryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[84..], GptEntrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[88..], entriesCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], Crc32.Compute(span));
    }
}
