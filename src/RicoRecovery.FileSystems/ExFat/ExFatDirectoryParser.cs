using System.Buffers.Binary;
using System.Text;

namespace RicoRecovery.FileSystems.ExFat;

internal sealed record ExFatRawEntry(
    string Name,
    bool IsDirectory,
    bool IsDeleted,
    uint Cluster,
    long Length,
    bool NoFatChain,
    DateTimeOffset? Modified);

internal static class ExFatDirectoryParser
{
    public static List<ExFatRawEntry> Parse(byte[] data, uint lastCluster, long maxLength)
    {
        var entries = new List<ExFatRawEntry>();
        var count = data.Length / 32;
        var index = 0;

        while (index < count)
        {
            var offset = index * 32;
            var type = data[offset];

            if (type == 0x00)
            {
                break;
            }

            if (type != 0x85 && type != 0x05)
            {
                index++;
                continue;
            }

            var secondaryCount = (int)data[offset + 1];

            if (secondaryCount < 2 || index + secondaryCount >= count)
            {
                index++;
                continue;
            }

            var parsed = ParseSet(data, offset, secondaryCount, type == 0x05, lastCluster, maxLength);

            if (parsed is null)
            {
                index++;
                continue;
            }

            entries.Add(parsed);
            index += secondaryCount + 1;
        }

        return entries;
    }

    private static ExFatRawEntry? ParseSet(byte[] data, int offset, int secondaryCount, bool deleted, uint lastCluster, long maxLength)
    {
        var streamType = deleted ? 0x40 : 0xC0;
        var nameType = deleted ? 0x41 : 0xC1;
        var streamOffset = offset + 32;

        if (data[streamOffset] != streamType)
        {
            return null;
        }

        var nameLength = (int)data[streamOffset + 3];
        var nameEntries = (nameLength + 14) / 15;

        if (nameLength == 0 || nameEntries > secondaryCount - 1)
        {
            return null;
        }

        var builder = new StringBuilder(nameLength);

        for (var part = 0; part < nameEntries; part++)
        {
            var nameOffset = streamOffset + 32 + part * 32;

            if (data[nameOffset] != nameType)
            {
                return null;
            }

            var characters = Math.Min(15, nameLength - part * 15);
            builder.Append(Encoding.Unicode.GetString(data, nameOffset + 2, characters * 2));
        }

        var flags = data[streamOffset + 1];
        var firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(streamOffset + 20));
        var rawLength = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(streamOffset + 24));

        if (rawLength > (ulong)maxLength)
        {
            return null;
        }

        var length = (long)rawLength;

        if (length > 0 && (firstCluster < 2 || firstCluster > lastCluster))
        {
            return null;
        }

        var attributes = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4));
        var modifiedTime = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 12));
        var modifiedOffset = data[offset + 23];

        return new ExFatRawEntry(
            EntryNames.Clean(builder.ToString()),
            (attributes & 0x10) != 0,
            deleted,
            firstCluster,
            length,
            (flags & 0x02) != 0,
            EntryTimes.FromExFat(modifiedTime, modifiedOffset));
    }
}
