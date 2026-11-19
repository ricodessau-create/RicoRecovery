using System.Text;

namespace RicoRecovery.Tests.TestSupport;

internal sealed class NtfsImageBuilder
{
    public const int ClusterSize = 512;
    public const int RecordSize = 1024;
    public const int MftCluster = 8;
    public const int MirrorCluster = 200;
    public const int BitmapCluster = 100;
    public const int TotalClusters = 4096;
    public const int RecordCount = 32;

    private readonly byte[] image = new byte[TotalClusters * ClusterSize];
    private readonly byte[] bitmap = new byte[TotalClusters / 8];
    private int nextAttributeId;

    public NtfsImageBuilder()
    {
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(image, 3);
        ImageWriter.U16(image, 11, ClusterSize);
        image[13] = 1;
        ImageWriter.U64(image, 40, TotalClusters);
        ImageWriter.U64(image, 48, MftCluster);
        ImageWriter.U64(image, 56, MirrorCluster);
        image[64] = 0xF6;
        image[510] = 0x55;
        image[511] = 0xAA;

        MarkAllocated(0, 72);
        MarkAllocated(BitmapCluster, 1);
        MarkAllocated(MirrorCluster, 1);

        WriteRecord(0, 0x01, [NonResident(0x80, Run(MftCluster, 64), RecordCount * RecordSize)]);
        WriteRecord(5, 0x03, []);
        WriteRecord(6, 0x01, [NonResident(0x80, Run(BitmapCluster, 1), TotalClusters / 8)]);
    }

    public static byte[] ResidentContent { get; } = "Hallo NTFS"u8.ToArray();

    public static byte[] BigContent { get; } = ScanHelper.Pattern(700);

    public static byte[] OldContent { get; } = ScanHelper.Pattern(400);

    public static byte[] CreateSample()
    {
        var builder = new NtfsImageBuilder();

        builder.AddResidentFile(24, "doc.txt", 5, ResidentContent);
        builder.AddFile(25, "big.bin", 5, BigContent, 300, false, true);
        builder.AddFile(26, "old.bin", 5, OldContent, 310, true, false);
        builder.AddDirectory(27, "Ordner", 5);
        builder.AddResidentFile(28, "inner.txt", 27, "xyz"u8.ToArray());
        builder.AddFile(29, "reused.bin", 5, OldContent, 320, true, true);

        return builder.Build();
    }

    public byte[] Build()
    {
        bitmap.CopyTo(image, BitmapCluster * ClusterSize);
        Array.Copy(image, MftCluster * ClusterSize, image, MirrorCluster * ClusterSize, RecordSize);

        return image;
    }

    public void AddResidentFile(int record, string name, int parent, byte[] data) =>
        WriteRecord(record, 0x01, [Resident(0x30, FileNameValue(name, parent, data.Length, false)), Resident(0x80, data)]);

    public void AddFile(int record, string name, int parent, byte[] data, int lcn, bool deleted, bool allocatedInBitmap)
    {
        var clusters = (data.Length + ClusterSize - 1) / ClusterSize;

        data.CopyTo(image, lcn * ClusterSize);

        if (allocatedInBitmap)
        {
            MarkAllocated(lcn, clusters);
        }

        WriteRecord(
            record,
            deleted ? (ushort)0 : (ushort)1,
            [Resident(0x30, FileNameValue(name, parent, data.Length, false)), NonResident(0x80, Run(lcn, clusters), data.Length)]);
    }

    public void AddDirectory(int record, string name, int parent) =>
        WriteRecord(record, 0x03, [Resident(0x30, FileNameValue(name, parent, 0, true))]);

    private static byte[] Run(long lcn, long length) =>
        [0x41, (byte)length, (byte)lcn, (byte)(lcn >> 8), (byte)(lcn >> 16), (byte)(lcn >> 24), 0x00];

    private static byte[] FileNameValue(string name, int parent, long size, bool directory)
    {
        var value = new byte[66 + name.Length * 2];
        var time = new DateTimeOffset(2024, 5, 17, 12, 30, 10, TimeSpan.Zero).ToFileTime();

        ImageWriter.U64(value, 0, parent | (1L << 48));
        ImageWriter.U64(value, 8, time);
        ImageWriter.U64(value, 16, time);
        ImageWriter.U64(value, 24, time);
        ImageWriter.U64(value, 32, time);
        ImageWriter.U64(value, 40, size);
        ImageWriter.U64(value, 48, size);
        ImageWriter.U32(value, 56, directory ? 0x10000000 : 0);
        value[64] = (byte)name.Length;
        value[65] = 1;
        Encoding.Unicode.GetBytes(name).CopyTo(value, 66);

        return value;
    }

    private void MarkAllocated(int firstCluster, int count)
    {
        for (var cluster = firstCluster; cluster < firstCluster + count; cluster++)
        {
            bitmap[cluster >> 3] |= (byte)(1 << (cluster & 7));
        }
    }

    private byte[] Resident(uint type, byte[] value)
    {
        var length = (24 + value.Length + 7) & ~7;
        var attribute = new byte[length];

        ImageWriter.U32(attribute, 0, type);
        ImageWriter.U32(attribute, 4, length);
        ImageWriter.U16(attribute, 14, nextAttributeId++);
        ImageWriter.U32(attribute, 16, value.Length);
        ImageWriter.U16(attribute, 20, 24);
        value.CopyTo(attribute, 24);

        return attribute;
    }

    private byte[] NonResident(uint type, byte[] runs, long realSize)
    {
        var length = (64 + runs.Length + 7) & ~7;
        var attribute = new byte[length];
        var allocated = (realSize + ClusterSize - 1) / ClusterSize * ClusterSize;

        ImageWriter.U32(attribute, 0, type);
        ImageWriter.U32(attribute, 4, length);
        attribute[8] = 1;
        ImageWriter.U16(attribute, 14, nextAttributeId++);
        ImageWriter.U64(attribute, 24, allocated / ClusterSize - 1);
        ImageWriter.U16(attribute, 32, 64);
        ImageWriter.U64(attribute, 40, allocated);
        ImageWriter.U64(attribute, 48, realSize);
        ImageWriter.U64(attribute, 56, realSize);
        runs.CopyTo(attribute, 64);

        return attribute;
    }

    private void WriteRecord(int number, ushort flags, IReadOnlyList<byte[]> attributes)
    {
        var record = new byte[RecordSize];

        Encoding.ASCII.GetBytes("FILE").CopyTo(record, 0);
        ImageWriter.U16(record, 4, 48);
        ImageWriter.U16(record, 6, 3);
        ImageWriter.U16(record, 16, 1);
        ImageWriter.U16(record, 18, 1);
        ImageWriter.U16(record, 20, 56);
        ImageWriter.U16(record, 22, flags);
        ImageWriter.U32(record, 28, RecordSize);
        ImageWriter.U32(record, 44, number);

        var position = 56;

        foreach (var attribute in attributes)
        {
            attribute.CopyTo(record, position);
            position += attribute.Length;
        }

        ImageWriter.U32(record, position, 0xFFFFFFFF);
        ImageWriter.U32(record, 24, position + 8);

        const int updateSequence = 0x0001;

        ImageWriter.U16(record, 48, updateSequence);

        for (var sector = 1; sector <= 2; sector++)
        {
            var end = sector * 512 - 2;

            record[48 + sector * 2] = record[end];
            record[48 + sector * 2 + 1] = record[end + 1];
            ImageWriter.U16(record, end, updateSequence);
        }

        record.CopyTo(image, MftCluster * ClusterSize + number * RecordSize);
    }
}
