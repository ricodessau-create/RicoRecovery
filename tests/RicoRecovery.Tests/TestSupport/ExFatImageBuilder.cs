using System.Text;

namespace RicoRecovery.Tests.TestSupport;

internal sealed class ExFatImageBuilder
{
    public const int SectorSize = 512;
    public const int TotalSectors = 2048;
    public const int FatOffsetSectors = 24;
    public const int FatLengthSectors = 8;
    public const int HeapOffsetSectors = 32;
    public const int SectorsPerCluster = 8;
    public const int ClusterCount = 252;
    public const int ClusterSize = SectorsPerCluster * SectorSize;
    public const int BitmapCluster = 2;
    public const int RootCluster = 4;

    private const uint ModifiedStamp = (44u << 25) | (5u << 21) | (17u << 16) | (12u << 11) | (30u << 5) | 5u;

    private readonly Dictionary<int, int> slots = [];

    public ExFatImageBuilder()
    {
        Image = new byte[TotalSectors * SectorSize];

        Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(Image, 3);
        ImageWriter.U64(Image, 72, TotalSectors);
        ImageWriter.U32(Image, 80, FatOffsetSectors);
        ImageWriter.U32(Image, 84, FatLengthSectors);
        ImageWriter.U32(Image, 88, HeapOffsetSectors);
        ImageWriter.U32(Image, 92, ClusterCount);
        ImageWriter.U32(Image, 96, RootCluster);
        ImageWriter.U16(Image, 104, 0x0100);
        Image[108] = 9;
        Image[109] = 3;
        Image[110] = 1;
        Image[510] = 0x55;
        Image[511] = 0xAA;

        Array.Copy(Image, 0, Image, 12 * SectorSize, SectorSize);

        SetFat(0, 0xFFFFFFF8);
        SetFat(1, 0xFFFFFFFF);
        SetFat(BitmapCluster, 0xFFFFFFFF);
        SetFat(3, 0xFFFFFFFF);
        SetFat(RootCluster, 0xFFFFFFFF);

        MarkAllocated(BitmapCluster);
        MarkAllocated(3);
        MarkAllocated(RootCluster);

        var label = Reserve(RootCluster, 1);
        Image[label] = 0x83;
        Image[label + 1] = 4;
        Encoding.Unicode.GetBytes("TEST").CopyTo(Image, label + 2);

        var bitmap = Reserve(RootCluster, 1);
        Image[bitmap] = 0x81;
        ImageWriter.U32(Image, bitmap + 20, BitmapCluster);
        ImageWriter.U64(Image, bitmap + 24, 32);
    }

    public byte[] Image { get; }

    public static int ClusterOffset(int cluster) => (HeapOffsetSectors + (cluster - 2) * SectorsPerCluster) * SectorSize;

    public void SetFat(int cluster, long value) => ImageWriter.U32(Image, FatOffsetSectors * SectorSize + cluster * 4, value);

    public void MarkAllocated(int cluster)
    {
        var index = cluster - 2;
        Image[ClusterOffset(BitmapCluster) + (index >> 3)] |= (byte)(1 << (index & 7));
    }

    public void AddFile(int directoryCluster, string name, int cluster, byte[] data, bool deleted, bool allocated)
    {
        data.CopyTo(Image, ClusterOffset(cluster));

        AddSet(directoryCluster, name, cluster, data.Length, 0x20, deleted);

        if (allocated)
        {
            var clusters = (data.Length + ClusterSize - 1) / ClusterSize;

            for (var index = 0; index < clusters; index++)
            {
                MarkAllocated(cluster + index);
            }
        }
    }

    public void AddDirectory(int directoryCluster, string name, int cluster)
    {
        AddSet(directoryCluster, name, cluster, ClusterSize, 0x10, false);
        SetFat(cluster, 0xFFFFFFFF);
        MarkAllocated(cluster);
    }

    private void AddSet(int directoryCluster, string name, int cluster, int length, int attributes, bool deleted)
    {
        var nameEntries = (name.Length + 14) / 15;
        var offset = Reserve(directoryCluster, 2 + nameEntries);

        Image[offset] = deleted ? (byte)0x05 : (byte)0x85;
        Image[offset + 1] = (byte)(1 + nameEntries);
        ImageWriter.U16(Image, offset + 4, attributes);
        ImageWriter.U32(Image, offset + 12, ModifiedStamp);

        var stream = offset + 32;

        Image[stream] = deleted ? (byte)0x40 : (byte)0xC0;
        Image[stream + 1] = 0x03;
        Image[stream + 3] = (byte)name.Length;
        ImageWriter.U64(Image, stream + 8, length);
        ImageWriter.U32(Image, stream + 20, cluster);
        ImageWriter.U64(Image, stream + 24, length);

        for (var part = 0; part < nameEntries; part++)
        {
            var nameOffset = stream + 32 + part * 32;
            var text = name.Substring(part * 15, Math.Min(15, name.Length - part * 15));

            Image[nameOffset] = deleted ? (byte)0x41 : (byte)0xC1;
            Encoding.Unicode.GetBytes(text).CopyTo(Image, nameOffset + 2);
        }
    }

    private int Reserve(int directoryCluster, int count)
    {
        slots.TryGetValue(directoryCluster, out var slot);
        slots[directoryCluster] = slot + count;

        return ClusterOffset(directoryCluster) + slot * 32;
    }
}
