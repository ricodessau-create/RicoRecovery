using System.Buffers.Binary;
using System.Text;

namespace RicoRecovery.FileSystems.Fat;

internal sealed record FatRawEntry(string Name, bool IsDirectory, bool IsDeleted, uint Cluster, long Size, DateTimeOffset? Modified);

internal static class FatDirectoryParser
{
    private const string InvalidShortNameCharacters = "\"*+,./:;<=>?[\\]|";

    public static List<FatRawEntry> Parse(byte[] data, bool isFat32, uint lastCluster)
    {
        var entries = new List<FatRawEntry>();
        var longNameParts = new List<byte[]>();

        for (var offset = 0; offset + 32 <= data.Length; offset += 32)
        {
            var entry = data.AsSpan(offset, 32);
            var first = entry[0];

            if (first == 0x00)
            {
                break;
            }

            var attributes = entry[11];

            if ((attributes & 0x3F) == 0x0F)
            {
                longNameParts.Add(entry.ToArray());
                continue;
            }

            var deleted = first == 0xE5;
            var isDirectory = (attributes & 0x10) != 0;
            var valid = (attributes & 0x08) == 0 && IsValidShortEntry(entry, attributes);
            uint cluster = 0;
            long size = 0;

            if (valid)
            {
                cluster = ReadCluster(entry, isFat32);
                size = BinaryPrimitives.ReadUInt32LittleEndian(entry[28..]);
                valid = (isDirectory || size > 0)
                    ? cluster >= 2 && cluster <= lastCluster
                    : cluster <= lastCluster;
            }

            if (!valid)
            {
                longNameParts.Clear();
                continue;
            }

            var shortName = DecodeShortName(entry, deleted);
            var longName = ResolveLongName(longNameParts, entry, deleted);
            longNameParts.Clear();

            var modified = EntryTimes.FromDos(
                BinaryPrimitives.ReadUInt16LittleEndian(entry[24..]),
                BinaryPrimitives.ReadUInt16LittleEndian(entry[22..]));

            entries.Add(new FatRawEntry(EntryNames.Clean(longName ?? shortName), isDirectory, deleted, cluster, size, modified));
        }

        return entries;
    }

    private static bool IsValidShortEntry(ReadOnlySpan<byte> entry, byte attributes)
    {
        if ((attributes & 0xC0) != 0)
        {
            return false;
        }

        for (var index = 0; index < 11; index++)
        {
            var value = entry[index];

            if (index == 0 && (value == 0x05 || value == 0xE5))
            {
                continue;
            }

            if (value < 0x20 || InvalidShortNameCharacters.Contains((char)value))
            {
                return false;
            }
        }

        return true;
    }

    private static uint ReadCluster(ReadOnlySpan<byte> entry, bool isFat32)
    {
        var low = (uint)BinaryPrimitives.ReadUInt16LittleEndian(entry[26..]);

        if (!isFat32)
        {
            return low;
        }

        var high = (uint)BinaryPrimitives.ReadUInt16LittleEndian(entry[20..]);

        return (high << 16) | low;
    }

    private static string DecodeShortName(ReadOnlySpan<byte> entry, bool deleted)
    {
        var nameCharacters = Encoding.Latin1.GetString(entry[..8]).ToCharArray();

        if (deleted)
        {
            nameCharacters[0] = '_';
        }
        else if (entry[0] == 0x05)
        {
            nameCharacters[0] = '\u00E5';
        }

        var name = new string(nameCharacters).TrimEnd(' ');
        var extension = Encoding.Latin1.GetString(entry.Slice(8, 3)).TrimEnd(' ');

        if ((entry[12] & 0x08) != 0)
        {
            name = name.ToLowerInvariant();
        }

        if ((entry[12] & 0x10) != 0)
        {
            extension = extension.ToLowerInvariant();
        }

        return extension.Length == 0 ? name : name + "." + extension;
    }

    private static string? ResolveLongName(List<byte[]> parts, ReadOnlySpan<byte> shortEntry, bool deleted)
    {
        if (parts.Count == 0)
        {
            return null;
        }

        var checksum = parts[0][13];

        foreach (var part in parts)
        {
            if (part[13] != checksum)
            {
                return null;
            }
        }

        if (!deleted && checksum != ComputeChecksum(shortEntry))
        {
            return null;
        }

        var builder = new StringBuilder();

        for (var index = parts.Count - 1; index >= 0; index--)
        {
            AppendCharacters(builder, parts[index], 1, 5);
            AppendCharacters(builder, parts[index], 14, 6);
            AppendCharacters(builder, parts[index], 28, 2);
        }

        var text = builder.ToString();
        var end = text.IndexOf('\0');

        if (end >= 0)
        {
            text = text[..end];
        }

        return text.Length == 0 ? null : text;
    }

    private static void AppendCharacters(StringBuilder builder, byte[] part, int start, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var position = start + index * 2;
            builder.Append((char)(part[position] | (part[position + 1] << 8)));
        }
    }

    private static byte ComputeChecksum(ReadOnlySpan<byte> entry)
    {
        byte sum = 0;

        for (var index = 0; index < 11; index++)
        {
            sum = (byte)((((sum & 1) != 0 ? 0x80 : 0) + (sum >> 1) + entry[index]) & 0xFF);
        }

        return sum;
    }
}
