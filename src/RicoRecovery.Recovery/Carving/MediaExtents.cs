using System.Buffers.Binary;
using System.Text;
using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;

namespace RicoRecovery.Recovery.Carving;

internal static class MediaExtents
{
    private static readonly HashSet<string> TopLevelBoxes =
        ["ftyp", "moov", "mdat", "free", "skip", "wide", "uuid", "moof", "mfra", "sidx", "styp", "meta", "pnot", "junk"];

    private static readonly HashSet<long> SegmentChildren =
        [0x114D9B74, 0x1549A966, 0x1654AE6B, 0x1F43B675, 0x1C53BB6B, 0x1254C367, 0x1043A770, 0x1941A469, 0xEC, 0xBF];

    private static readonly int[] Mpeg1Layer1 = [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448];
    private static readonly int[] Mpeg1Layer2 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384];
    private static readonly int[] Mpeg1Layer3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] Mpeg2Layer1 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256];
    private static readonly int[] Mpeg2Layer23 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    private static readonly int[][] SampleRates = [[44100, 48000, 32000], [22050, 24000, 16000], [11025, 12000, 8000]];

    public static async Task<ExtentResult?> RiffAsync(IBlockSource source, long start, FileSignature signature, CancellationToken cancellationToken)
    {
        var header = await SourceReader.ReadAsync(source, start, 16, cancellationToken).ConfigureAwait(false);

        if (header is null)
        {
            return null;
        }

        var form = Encoding.ASCII.GetString(header, 8, 4);
        var chunk = Encoding.ASCII.GetString(header, 12, 4);
        string extension;

        if (form == "WAVE" && chunk == "fmt ")
        {
            extension = "wav";
        }
        else if (form == "AVI " && chunk == "LIST")
        {
            extension = "avi";
        }
        else
        {
            return null;
        }

        var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) + 8L;

        if (size > signature.MaxSize)
        {
            return null;
        }

        var available = source.Length - start;

        return size > available
            ? new ExtentResult(available, IntegrityStatus.Truncated, "Die Datei ist kürzer als im Header angegeben.", extension)
            : new ExtentResult(size, IntegrityStatus.Plausible, null, extension);
    }

    public static async Task<ExtentResult?> Mp4Async(IBlockSource source, long start, FileSignature signature, CancellationToken cancellationToken)
    {
        var limit = ExtentHelpers.Limit(source, start, signature);
        var position = start;
        var boxes = 0;
        var sawMoov = false;
        var sawMdat = false;
        var truncated = false;
        var extension = "mp4";

        while (position + 8 <= limit)
        {
            var head = await SourceReader.ReadAvailableAsync(source, position, 16, cancellationToken).ConfigureAwait(false);

            if (head.Length < 8)
            {
                break;
            }

            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(head);
            var type = Encoding.ASCII.GetString(head, 4, 4);

            if (boxes == 0 && type != "ftyp")
            {
                return null;
            }

            if (!TopLevelBoxes.Contains(type))
            {
                break;
            }

            long headerLength = 8;

            if (size == 1)
            {
                if (head.Length < 16)
                {
                    break;
                }

                size = BinaryPrimitives.ReadInt64BigEndian(head.AsSpan(8));
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = limit - position;
            }

            if (size < headerLength)
            {
                break;
            }

            if (boxes == 0 && head.Length >= 12)
            {
                extension = ExtensionForBrand(Encoding.ASCII.GetString(head, 8, 4));
            }

            if (position + size > limit)
            {
                truncated = true;
                position = limit;
                break;
            }

            sawMoov |= type == "moov";
            sawMdat |= type == "mdat";
            position += size;
            boxes++;
        }

        if (boxes == 0)
        {
            return null;
        }

        var integrity = truncated ? IntegrityStatus.Truncated : sawMoov && sawMdat ? IntegrityStatus.Valid : IntegrityStatus.Plausible;
        var note = truncated
            ? "Der letzte Block reicht über die Dateigrenze hinaus."
            : sawMoov ? null : "Der moov-Block fehlt, die Datei lässt sich möglicherweise nicht abspielen.";

        return new ExtentResult(position - start, integrity, note, extension);
    }

    public static async Task<ExtentResult?> MatroskaAsync(IBlockSource source, long start, FileSignature signature, CancellationToken cancellationToken)
    {
        var limit = ExtentHelpers.Limit(source, start, signature);
        var head = await SourceReader.ReadAvailableAsync(source, start, 128, cancellationToken).ConfigureAwait(false);

        if (!TryReadVint(head, 4, true, out var headerSize, out var headerSizeLength, out _))
        {
            return null;
        }

        var contentStart = 4 + headerSizeLength;
        var segmentOffset = contentStart + headerSize;
        var searchLength = Math.Max(0, (int)Math.Min(head.Length, segmentOffset) - contentStart);
        var extension = head.AsSpan(contentStart, searchLength).IndexOf("webm"u8) >= 0 ? "webm" : "mkv";
        var segment = await SourceReader.ReadAvailableAsync(source, start + segmentOffset, 16, cancellationToken).ConfigureAwait(false);

        if (segment.Length < 5 || !segment.AsSpan(0, 4).SequenceEqual(new byte[] { 0x18, 0x53, 0x80, 0x67 }))
        {
            return null;
        }

        if (!TryReadVint(segment, 4, true, out var segmentSize, out var segmentSizeLength, out var unknownSize))
        {
            return null;
        }

        var dataStart = segmentOffset + 4 + segmentSizeLength;

        if (!unknownSize)
        {
            var length = dataStart + segmentSize;
            var available = source.Length - start;

            if (length > signature.MaxSize)
            {
                return null;
            }

            return length > available
                ? new ExtentResult(available, IntegrityStatus.Truncated, "Die Datei ist kürzer als im Header angegeben.", extension)
                : new ExtentResult(length, IntegrityStatus.Plausible, null, extension);
        }

        var position = start + dataStart;
        var elements = 0;

        while (position + 12 <= limit)
        {
            var element = await SourceReader.ReadAvailableAsync(source, position, 16, cancellationToken).ConfigureAwait(false);

            if (!TryReadVint(element, 0, false, out var id, out var idLength, out _) || !SegmentChildren.Contains(id))
            {
                break;
            }

            if (!TryReadVint(element, idLength, true, out var size, out var sizeLength, out var sizeUnknown) || sizeUnknown)
            {
                break;
            }

            position += idLength + sizeLength + size;
            elements++;
        }

        return elements == 0
            ? null
            : new ExtentResult(Math.Min(position, limit) - start, IntegrityStatus.Plausible, "Segmentgröße unbekannt, Ende anhand der Elementfolge bestimmt.", extension);
    }

    public static async Task<ExtentResult?> MpegProgramAsync(IBlockSource source, long start, FileSignature signature, CancellationToken cancellationToken)
    {
        var limit = ExtentHelpers.Limit(source, start, signature);
        var found = await ExtentHelpers.FindAsync(source, start + 14, limit, [0x00, 0x00, 0x01, 0xB9], cancellationToken).ConfigureAwait(false);

        return found < 0 ? null : new ExtentResult(found + 4 - start, IntegrityStatus.Plausible, null);
    }

    public static async Task<ExtentResult?> Mp3Async(IBlockSource source, long start, FileSignature signature, CancellationToken cancellationToken)
    {
        var limit = ExtentHelpers.Limit(source, start, signature);
        var position = start;
        var minimumFrames = 16;
        var tag = await SourceReader.ReadAvailableAsync(source, start, 10, cancellationToken).ConfigureAwait(false);

        if (tag.Length == 10 && tag[0] == 'I' && tag[1] == 'D' && tag[2] == '3')
        {
            var tagSize = (tag[6] << 21) | (tag[7] << 14) | (tag[8] << 7) | tag[9];
            position = start + 10 + tagSize + ((tag[5] & 0x10) != 0 ? 10 : 0);
            minimumFrames = 4;
        }

        var reader = new BufferedReader(source, position, limit);
        var frames = 0;
        var truncated = false;

        while (true)
        {
            reader.Position = position;

            var frameHeader = await reader.ReadBytesAsync(4, cancellationToken).ConfigureAwait(false);
            var frameLength = frameHeader is null ? 0 : Mp3FrameLength(frameHeader);

            if (frameLength <= 0)
            {
                break;
            }

            if (position + frameLength > limit)
            {
                truncated = true;
                break;
            }

            position += frameLength;
            frames++;
        }

        if (frames < minimumFrames)
        {
            return null;
        }

        var trailer = await SourceReader.ReadAvailableAsync(source, position, 3, cancellationToken).ConfigureAwait(false);

        if (trailer.Length == 3 && trailer[0] == 'T' && trailer[1] == 'A' && trailer[2] == 'G' && position + 128 <= limit)
        {
            position += 128;
        }

        return new ExtentResult(
            position - start,
            truncated ? IntegrityStatus.Truncated : IntegrityStatus.Plausible,
            truncated ? "Der letzte Frame ist unvollständig." : null);
    }

    public static async Task<ExtentResult?> AdtsAsync(IBlockSource source, long start, FileSignature signature, CancellationToken cancellationToken)
    {
        var limit = ExtentHelpers.Limit(source, start, signature);
        var reader = new BufferedReader(source, start, limit);
        var position = start;
        var frames = 0;
        var truncated = false;

        while (true)
        {
            reader.Position = position;

            var header = await reader.ReadBytesAsync(7, cancellationToken).ConfigureAwait(false);

            if (header is null || header[0] != 0xFF || (header[1] & 0xF6) != 0xF0 || ((header[2] >> 2) & 0xF) >= 13)
            {
                break;
            }

            var frameLength = ((header[3] & 3) << 11) | (header[4] << 3) | (header[5] >> 5);

            if (frameLength < 7)
            {
                break;
            }

            if (position + frameLength > limit)
            {
                truncated = true;
                break;
            }

            position += frameLength;
            frames++;
        }

        if (frames < 16)
        {
            return null;
        }

        return new ExtentResult(
            position - start,
            truncated ? IntegrityStatus.Truncated : IntegrityStatus.Plausible,
            truncated ? "Der letzte Frame ist unvollständig." : null);
    }

    public static async Task<ExtentResult?> OggAsync(IBlockSource source, long start, FileSignature signature, CancellationToken cancellationToken)
    {
        var reader = new BufferedReader(source, start, ExtentHelpers.Limit(source, start, signature));
        var active = new HashSet<uint>();
        var pages = 0;
        var sawBegin = false;

        while (true)
        {
            var pageStart = reader.Position;
            var header = await reader.ReadBytesAsync(27, cancellationToken).ConfigureAwait(false);

            if (header is null || header[0] != 'O' || header[1] != 'g' || header[2] != 'g' || header[3] != 'S' || header[4] != 0)
            {
                return pages == 0 ? null : ExtentHelpers.Truncated(pageStart - start, "Das Ende des Ogg-Datenstroms wurde nicht gefunden.");
            }

            var type = header[5];
            var serial = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(14));

            if (pages == 0 && (type & 0x02) == 0)
            {
                return null;
            }

            var table = await reader.ReadBytesAsync(header[26], cancellationToken).ConfigureAwait(false);

            if (table is null)
            {
                return ExtentHelpers.Truncated(pageStart - start, "Die Segmenttabelle ist unvollständig.");
            }

            long payload = 0;

            foreach (var segment in table)
            {
                payload += segment;
            }

            reader.Position += payload;
            pages++;

            if ((type & 0x02) != 0)
            {
                active.Add(serial);
                sawBegin = true;
            }

            if ((type & 0x04) != 0)
            {
                active.Remove(serial);

                if (sawBegin && active.Count == 0)
                {
                    return new ExtentResult(reader.Position - start, IntegrityStatus.Valid, null);
                }
            }
        }
    }

    private static string ExtensionForBrand(string brand)
    {
        if (brand == "qt  ")
        {
            return "mov";
        }

        if (brand == "M4A ")
        {
            return "m4a";
        }

        if (brand == "M4V ")
        {
            return "m4v";
        }

        return brand.StartsWith("3g", StringComparison.Ordinal) ? "3gp" : "mp4";
    }

    private static bool TryReadVint(byte[] data, int offset, bool stripMarker, out long value, out int length, out bool unknown)
    {
        value = 0;
        length = 0;
        unknown = false;

        if (offset < 0 || offset >= data.Length || data[offset] == 0)
        {
            return false;
        }

        var first = data[offset];
        var mask = 0x80;
        length = 1;

        while ((first & mask) == 0)
        {
            mask >>= 1;
            length++;
        }

        if (offset + length > data.Length)
        {
            return false;
        }

        value = stripMarker ? first & (mask - 1) : first;
        var allOnes = (first & (mask - 1)) == mask - 1;

        for (var index = 1; index < length; index++)
        {
            value = (value << 8) | data[offset + index];

            if (data[offset + index] != 0xFF)
            {
                allOnes = false;
            }
        }

        unknown = allOnes;

        return true;
    }

    private static int Mp3FrameLength(byte[] header)
    {
        if (header[0] != 0xFF || (header[1] & 0xE0) != 0xE0)
        {
            return 0;
        }

        var version = (header[1] >> 3) & 3;
        var layer = (header[1] >> 1) & 3;
        var bitrateIndex = header[2] >> 4;
        var sampleIndex = (header[2] >> 2) & 3;
        var padding = (header[2] >> 1) & 1;

        if (version == 1 || layer == 0 || bitrateIndex is 0 or 15 || sampleIndex == 3)
        {
            return 0;
        }

        var mpeg1 = version == 3;
        var table = mpeg1
            ? layer == 3 ? Mpeg1Layer1 : layer == 2 ? Mpeg1Layer2 : Mpeg1Layer3
            : layer == 3 ? Mpeg2Layer1 : Mpeg2Layer23;
        var bitrate = table[bitrateIndex] * 1000;
        var sampleRate = SampleRates[version == 3 ? 0 : version == 2 ? 1 : 2][sampleIndex];

        if (layer == 3)
        {
            return (12 * bitrate / sampleRate + padding) * 4;
        }

        var factor = layer == 1 && !mpeg1 ? 72 : 144;

        return factor * bitrate / sampleRate + padding;
    }
}
