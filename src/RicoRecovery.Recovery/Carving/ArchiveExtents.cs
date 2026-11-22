using System.Buffers.Binary;
using System.Text;
using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;
using RicoRecovery.Storage.Partitioning;

namespace RicoRecovery.Recovery.Carving;

internal static class ArchiveExtents
{
    public static async Task<ExtentResult?> ZipAsync(IBlockSource source, long start, FileSignature signature, CancellationToken cancellationToken)
    {
        var limit = ExtentHelpers.Limit(source, start, signature);
        var pattern = new byte[] { 0x50, 0x4B, 0x05, 0x06 };
        var searchFrom = start + 30;
        long fallbackEnd = -1;

        while (true)
        {
            var found = await ExtentHelpers.FindAsync(source, searchFrom, limit, pattern, cancellationToken).ConfigureAwait(false);

            if (found < 0)
            {
                break;
            }

            var record = await SourceReader.ReadAsync(source, found, 22, cancellationToken).ConfigureAwait(false);

            if (record is null)
            {
                break;
            }

            var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(12));
            var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(16));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
            var end = found + 22 + commentLength;

            if (end <= limit)
            {
                if (directoryOffset + (long)directorySize == found - start)
                {
                    var extension = await DetectZipExtensionAsync(source, start, directoryOffset, directorySize, cancellationToken).ConfigureAwait(false);

                    return new ExtentResult(end - start, IntegrityStatus.Valid, null, extension);
                }

                if (fallbackEnd < 0)
                {
                    fallbackEnd = end;
                }
            }

