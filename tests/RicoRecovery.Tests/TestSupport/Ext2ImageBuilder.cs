using System.Text;

namespace RicoRecovery.Tests.TestSupport;

internal sealed class Ext2ImageBuilder
{
    public const int BlockSize = 1024;
    public const int TotalBlocks = 64;
    public const int InodeTableBlock = 5;
    public const int InodeSize = 128;
    public const uint ModifiedTime = 1715949010;

    public Ext2ImageBuilder()
    {
        Image = new byte[TotalBlocks * BlockSize];

        ImageWriter.U32(Image, 1024 + 0, 32);
        ImageWriter.U32(Image, 1024 + 4, TotalBlocks);
        ImageWriter.U32(Image, 1024 + 20, 1);
        ImageWriter.U32(Image, 1024 + 24, 0);
        ImageWriter.U32(Image, 1024 + 32, 8192);
        ImageWriter.U32(Image, 1024 + 40, 32);
        ImageWriter.U16(Image, 1024 + 56, 0xEF53);
        ImageWriter.U32(Image, 1024 + 76, 1);
        ImageWriter.U32(Image, 1024 + 84, 11);
        ImageWriter.U16(Image, 1024 + 88, InodeSize);
        ImageWriter.U32(Image, 1024 + 96, 0x2);
        Encoding.ASCII.GetBytes("EXTTEST").CopyTo(Image, 1024 + 120);

        ImageWriter.U32(Image, 2048 + 0, 3);
        ImageWriter.U32(Image, 2048 + 4, 4);
        ImageWriter.U32(Image, 2048 + 8, InodeTableBlock);

        Image[3 * BlockSize] = 0xFF;
        Image[3 * BlockSize + 1] = 0x0F;
    }

    public static byte[] HelloContent { get; } = Encoding.ASCII.GetBytes("Hallo ext2!");

    public static byte[] GoneContent { get; } = ScanHelper.Pattern(1500);

    public byte[] Image { get; }

    public static Ext2ImageBuilder CreateSample()
    {
        var builder = new Ext2ImageBuilder();

        builder.WriteInode(2, 0x41ED, 1024, 2, 0, 9);
        builder.WriteInode(12, 0x81A4, HelloContent.Length, 1, 0, 11);
        builder.WriteInode(13, 0x81A4, GoneContent.Length, 0, 1715949999, 13, 14);
        builder.WriteInode(14, 0x41ED, 1024, 2, 0, 10);
        builder.WriteInode(15, 0x81A4, 3, 1, 0, 12);

        var root = 9 * BlockSize;

        builder.WriteEntry(root + 0, 2, 12, ".", 2);
        builder.WriteEntry(root + 12, 2, 12, "..", 2);
        builder.WriteEntry(root + 24, 12, 20, "hello.txt", 1);
        builder.WriteEntry(root + 44, 14, 980, "sub", 2);
        builder.WriteEntry(root + 56, 13, 16, "gone.txt", 1);

        var sub = 10 * BlockSize;

        builder.WriteEntry(sub + 0, 14, 12, ".", 2);
        builder.WriteEntry(sub + 12, 2, 12, "..", 2);
        builder.WriteEntry(sub + 24, 15, 1000, "in.txt", 1);

        HelloContent.CopyTo(builder.Image, 11 * BlockSize);
        Encoding.ASCII.GetBytes("abc").CopyTo(builder.Image, 12 * BlockSize);
        GoneContent.CopyTo(builder.Image, 13 * BlockSize);

        return builder;
    }

    public void WriteInode(int number, int mode, long size, int links, long deletionTime, params int[] blocks)
    {
        var offset = InodeTableBlock * BlockSize + (number - 1) * InodeSize;

        ImageWriter.U16(Image, offset, mode);
        ImageWriter.U32(Image, offset + 4, size);
        ImageWriter.U32(Image, offset + 16, ModifiedTime);
        ImageWriter.U32(Image, offset + 20, deletionTime);
        ImageWriter.U16(Image, offset + 26, links);

        for (var index = 0; index < blocks.Length; index++)
        {
            ImageWriter.U32(Image, offset + 40 + index * 4, blocks[index]);
        }
    }

    public void WriteEntry(int offset, int inode, int recordLength, string name, int fileType)
    {
        ImageWriter.U32(Image, offset, inode);
        ImageWriter.U16(Image, offset + 4, recordLength);
        Image[offset + 6] = (byte)name.Length;
        Image[offset + 7] = (byte)fileType;
        Encoding.UTF8.GetBytes(name).CopyTo(Image, offset + 8);
    }
}
