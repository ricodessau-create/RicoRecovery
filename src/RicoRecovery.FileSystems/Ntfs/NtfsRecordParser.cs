using System.Buffers.Binary;
using System.Text;
using RicoRecovery.Core.Models;

namespace RicoRecovery.FileSystems.Ntfs;

internal sealed record NtfsAttribute(
    uint Type,
    string Name,
    ushort Flags,
    bool NonResident,
    byte[]? Value,
    long StartVcn,
    long RealSize,
    ushort CompressionUnit,
    IReadOnlyList<DataRun> Runs);

internal sealed record NtfsRecord(
    long Number,
    bool InUse,
    bool IsDirectory,
    long BaseRecord,
    IReadOnlyList<NtfsAttribute> Attributes);

internal sealed class NtfsRecordParser(long partitionOffset, int clusterBytes, int recordSize)
{
    public NtfsRecord? Parse(byte[] buffer, int offset, long number)
    {
        if (offset < 0 || offset + recordSize > buffer.Length)
        {
            return null;
        }

        var record = buffer.AsSpan(offset, recordSize);

        if (!record[..4].SequenceEqual("FILE"u8))
        {
            return null;
        }

        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);

        if (usaCount < 2 || usaCount - 1 != recordSize / 512 || usaOffset + usaCount * 2 > recordSize)
        {
            return null;
        }

        var updateSequence = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);

        for (var index = 1; index < usaCount; index++)
        {
            var position = index * 512 - 2;

            if (BinaryPrimitives.ReadUInt16LittleEndian(record[position..]) != updateSequence)
            {
                return null;
            }

            record[position] = record[usaOffset + index * 2];
            record[position + 1] = record[usaOffset + index * 2 + 1];
        }

        int firstAttribute = BinaryPrimitives.ReadUInt16LittleEndian(record[20..]);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(record[22..]);
        var usedBytes = BinaryPrimitives.ReadUInt32LittleEndian(record[24..]);
        var baseRecord = (long)(BinaryPrimitives.ReadUInt64LittleEndian(record[32..]) & 0x0000FFFFFFFFFFFF);

        if (firstAttribute < 42 || firstAttribute >= recordSize || usedBytes > recordSize)
        {
            return null;
        }

        var attributes = new List<NtfsAttribute>();
        var position2 = firstAttribute;

        while (position2 + 8 <= recordSize)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(record[position2..]);

            if (type == 0xFFFFFFFF)
            {
                break;
            }

            var length = BinaryPrimitives.ReadUInt32LittleEndian(record[(position2 + 4)..]);

            if (length < 24 || length > recordSize - position2)
            {
                break;
            }

            var attribute = ParseAttribute(record.Slice(position2, (int)length));

            if (attribute is not null)
            {
                attributes.Add(attribute);
            }

            position2 += (int)length;
        }

        return new NtfsRecord(number, (flags & 1) != 0, (flags & 2) != 0, baseRecord, attributes);
    }

    private NtfsAttribute? ParseAttribute(ReadOnlySpan<byte> slice)
    {
        var type = BinaryPrimitives.ReadUInt32LittleEndian(slice);
        var nonResident = slice[8] != 0;
        int nameLength = slice[9];
        int nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(slice[10..]);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(slice[12..]);
        var name = string.Empty;

        if (nameLength > 0)
        {
            if (nameOffset + nameLength * 2 > slice.Length)
            {
                return null;
            }

            name = Encoding.Unicode.GetString(slice.Slice(nameOffset, nameLength * 2));
        }

        if (!nonResident)
        {
            var valueLength = BinaryPrimitives.ReadUInt32LittleEndian(slice[16..]);
            int valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(slice[20..]);

            if (valueLength > slice.Length || valueOffset + valueLength > slice.Length)
            {
                return null;
            }

            return new NtfsAttribute(
                type,
                name,
                flags,
                false,
                slice.Slice(valueOffset, (int)valueLength).ToArray(),
                0,
                valueLength,
                0,
                []);
        }

        if (slice.Length < 64)
        {
            return null;
        }

        var startVcn = BinaryPrimitives.ReadInt64LittleEndian(slice[16..]);
        int runsOffset = BinaryPrimitives.ReadUInt16LittleEndian(slice[32..]);
        var compressionUnit = BinaryPrimitives.ReadUInt16LittleEndian(slice[34..]);
        var realSize = BinaryPrimitives.ReadInt64LittleEndian(slice[48..]);

        if (runsOffset >= slice.Length || startVcn < 0 || realSize < 0)
        {
            return null;
        }

        var runs = DecodeRuns(slice[runsOffset..]);

        return runs is null
            ? null
            : new NtfsAttribute(type, name, flags, true, null, startVcn, realSize, compressionUnit, runs);
    }

    private List<DataRun>? DecodeRuns(ReadOnlySpan<byte> data)
    {
        var runs = new List<DataRun>();
        var position = 0;
        long lcn = 0;

        while (position < data.Length)
        {
            var header = data[position++];

            if (header == 0)
            {
                break;
            }

            var lengthSize = header & 0x0F;
            var offsetSize = header >> 4;

            if (lengthSize == 0 || lengthSize > 8 || offsetSize > 8 || position + lengthSize + offsetSize > data.Length)
            {
                return null;
            }

            long clusters = 0;

            for (var index = 0; index < lengthSize; index++)
            {
                clusters |= (long)data[position + index] << (8 * index);
            }

            position += lengthSize;

            if (clusters <= 0 || clusters > long.MaxValue / clusterBytes)
            {
                return null;
            }

            if (offsetSize == 0)
            {
                runs.Add(new DataRun(0, clusters * clusterBytes, true));
                continue;
            }

            long delta = 0;

            for (var index = 0; index < offsetSize; index++)
            {
                delta |= (long)data[position + index] << (8 * index);
            }

            if (offsetSize < 8 && (data[position + offsetSize - 1] & 0x80) != 0)
            {
                delta |= -1L << (8 * offsetSize);
            }

            position += offsetSize;
            lcn += delta;

            if (lcn < 0 || lcn > long.MaxValue / clusterBytes - 1)
            {
                return null;
            }

            runs.Add(new DataRun(partitionOffset + lcn * clusterBytes, clusters * clusterBytes, false));
        }

        return runs;
    }
}
