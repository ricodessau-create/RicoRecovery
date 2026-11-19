using System.Buffers.Binary;

namespace RicoRecovery.FileSystems.Ext;

internal sealed record ExtInode(
    uint Number,
    ushort Mode,
    long Size,
    uint ModifiedTime,
    uint DeletionTime,
    ushort LinksCount,
    uint Flags,
    byte[] Blocks)
{
    public bool IsDirectory => (Mode & 0xF000) == 0x4000;

    public bool IsRegular => (Mode & 0xF000) == 0x8000;

    public bool UsesExtents => (Flags & 0x80000) != 0;

    public bool HasInlineData => (Flags & 0x10000000) != 0;

    public bool IsDeleted => DeletionTime != 0 && LinksCount == 0;

    public static ExtInode? Parse(uint number, byte[] buffer, int offset)
    {
        if (offset < 0 || offset + 128 > buffer.Length)
        {
            return null;
        }

        var raw = buffer.AsSpan(offset, 128);
        var mode = BinaryPrimitives.ReadUInt16LittleEndian(raw);
        long size = BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]);
        var modified = BinaryPrimitives.ReadUInt32LittleEndian(raw[16..]);
        var deletion = BinaryPrimitives.ReadUInt32LittleEndian(raw[20..]);
        var links = BinaryPrimitives.ReadUInt16LittleEndian(raw[26..]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(raw[32..]);

        if ((mode & 0xF000) == 0x8000)
        {
            size |= (long)BinaryPrimitives.ReadUInt32LittleEndian(raw[108..]) << 32;
        }

        return new ExtInode(number, mode, size, modified, deletion, links, flags, raw.Slice(40, 60).ToArray());
    }
}
