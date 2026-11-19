using System.Buffers.Binary;
using System.Text;

namespace RicoRecovery.FileSystems.Ext;

internal sealed record ExtDirectoryEntry(uint Inode, string Name, byte FileType, bool IsSlack);

internal static class ExtDirectoryParser
{
    public static List<ExtDirectoryEntry> Parse(byte[] data, int blockSize, uint inodesCount, bool includeSlack)
    {
        var entries = new List<ExtDirectoryEntry>();

        for (var blockStart = 0; blockStart + 12 <= data.Length; blockStart += blockSize)
        {
            var blockEnd = Math.Min(blockStart + blockSize, data.Length);
            var offset = blockStart;

            while (offset + 8 <= blockEnd)
            {
                var inode = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
                int recordLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4));
                int nameLength = data[offset + 6];
                var fileType = data[offset + 7];

                if (recordLength < 12 || recordLength % 4 != 0 || offset + recordLength > blockEnd)
                {
                    break;
                }

                if (inode != 0 && nameLength > 0 && 8 + nameLength <= recordLength)
                {
                    if (TryReadName(data, offset + 8, nameLength, out var name))
                    {
                        entries.Add(new ExtDirectoryEntry(inode, name, fileType, false));
                    }

                    var used = 8 + ((nameLength + 3) & ~3);

                    if (includeSlack && recordLength - used >= 12)
                    {
                        ParseSlack(data, offset + used, offset + recordLength, inodesCount, entries);
                    }
                }

                offset += recordLength;
            }
        }

        return entries;
    }

    private static void ParseSlack(byte[] data, int start, int end, uint inodesCount, List<ExtDirectoryEntry> entries)
    {
        var offset = start;

        while (offset + 12 <= end)
        {
            var inode = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            int recordLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4));
            int nameLength = data[offset + 6];
            var fileType = data[offset + 7];

            if (inode == 0 || inode > inodesCount || recordLength < 12 || recordLength % 4 != 0)
            {
                break;
            }

            if (offset + recordLength > end || nameLength == 0 || 8 + nameLength > recordLength || fileType > 7)
            {
                break;
            }

            if (!TryReadName(data, offset + 8, nameLength, out var name))
            {
                break;
            }

            entries.Add(new ExtDirectoryEntry(inode, name, fileType, true));
            offset += 8 + ((nameLength + 3) & ~3);
        }
    }

    private static bool TryReadName(byte[] data, int offset, int length, out string name)
    {
        name = string.Empty;

        for (var index = 0; index < length; index++)
        {
            var value = data[offset + index];

            if (value < 0x20 || value == 0x2F || value == 0x7F)
            {
                return false;
            }
        }

        var text = Encoding.UTF8.GetString(data, offset, length);

        if (text is "." or "..")
        {
            return false;
        }

        name = EntryNames.Clean(text);

        return true;
    }
}
