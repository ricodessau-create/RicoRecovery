using System.Buffers.Binary;
using System.Text;
using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;

namespace RicoRecovery.Recovery.Carving;

internal static class ImageExtents
{
    private const string EndNotFound = "Das Dateiende wurde nicht gefunden.";

    public static async Task<ExtentResult?> JpegAsync(IBlockSource source, long start, FileSignature signature, CancellationToken cancellationToken)
    {
        var reader = new BufferedReader(source, start + 2, ExtentHelpers.Limit(source, start, signature));
        var sawFrame = false;
        var sawScan = false;

        while (true)
        {
            var marker = await NextMarkerAsync(reader, cancellationToken).ConfigureAwait(false);

            if (marker == -2)
            {
                return ExtentHelpers.Damaged(reader.Position - start, "Ungültige Markerstruktur.");
            }

            if (marker < 0)
            {
                return ExtentHelpers.Truncated(reader.Position - start, EndNotFound);
            }

            if (marker == 0xD9)
            {
                return new ExtentResult(reader.Position - start, sawFrame && sawScan ? IntegrityStatus.Valid : IntegrityStatus.Plausible, null);
            }

            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD8))
            {
                continue;
            }

            var high = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
            var low = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);

            if (high < 0 || low < 0)
            {
                return ExtentHelpers.Truncated(reader.Position - start, EndNotFound);
            }

            var segmentLength = (high << 8) | low;

            if (segmentLength < 2)
            {
                return ExtentHelpers.Damaged(reader.Position - start, "Ungültige Segmentlänge.");
            }

            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                sawFrame = true;
            }

            reader.Position += segmentLength - 2;

            if (marker == 0xDA)
            {
                sawScan = true;

                if (!await SkipEntropyAsync(reader, cancellationToken).ConfigureAwait(false))
                {
                    return ExtentHelpers.Truncated(reader.Position - start, EndNotFound);
                }
            }
        }
    }

    public static async Task<ExtentResult?> PngAsync(IBlockSource source, long start, FileSignature signature, CancellationToken cancellationToken)
    {
        var limit = ExtentHelpers.Limit(source, start, signature);
        var position = start + 8;
        var damaged = false;
        var first = true;

        while (true)
        {
            var head = position + 12 <= limit
                ? await SourceReader.ReadAsync(source, position, 8, cancellationToken).ConfigureAwait(false)
                : null;

            if (head is null || !IsChunkType(head))
            {
                return first ? null : ExtentHelpers.Truncated(position - start, "Das Dateiende (IEND) wurde nicht gefunden.");
            }

            var type = Encoding.ASCII.GetString(head, 4, 4);
            var length = BinaryPrimitives.ReadUInt32BigEndian(head);

            if (first && (type != "IHDR" || length != 13))
            {
                return null;
            }

            first = false;

            var total = 12L + length;

            if (length > int.MaxValue || position + total > limit)
            {
                return ExtentHelpers.Truncated(position - start, "Das Dateiende (IEND) wurde nicht gefunden.");
            }

            var crc = new Crc32Stream();
            crc.Update(head.AsSpan(4, 4));

            var offset = position + 8;
            var remaining = (long)length;

            while (remaining > 0)
            {
                var count = (int)Math.Min(remaining, 1 << 20);
                var block = await SourceReader.ReadAsync(source, offset, count, cancellationToken).ConfigureAwait(false);

                if (block is null)
                {
                    return ExtentHelpers.Truncated(position - start, "Chunk nicht lesbar.");
                }

                crc.Update(block);
                offset += count;
                remaining -= count;
            }

            var stored = await SourceReader.ReadAsync(source, offset, 4, cancellationToken).ConfigureAwait(false);

            if (stored is null)
            {
                return ExtentHelpers.Truncated(position - start, "Prüfsumme nicht lesbar.");
            }

            if (crc.Value != BinaryPrimitives.ReadUInt32BigEndian(stored))
            {
                damaged = true;
            }

            position += total;

            if (type == "IEND")
            {
                return damaged
                    ? ExtentHelpers.Damaged(position - start, "Mindestens eine Chunk-Prüfsumme stimmt nicht.")
                    : new ExtentResult(position - start, IntegrityStatus.Valid, null);
            }
        }
    }

    public static async Task<ExtentResult?> GifAsync(IBlockSource source, long start, FileSignature signature, CancellationToken cancellationToken)
    {
        var reader = new BufferedReader(source, start + 6, ExtentHelpers.Limit(source, start, signature));
        var screen = await reader.ReadBytesAsync(7, cancellationToken).ConfigureAwait(false);

        if (screen is null)
        {
            return null;
        }

        if ((screen[4] & 0x80) != 0)
        {
            reader.Position += 3 << ((screen[4] & 7) + 1);
        }

        var sawImage = false;

        while (true)
        {
            var block = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);

            if (block == 0x3B)
            {
                return new ExtentResult(reader.Position - start, sawImage ? IntegrityStatus.Valid : IntegrityStatus.Plausible, null);
            }

            if (block == 0x21)
            {
                if (await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false) < 0 || !await SkipSubBlocksAsync(reader, cancellationToken).ConfigureAwait(false))
                {
                    return ExtentHelpers.Truncated(reader.Position - start, EndNotFound);
                }

                continue;
            }

            if (block != 0x2C)
            {
                return block < 0
                    ? ExtentHelpers.Truncated(reader.Position - start, EndNotFound)
                    : ExtentHelpers.Damaged(reader.Position - start, "Ungültiger GIF-Block.");
            }

            var descriptor = await reader.ReadBytesAsync(9, cancellationToken).ConfigureAwait(false);

            if (descriptor is null)
            {
                return ExtentHelpers.Truncated(reader.Position - start, EndNotFound);
            }

            if ((descriptor[8] & 0x80) != 0)
            {
                reader.Position += 3 << ((descriptor[8] & 7) + 1);
            }

            if (await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false) < 0 || !await SkipSubBlocksAsync(reader, cancellationToken).ConfigureAwait(false))
            {
                return ExtentHelpers.Truncated(reader.Position - start, EndNotFound);
            }

            sawImage = true;
        }
    }

    public static async Task<ExtentResult?> BmpAsync(IBlockSource source, long start, FileSignature signature, CancellationToken cancellationToken)
    {
        var header = await SourceReader.ReadAsync(source, start, 30, cancellationToken).ConfigureAwait(false);

        if (header is null)
        {
            return null;
        }

        var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(2));
        var reserved = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(6));
        var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(10));
        var dibSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(14));

        if (reserved != 0 || dibSize is not (12 or 40 or 52 or 56 or 64 or 108 or 124))
        {
            return null;
        }

        if (dataOffset < 14 + dibSize || size < dataOffset || size > signature.MaxSize)
        {
            return null;
        }

        if (dibSize >= 40)
        {
            var planes = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26));
            var bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));

            if (planes != 1 || bitsPerPixel is not (1 or 4 or 8 or 16 or 24 or 32))
            {
                return null;
            }
        }

        var available = source.Length - start;

        return size > available
            ? ExtentHelpers.Truncated(available, "Die Datei ist kürzer als im Header angegeben.")
            : new ExtentResult(size, IntegrityStatus.Plausible, null);
    }

    private static bool IsChunkType(byte[] head) =>
        IsLetter(head[4]) && IsLetter(head[5]) && IsLetter(head[6]) && IsLetter(head[7]);

    private static bool IsLetter(byte value) => (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');

    private static async Task<int> NextMarkerAsync(BufferedReader reader, CancellationToken cancellationToken)
    {
        var value = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);

        if (value < 0)
        {
            return -1;
        }

        if (value != 0xFF)
        {
            return -2;
        }

        int marker;

        do
        {
            marker = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
        }
        while (marker == 0xFF);

        return marker;
    }

    private static async Task<bool> SkipEntropyAsync(BufferedReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            var value = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);

            if (value < 0)
            {
                return false;
            }

            if (value != 0xFF)
            {
                continue;
            }

            var next = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);

            if (next < 0)
            {
                return false;
            }

            if (next == 0x00 || (next >= 0xD0 && next <= 0xD7))
            {
                continue;
            }

            if (next == 0xFF)
            {
                reader.Position--;
                continue;
            }

            reader.Position -= 2;

            return true;
        }
    }

    private static async Task<bool> SkipSubBlocksAsync(BufferedReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            var size = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);

            if (size < 0)
            {
                return false;
            }

            if (size == 0)
            {
                return true;
            }

            reader.Position += size;
        }
    }
}
