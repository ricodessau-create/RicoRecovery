using System.Text;

namespace RicoRecovery.Tests.TestSupport;

internal sealed class Fat16ImageBuilder
{
    public const int SectorSize = 512;
    public const int TotalSectors = 8192;
    public const int FatSectors = 32;
    public const int RootSectors = 32;
    public const int DataStartSector = 1 + 2 * FatSectors + RootSectors;

    private readonly Dictionary<int, int> slots = [];

    public Fat16ImageBuilder()
    {
        Image = new byte[TotalSectors * SectorSize];

        Encoding.ASCII.GetBytes("MSDOS5.0").CopyTo(Image, 3);
        ImageWriter.U16(Image, 11, SectorSize);
        Image[13] = 1;
        ImageWriter.U16(Image, 14, 1);
        Image[16] = 2;
        ImageWriter.U16(Image, 17, 512);
        ImageWriter.U16(Image, 19, TotalSectors);
        Image[21] = 0xF8;
        ImageWriter.U16(Image, 22, FatSectors);
        Image[38] = 0x29;
        Encoding.ASCII.GetBytes("TESTVOL    ").CopyTo(Image, 43);
        Image[510] = 0x55;
        Image[511] = 0xAA;

        SetFat(0, 0xFFF8);
        SetFat(1, 0xFFFF);
    }

    public byte[] Image { get; }

    public static int ClusterOffset(int cluster) => (DataStartSector + cluster - 2) * SectorSize;

    public void SetFat(int cluster, int value)
    {
        for (var copy = 0; copy < 2; copy++)
        {
            ImageWriter.U16(Image, (1 + copy * FatSectors) * SectorSize + cluster * 2, value);
        }
    }

    public void WriteData(int cluster, byte[] data) => data.CopyTo(Image, ClusterOffset(cluster));

    public void AddEntry(int directoryCluster, string shortName, byte attributes, int cluster, int size, bool deleted, string? longName = null)
    {
        var baseOffset = directoryCluster == 0 ? (1 + 2 * FatSectors) * SectorSize : ClusterOffset(directoryCluster);
        slots.TryGetValue(directoryCluster, out var slot);

        var shortBytes = EncodeShortName(shortName);

        if (longName is not null)
        {
            var parts = (longName.Length + 12) / 13;
            var checksum = Checksum(shortBytes);

            for (var part = parts; part >= 1; part--)
            {
                WriteLongPart(baseOffset + slot * 32, longName, part, part == parts, checksum, deleted);
                slot++;
            }
        }

        var offset = baseOffset + slot * 32;

        shortBytes.CopyTo(Image, offset);

        if (deleted)
        {
            Image[offset] = 0xE5;
        }

        Image[offset + 11] = attributes;
        ImageWriter.U16(Image, offset + 22, (12 << 11) | (30 << 5) | 5);
        ImageWriter.U16(Image, offset + 24, ((2024 - 1980) << 9) | (5 << 5) | 17);
        ImageWriter.U16(Image, offset + 26, cluster);
        ImageWriter.U32(Image, offset + 28, size);

        slots[directoryCluster] = slot + 1;
    }

    private static byte[] EncodeShortName(string name)
    {
        var dot = name.IndexOf('.');
        var main = dot < 0 ? name : name[..dot];
        var extension = dot < 0 ? string.Empty : name[(dot + 1)..];

        return Encoding.ASCII.GetBytes(main.PadRight(8) + extension.PadRight(3));
    }

    private static byte Checksum(byte[] shortBytes)
    {
        byte sum = 0;

        for (var index = 0; index < 11; index++)
        {
            sum = (byte)((((sum & 1) != 0 ? 0x80 : 0) + (sum >> 1) + shortBytes[index]) & 0xFF);
        }

        return sum;
    }

    private void WriteLongPart(int entryOffset, string longName, int part, bool isLast, byte checksum, bool deleted)
    {
        Image[entryOffset] = deleted ? (byte)0xE5 : (byte)(part | (isLast ? 0x40 : 0));
        Image[entryOffset + 11] = 0x0F;
        Image[entryOffset + 13] = checksum;

        var start = (part - 1) * 13;

        for (var index = 0; index < 13; index++)
        {
            var position = index < 5 ? 1 + index * 2 : index < 11 ? 14 + (index - 5) * 2 : 28 + (index - 11) * 2;
            var characterIndex = start + index;
            var character = characterIndex < longName.Length ? longName[characterIndex] : characterIndex == longName.Length ? '\0' : '\uFFFF';

            ImageWriter.U16(Image, entryOffset + position, character);
        }
    }
}