            searchFrom = found + 4;
        }

        return fallbackEnd < 0
            ? null
            : new ExtentResult(fallbackEnd - start, IntegrityStatus.Plausible, "Das Zentralverzeichnis ist nicht konsistent (ZIP64 oder beschädigt).");
    }

    public static async Task<ExtentResult?> SevenZipAsync(IBlockSource source, long start, FileSignature signature, CancellationToken cancellationToken)
    {
        var header = await SourceReader.ReadAsync(source, start, 32, cancellationToken).ConfigureAwait(false);

        if (header is null || Crc32.Compute(header.AsSpan(12, 20)) != BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)))
        {
            return null;
        }

        var nextOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(12));
        var nextSize = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(20));

        if (nextOffset < 0 || nextSize < 0 || nextSize > 64 * 1024 * 1024 || nextOffset > signature.MaxSize)
        {
            return null;
        }

        var length = 32 + nextOffset + nextSize;
        var available = source.Length - start;

        if (length > available)
        {
            return ExtentHelpers.Truncated(available, "Die Datei ist kürzer als im Header angegeben.");
        }

        var nextHeader = await SourceReader.ReadAsync(source, start + 32 + nextOffset, (int)nextSize, cancellationToken).ConfigureAwait(false);
        var valid = nextHeader is not null && Crc32.Compute(nextHeader) == BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28));

        return valid
            ? new ExtentResult(length, IntegrityStatus.Valid, null)
            : ExtentHelpers.Damaged(length, "Die Prüfsumme des Endheaders stimmt nicht.");
    }

    public static async Task<ExtentResult?> RarAsync(IBlockSource source, long start, FileSignature signature, CancellationToken cancellationToken)
    {
        var limit = ExtentHelpers.Limit(source, start, signature);
        var marker = await SourceReader.ReadAsync(source, start, 8, cancellationToken).ConfigureAwait(false);

        if (marker is null)
        {
            return null;
        }

        if (marker[6] == 0x00)
        {
            return await Rar4Async(source, start, limit, cancellationToken).ConfigureAwait(false);
        }

        return marker[6] == 0x01 && marker[7] == 0x00
            ? await Rar5Async(source, start, limit, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public static async Task<ExtentResult?> TarAsync(IBlockSource source, long start, FileSignature signature, CancellationToken cancellationToken)
    {
        var limit = ExtentHelpers.Limit(source, start, signature);
        var position = start;
        var entries = 0;

        while (position + 512 <= limit)
        {
            var block = await SourceReader.ReadAsync(source, position, 512, cancellationToken).ConfigureAwait(false);

            if (block is null)
            {
                break;
            }

            if (block.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            {
                if (entries == 0)
                {
                    return null;
                }

                var end = position + 512;
                var second = await SourceReader.ReadAsync(source, end, 512, cancellationToken).ConfigureAwait(false);

                if (second is not null && second.AsSpan().IndexOfAnyExcept((byte)0) < 0)
                {
                    end += 512;
                }

                return new ExtentResult(end - start, IntegrityStatus.Valid, null);
            }

            var size = HasValidChecksum(block) ? ReadTarSize(block) : -1;

            if (size < 0)
            {
                return entries == 0 ? null : ExtentHelpers.Damaged(position - start, "Ungültiger TAR-Header.");
            }

            position += 512 + (size + 511) / 512 * 512;
            entries++;
        }

        return entries == 0 ? null : ExtentHelpers.Truncated(Math.Min(position, limit) - start, "Das Archivende wurde nicht gefunden.");
    }

    private static async Task<string?> DetectZipExtensionAsync(IBlockSource source, long start, long directoryOffset, uint directorySize, CancellationToken cancellationToken)
    {
        if (directorySize == 0 || directorySize > 4 * 1024 * 1024)
        {
            return null;
        }

        var data = await SourceReader.ReadAsync(source, start + directoryOffset, (int)directorySize, cancellationToken).ConfigureAwait(false);

        if (data is null)
        {
            return null;
        }

        if (data.AsSpan().IndexOf("AndroidManifest.xml"u8) >= 0)
        {
            return "apk";
        }

        if (data.AsSpan().IndexOf("word/document.xml"u8) >= 0)
        {
            return "docx";
        }

        if (data.AsSpan().IndexOf("xl/workbook.xml"u8) >= 0)
        {
            return "xlsx";
        }

        return data.AsSpan().IndexOf("ppt/presentation.xml"u8) >= 0 ? "pptx" : null;
    }

    private static async Task<ExtentResult?> Rar4Async(IBlockSource source, long start, long limit, CancellationToken cancellationToken)
    {
        var position = start + 7;
        var first = true;

        for (var block = 0; block < 1_000_000; block++)
        {
            var header = position + 7 <= limit
                ? await SourceReader.ReadAsync(source, position, 7, cancellationToken).ConfigureAwait(false)
                : null;

            if (header is null)
            {
                return first ? null : ExtentHelpers.Truncated(position - start, "Das Archivende wurde nicht gefunden.");
            }

            var type = header[2];
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(3));
            var size = (long)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(5));

            if (first && type != 0x73)
            {
                return null;
            }

            if (type < 0x72 || type > 0x7B || size < 7)
            {
                return first ? null : ExtentHelpers.Damaged(position - start, "Ungültiger RAR-Block.");
            }

            long addSize = 0;

            if ((flags & 0x8000) != 0)
            {
                var extra = await SourceReader.ReadAsync(source, position + 7, 4, cancellationToken).ConfigureAwait(false);

                if (extra is null)
                {
                    return ExtentHelpers.Truncated(position - start, "Der Blockheader ist unvollständig.");
                }

                addSize = BinaryPrimitives.ReadUInt32LittleEndian(extra);
            }

            first = false;
            position += size + addSize;

            if (position > limit)
            {
                return ExtentHelpers.Truncated(limit - start, "Das Archivende wurde nicht gefunden.");
            }

            if (type == 0x7B)
            {
                return new ExtentResult(position - start, IntegrityStatus.Valid, null);
            }
        }

        return null;
    }

    private static async Task<ExtentResult?> Rar5Async(IBlockSource source, long start, long limit, CancellationToken cancellationToken)
    {
        var position = start + 8;
        var first = true;

        for (var block = 0; block < 1_000_000; block++)
        {
            var head = await SourceReader.ReadAvailableAsync(source, position, 16, cancellationToken).ConfigureAwait(false);
            var index = 4;

            if (head.Length < 6 || !TryReadVarint(head, ref index, out var headerSize) || headerSize < 2 || headerSize > 1 << 20)
            {
                return first ? null : ExtentHelpers.Damaged(position - start, "Ungültiger RAR-Block.");
            }

            var content = await SourceReader.ReadAsync(source, position + index, (int)headerSize, cancellationToken).ConfigureAwait(false);

            if (content is null)
            {
                return first ? null : ExtentHelpers.Truncated(position - start, "Der Blockheader ist unvollständig.");
            }

            var contentIndex = 0;

            if (!TryReadVarint(content, ref contentIndex, out var type) || !TryReadVarint(content, ref contentIndex, out var flags))
            {
                return first ? null : ExtentHelpers.Damaged(position - start, "Ungültiger RAR-Block.");
            }

            if ((flags & 1) != 0 && !TryReadVarint(content, ref contentIndex, out _))
            {
                return first ? null : ExtentHelpers.Damaged(position - start, "Ungültiger RAR-Block.");
            }

            long dataSize = 0;

            if ((flags & 2) != 0 && !TryReadVarint(content, ref contentIndex, out dataSize))
            {
                return first ? null : ExtentHelpers.Damaged(position - start, "Ungültiger RAR-Block.");
            }

            if (first && type != 1)
            {
                return null;
            }

            first = false;
            position += index + headerSize + dataSize;

            if (position > limit)
            {
                return ExtentHelpers.Truncated(limit - start, "Das Archivende wurde nicht gefunden.");
            }

            if (type == 5)
            {
                return new ExtentResult(position - start, IntegrityStatus.Valid, null);
            }
        }

        return null;
    }

    private static bool TryReadVarint(byte[] data, ref int index, out long value)
    {
        value = 0;
        var shift = 0;

        while (index < data.Length && shift < 63)
        {
            var current = data[index++];
            value |= (long)(current & 0x7F) << shift;

            if ((current & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }

        return false;
    }

    private static bool HasValidChecksum(byte[] block)
    {
        var sum = 0;

        for (var index = 0; index < 512; index++)
        {
            sum += index is >= 148 and < 156 ? 0x20 : block[index];
        }

        var text = Encoding.ASCII.GetString(block, 148, 8).Trim('\0', ' ');

        if (text.Length == 0)
        {
            return false;
        }

        var stored = 0;

        foreach (var character in text)
        {
            if (character < '0' || character > '7')
            {
                return false;
            }

            stored = stored * 8 + (character - '0');
        }

        return stored == sum;
    }

    private static long ReadTarSize(byte[] block)
    {
        if ((block[124] & 0x80) != 0)
        {
            return BinaryPrimitives.ReadInt64BigEndian(block.AsSpan(128));
        }

        var text = Encoding.ASCII.GetString(block, 124, 12).Trim('\0', ' ');
        long value = 0;

        foreach (var character in text)
        {
            if (character < '0' || character > '7')
            {
                return -1;
            }

            value = value * 8 + (character - '0');
        }

        return value;
    }
}
