using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using RicoRecovery.Storage.Partitioning;

namespace RicoRecovery.Tests.TestSupport;

internal static class SampleFiles
{
    public static byte[] Png() =>
        Concat(
            Bytes(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A),
            PngChunk("IHDR", Bytes(0, 0, 0, 1, 0, 0, 0, 1, 8, 2, 0, 0, 0)),
            PngChunk("IDAT", ScanHelper.Pattern(300)),
            PngChunk("IEND", []));

    public static byte[] Jpeg()
    {
        var comment = Encoding.ASCII.GetBytes(new string('x', 200));
        var length = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)(comment.Length + 2));

        return Concat(
            Bytes(0xFF, 0xD8),
            Bytes(0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00),
            Bytes(0xFF, 0xFE),
            length,
            comment,
            Bytes(0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x01, 0x00, 0x01, 0x01, 0x01, 0x11, 0x00),
            Bytes(0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00),
            Bytes(0x12, 0x34, 0xFF, 0x00, 0x56, 0xFF, 0xD0, 0x78, 0x9A),
            Bytes(0xFF, 0xD9));
    }

    public static byte[] Gif() =>
        Concat(
            Encoding.ASCII.GetBytes("GIF89a"),
            Bytes(1, 0, 1, 0, 0x80, 0, 0),
            Bytes(0, 0, 0, 255, 255, 255),
            Bytes(0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0),
            Bytes(2),
            Bytes(2, 0x44, 0x01, 0),
            Bytes(0x3B));

    public static byte[] Bmp()
    {
        var data = new byte[70];

        data[0] = (byte)'B';
        data[1] = (byte)'M';
        ImageWriter.U32(data, 2, 70);
        ImageWriter.U32(data, 10, 54);
        ImageWriter.U32(data, 14, 40);
        ImageWriter.U32(data, 18, 2);
        ImageWriter.U32(data, 22, 2);
        ImageWriter.U16(data, 26, 1);
        ImageWriter.U16(data, 28, 24);
        ImageWriter.U32(data, 34, 16);
        ScanHelper.Pattern(16).CopyTo(data, 54);

        return data;
    }

    public static byte[] Wav()
    {
        var data = new byte[52];

        Encoding.ASCII.GetBytes("RIFF").CopyTo(data, 0);
        ImageWriter.U32(data, 4, 44);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(data, 8);
        ImageWriter.U32(data, 16, 16);
        ImageWriter.U16(data, 20, 1);
        ImageWriter.U16(data, 22, 1);
        ImageWriter.U32(data, 24, 8000);
        Encoding.ASCII.GetBytes("data").CopyTo(data, 36);
        ImageWriter.U32(data, 40, 8);
        ScanHelper.Pattern(8).CopyTo(data, 44);

        return data;
    }

    public static byte[] Mp4()
    {
        var ftyp = new byte[24];
        ImageWriter.U32(ftyp, 0, 0);
        BinaryPrimitives.WriteUInt32BigEndian(ftyp, 24);
        Encoding.ASCII.GetBytes("ftypisom").CopyTo(ftyp, 4);
        Encoding.ASCII.GetBytes("isommp41").CopyTo(ftyp, 16);

        return Concat(ftyp, Box("mdat", ScanHelper.Pattern(8)), Box("moov", ScanHelper.Pattern(8)));
    }

    public static byte[] Ogg() =>
        Concat(OggPage(0x02, 0, Bytes(1, 2, 3, 4)), OggPage(0x04, 1, Bytes(5, 6, 7)));

    public static byte[] Mp3()
    {
        var frame = new byte[417];
        Bytes(0xFF, 0xFB, 0x90, 0x00).CopyTo(frame, 0);

        return Concat(Enumerable.Repeat(frame, 20).ToArray());
    }

    public static byte[] Pdf() =>
        Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<< /Type /Catalog >>\nendobj\ntrailer\n<< /Root 1 0 R >>\nstartxref\n0\n%%EOF\n");

    public static byte[] Rtf() =>
        Encoding.ASCII.GetBytes("{\\rtf1\\ansi\\deff0 {\\fonttbl {\\f0 Arial;}}Hallo Welt\\par}");

    public static byte[] TextWithBom() =>
        Concat(Bytes(0xEF, 0xBB, 0xBF), Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Dies ist ein Text mit Umlauten äöü.\n", 8))));

    public static byte[] Zip() => CreateZip("a.txt");

    public static byte[] Docx() => CreateZip("word/document.xml");

    public static byte[] Ole()
    {
        var data = new byte[1536];

        Bytes(0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1).CopyTo(data, 0);
        ImageWriter.U16(data, 24, 0x3E);
        ImageWriter.U16(data, 26, 3);
        ImageWriter.U16(data, 28, 0xFFFE);
        ImageWriter.U16(data, 30, 9);
        ImageWriter.U16(data, 32, 6);
        ImageWriter.U32(data, 44, 1);
        ImageWriter.U32(data, 48, 1);
        ImageWriter.U32(data, 68, 0xFFFFFFFE);
        ImageWriter.U32(data, 72, 0);
        ImageWriter.U32(data, 76, 0);

        for (var index = 1; index < 109; index++)
        {
            ImageWriter.U32(data, 76 + index * 4, 0xFFFFFFFF);
        }

        ImageWriter.U32(data, 512, 0xFFFFFFFD);
        ImageWriter.U32(data, 516, 0xFFFFFFFE);

        for (var entry = 2; entry < 128; entry++)
        {
            ImageWriter.U32(data, 512 + entry * 4, 0xFFFFFFFF);
        }

        WriteDirectoryEntry(data, 1024, "Root Entry");
        WriteDirectoryEntry(data, 1024 + 128, "WordDocument");

        return data;
    }

    public static byte[] SevenZip()
    {
        var nextHeader = ScanHelper.Pattern(10);
        var data = new byte[62];

        Bytes(0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04).CopyTo(data, 0);
        ImageWriter.U64(data, 12, 20);
        ImageWriter.U64(data, 20, 10);
        ImageWriter.U32(data, 28, Crc32.Compute(nextHeader));
        ImageWriter.U32(data, 8, Crc32.Compute(data.AsSpan(12, 20)));
        nextHeader.CopyTo(data, 52);

        return data;
    }

    public static byte[] Tar()
    {
        var header = new byte[512];

        Encoding.ASCII.GetBytes("a.txt").CopyTo(header, 0);
        Encoding.ASCII.GetBytes("0000644\0").CopyTo(header, 100);
        Encoding.ASCII.GetBytes("00000000005\0").CopyTo(header, 124);
        Encoding.ASCII.GetBytes("00000000000\0").CopyTo(header, 136);
        header[156] = (byte)'0';
        Encoding.ASCII.GetBytes("ustar\0").CopyTo(header, 257);
        Encoding.ASCII.GetBytes("00").CopyTo(header, 263);

        for (var index = 148; index < 156; index++)
        {
            header[index] = 0x20;
        }

        var sum = header.Sum(value => (int)value);
        Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ").CopyTo(header, 148);

        var content = new byte[512];
        Encoding.ASCII.GetBytes("hello").CopyTo(content, 0);

        return Concat(header, content, new byte[1024]);
    }

    public static byte[] Sqlite()
    {
        var data = new byte[1024];

        Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(16), 512);
        data[18] = 1;
        data[19] = 1;
        data[21] = 64;
        data[22] = 32;
        data[23] = 32;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(24), 5);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(28), 2);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(92), 5);

        return data;
    }

    public static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    public static byte[] Bytes(params int[] values) => values.Select(value => (byte)value).ToArray();

    private static byte[] PngChunk(string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        var length = new byte[4];
        var crc = new byte[4];

        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.Compute(Concat(typeBytes, data)));

        return Concat(length, typeBytes, data, crc);
    }

    private static byte[] Box(string type, byte[] data)
    {
        var box = new byte[8 + data.Length];

        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        data.CopyTo(box, 8);

        return box;
    }

    private static byte[] OggPage(byte type, int sequence, byte[] payload)
    {
        var page = new byte[27 + 1 + payload.Length];

        Encoding.ASCII.GetBytes("OggS").CopyTo(page, 0);
        page[5] = type;
        ImageWriter.U32(page, 14, 1);
        ImageWriter.U32(page, 18, sequence);
        page[26] = 1;
        page[27] = (byte)payload.Length;
        payload.CopyTo(page, 28);

        return page;
    }

    private static byte[] CreateZip(string entryName)
    {
        using var stream = new MemoryStream();

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry(entryName);
            using var entryStream = entry.Open();
            entryStream.Write(Encoding.ASCII.GetBytes(new string('a', 200)));
        }

        return stream.ToArray();
    }

    private static void WriteDirectoryEntry(byte[] data, int offset, string name)
    {
        Encoding.Unicode.GetBytes(name).CopyTo(data, offset);
        ImageWriter.U16(data, offset + 64, (name.Length + 1) * 2);
    }
}
