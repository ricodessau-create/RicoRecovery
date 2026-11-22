using System.Buffers.Binary;
using System.Text;
using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;

namespace RicoRecovery.Recovery.Carving;

internal static class DocumentExtents
{
    public static async Task<ExtentResult?> OleAsync(IBlockSource source, long start, FileSignature signature, CancellationToken cancellationToken)
    {
        var header = await SourceReader.ReadAsync(source, start, 512, cancellationToken).ConfigureAwait(false);

        if (header is null)
        {
            return null;
        }

        var major = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26));
        var byteOrder = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
        var shift = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30));

        if (byteOrder != 0xFFFE || (major != 3 || shift != 9) && (major != 4 || shift != 12))
        {
            return null;
        }

        var sectorSize = 1 << shift;
        var fatCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(44));
        var firstDirectory = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(48));
        var difatStart = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(68));
        var difatCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(72));

        if (fatCount == 0 || fatCount > 100_000)
        {
            return null;
        }

        var fatSectors = new List<uint>();

        for (var index = 0; index < 109 && fatSectors.Count < fatCount; index++)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(76 + index * 4));

            if (value == 0xFFFFFFFF)
            {
                break;
            }

            fatSectors.Add(value);
        }

        var difat = difatStart;
        var guard = 0u;

        while (fatSectors.Count < fatCount && difat < 0xFFFFFFFA && guard++ < difatCount)
        {
            var difatSector = await SourceReader.ReadAsync(source, start + ((long)difat + 1) * sectorSize, sectorSize, cancellationToken).ConfigureAwait(false);

            if (difatSector is null)
            {
                break;
            }

            for (var index = 0; index < sectorSize / 4 - 1 && fatSectors.Count < fatCount; index++)
            {
                var value = BinaryPrimitives.ReadUInt32LittleEndian(difatSector.AsSpan(index * 4));

                if (value == 0xFFFFFFFF)
                {
                    break;
                }

                fatSectors.Add(value);
            }

            difat = BinaryPrimitives.ReadUInt32LittleEndian(difatSector.AsSpan(sectorSize - 4));
        }

        var entriesPerSector = sectorSize / 4;
        long maxSector = -1;

        for (var index = 0; index < fatSectors.Count; index++)
        {
            var fatSector = await SourceReader.ReadAsync(source, start + ((long)fatSectors[index] + 1) * sectorSize, sectorSize, cancellationToken).ConfigureAwait(false);

            if (fatSector is null)
            {
                break;
            }

            for (var entry = entriesPerSector - 1; entry >= 0; entry--)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(fatSector.AsSpan(entry * 4)) != 0xFFFFFFFF)
                {
                    maxSector = Math.Max(maxSector, (long)index * entriesPerSector + entry);
                    break;
                }
            }
        }

        if (maxSector < 0)
        {
            return null;
        }

        var length = (maxSector + 2) * sectorSize;

        if (length > signature.MaxSize)
        {
            return null;
        }

        var extension = await DetectOleExtensionAsync(source, start, firstDirectory, sectorSize, cancellationToken).ConfigureAwait(false);
        var available = source.Length - start;

        return length > available
            ? new ExtentResult(available, IntegrityStatus.Truncated, "Die Datei ist kürzer als die Dateizuordnungstabelle angibt.", extension)
            : new ExtentResult(length, IntegrityStatus.Plausible, null, extension);
    }

    public static async Task<ExtentResult?> PdfAsync(IBlockSource source, long start, FileSignature signature, CancellationToken cancellationToken)
    {
        var limit = ExtentHelpers.Limit(source, start, signature);
        var pattern = "%%EOF"u8.ToArray();
        var searchFrom = start + 8;

        while (true)
        {
            var found = await ExtentHelpers.FindAsync(source, searchFrom, limit, pattern, cancellationToken).ConfigureAwait(false);

            if (found < 0)
            {
                return null;
            }

            var end = found + 5;
            var tail = await SourceReader.ReadAvailableAsync(source, end, 64, cancellationToken).ConfigureAwait(false);
            var skip = 0;

            while (skip < tail.Length && skip < 2 && (tail[skip] == '\r' || tail[skip] == '\n'))
            {
                skip++;
            }

            if (ContinuesWithObject(tail, skip))
            {
                searchFrom = found + 5;
                continue;
            }

            var beforeStart = Math.Max(start, found - 64);
            var before = await SourceReader.ReadAvailableAsync(source, beforeStart, (int)(found - beforeStart), cancellationToken).ConfigureAwait(false);
            var hasCrossReference = before.AsSpan().IndexOf("startxref"u8) >= 0;

            return new ExtentResult(
                end + skip - start,
                hasCrossReference ? IntegrityStatus.Valid : IntegrityStatus.Plausible,
                hasCrossReference ? null : "Der startxref-Eintrag vor dem Dateiende fehlt.");
        }
    }

    public static async Task<ExtentResult?> RtfAsync(IBlockSource source, long start, FileSignature signature, CancellationToken cancellationToken)
    {
        var reader = new BufferedReader(source, start, ExtentHelpers.Limit(source, start, signature));
        var depth = 0;

        while (true)
        {
            var value = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);

            if (value < 0)
            {
                return ExtentHelpers.Truncated(reader.Position - start, "Die schließende Klammer des Dokuments fehlt.");
            }

            if (value == '\\')
            {
                if (await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false) < 0)
                {
                    return ExtentHelpers.Truncated(reader.Position - start, "Die schließende Klammer des Dokuments fehlt.");
                }

                continue;
            }

            if (value == '{')
            {
                depth++;
            }
            else if (value == '}')
            {
                depth--;

                if (depth == 0)
                {
                    return new ExtentResult(reader.Position - start, IntegrityStatus.Valid, null);
                }
            }
        }
    }

    public static async Task<ExtentResult?> SqliteAsync(IBlockSource source, long start, FileSignature signature, CancellationToken cancellationToken)
    {
        var header = await SourceReader.ReadAsync(source, start, 100, cancellationToken).ConfigureAwait(false);

        if (header is null)
        {
            return null;
        }

        var rawPageSize = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(16));
        var pageSize = rawPageSize == 1 ? 65536 : (int)rawPageSize;

        if (pageSize < 512 || (pageSize & (pageSize - 1)) != 0)
        {
            return null;
        }

        if (header[18] is not (1 or 2) || header[19] is not (1 or 2) || header[21] != 64 || header[22] != 32 || header[23] != 32)
        {
            return null;
        }

        var changeCounter = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(24));
        var pageCount = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(28));
        var validFor = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(92));

        if (pageCount == 0 || validFor != changeCounter)
        {
            return null;
        }

        var length = (long)pageSize * pageCount;

        if (length > signature.MaxSize)
        {
            return null;
        }

        var available = source.Length - start;

        return length > available
            ? ExtentHelpers.Truncated(available, "Die Datei ist kürzer als Seitengröße mal Seitenzahl.")
            : new ExtentResult(length, IntegrityStatus.Plausible, null);
    }

    public static async Task<ExtentResult?> TextAsync(IBlockSource source, long start, FileSignature signature, CancellationToken cancellationToken)
    {
        var reader = new BufferedReader(source, start + 3, ExtentHelpers.Limit(source, start, signature));
        var good = reader.Position;

        while (true)
        {
            var value = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);

            if (value < 0)
            {
                break;
            }

            int continuation;

            if (value is 0x09 or 0x0A or 0x0D || (value >= 0x20 && value < 0x7F))
            {
                continuation = 0;
            }
            else if (value >= 0xC2 && value <= 0xDF)
            {
                continuation = 1;
            }
            else if (value >= 0xE0 && value <= 0xEF)
            {
                continuation = 2;
            }
            else if (value >= 0xF0 && value <= 0xF4)
            {
                continuation = 3;
            }
            else
            {
                break;
            }

            var valid = true;

            for (var index = 0; index < continuation; index++)
            {
                var next = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);

                if (next < 0x80 || next > 0xBF)
                {
                    valid = false;
                    break;
                }
            }

            if (!valid)
            {
                break;
            }

            good = reader.Position;
        }

        return new ExtentResult(good - start, IntegrityStatus.Plausible, "Textdatei ohne weitere Strukturprüfung.");
    }

    private static async Task<string> DetectOleExtensionAsync(IBlockSource source, long start, uint firstDirectory, int sectorSize, CancellationToken cancellationToken)
    {
        var directory = await SourceReader.ReadAvailableAsync(source, start + ((long)firstDirectory + 1) * sectorSize, sectorSize, cancellationToken).ConfigureAwait(false);

        for (var offset = 0; offset + 128 <= directory.Length; offset += 128)
        {
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(offset + 64));

            if (nameLength < 2 || nameLength > 64)
            {
                continue;
            }

            var name = Encoding.Unicode.GetString(directory, offset, nameLength - 2);

            if (name == "WordDocument")
            {
                return "doc";
            }

            if (name is "Workbook" or "Book")
            {
                return "xls";
            }

            if (name == "PowerPoint Document")
            {
                return "ppt";
            }
        }

        return "ole";
    }

    private static bool ContinuesWithObject(byte[] tail, int skip)
    {
        if (skip >= tail.Length || tail[skip] < '0' || tail[skip] > '9')
        {
            return false;
        }

        var text = Encoding.ASCII.GetString(tail, skip, Math.Min(24, tail.Length - skip));

        return text.Contains(" obj", StringComparison.Ordinal);
    }
}
