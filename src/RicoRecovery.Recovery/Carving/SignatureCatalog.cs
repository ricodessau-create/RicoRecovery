using System.Text;
using RicoRecovery.Core.Models;

namespace RicoRecovery.Recovery.Carving;

public static class SignatureCatalog
{
    private const long MiB = 1024L * 1024;
    private const long GiB = 1024L * MiB;

    public static IReadOnlyList<FileSignature> All { get; } = Build();

    private static List<FileSignature> Build() =>
    [
        Make("JPEG", "jpg", FileCategory.Image, [0xFF, 0xD8, 0xFF], ExtentStrategy.Jpeg, 128, 256 * MiB),
        Make("PNG", "png", FileCategory.Image, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], ExtentStrategy.Png, 64, 256 * MiB),
        Make("GIF87a", "gif", FileCategory.Image, Ascii("GIF87a"), ExtentStrategy.Gif, 20, 64 * MiB),
        Make("GIF89a", "gif", FileCategory.Image, Ascii("GIF89a"), ExtentStrategy.Gif, 20, 64 * MiB),
        Make("BMP", "bmp", FileCategory.Image, Ascii("BM"), ExtentStrategy.Bmp, 58, 256 * MiB),
        Make("TIFF (Intel)", "tif", FileCategory.Image, [0x49, 0x49, 0x2A, 0x00], ExtentStrategy.Unbounded, 1024, 256 * MiB),
        Make("TIFF (Motorola)", "tif", FileCategory.Image, [0x4D, 0x4D, 0x00, 0x2A], ExtentStrategy.Unbounded, 1024, 256 * MiB),
        Make("Canon RAW (CR2)", "cr2", FileCategory.Image, [0x49, 0x49, 0x2A, 0x00, 0x10, 0x00, 0x00, 0x00, 0x43, 0x52], ExtentStrategy.Unbounded, 4096, 128 * MiB),
        Make("Fujifilm RAW (RAF)", "raf", FileCategory.Image, Ascii("FUJIFILMCCD-RAW "), ExtentStrategy.Unbounded, 4096, 128 * MiB),
        Make("MP4 / MOV", "mp4", FileCategory.Video, Ascii("ftyp"), ExtentStrategy.Mp4, 32, 16 * GiB, 4),
        Make("RIFF (WAV / AVI)", "wav", FileCategory.Audio, Ascii("RIFF"), ExtentStrategy.Riff, 44, 4 * GiB),
        Make("Matroska / WebM", "mkv", FileCategory.Video, [0x1A, 0x45, 0xDF, 0xA3], ExtentStrategy.Matroska, 256, 16 * GiB),
        Make("MPEG-Programmstrom", "mpg", FileCategory.Video, [0x00, 0x00, 0x01, 0xBA], ExtentStrategy.MpegProgram, 2048, 4 * GiB),
        Make("MP3 (ID3)", "mp3", FileCategory.Audio, Ascii("ID3"), ExtentStrategy.Mp3, 1024, 512 * MiB),
        Make("MP3 (FFFB)", "mp3", FileCategory.Audio, [0xFF, 0xFB], ExtentStrategy.Mp3, 4096, 512 * MiB),
        Make("MP3 (FFFA)", "mp3", FileCategory.Audio, [0xFF, 0xFA], ExtentStrategy.Mp3, 4096, 512 * MiB),
        Make("MP3 (FFF3)", "mp3", FileCategory.Audio, [0xFF, 0xF3], ExtentStrategy.Mp3, 4096, 512 * MiB),
        Make("MP3 (FFF2)", "mp3", FileCategory.Audio, [0xFF, 0xF2], ExtentStrategy.Mp3, 4096, 512 * MiB),
        Make("AAC (ADTS, MPEG-4)", "aac", FileCategory.Audio, [0xFF, 0xF1], ExtentStrategy.Adts, 2048, 512 * MiB),
        Make("AAC (ADTS, MPEG-2)", "aac", FileCategory.Audio, [0xFF, 0xF9], ExtentStrategy.Adts, 2048, 512 * MiB),
        Make("FLAC", "flac", FileCategory.Audio, Ascii("fLaC"), ExtentStrategy.Unbounded, 4096, 1 * GiB),
        Make("Ogg", "ogg", FileCategory.Audio, Ascii("OggS"), ExtentStrategy.Ogg, 32, 1 * GiB),
        Make("OLE (DOC / XLS / PPT)", "ole", FileCategory.Document, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1], ExtentStrategy.Ole, 1536, 2 * GiB),
        Make("ZIP (ZIP / DOCX / XLSX / PPTX / APK)", "zip", FileCategory.Archive, [0x50, 0x4B, 0x03, 0x04], ExtentStrategy.Zip, 98, 4 * GiB),
        Make("PDF", "pdf", FileCategory.Document, Ascii("%PDF-"), ExtentStrategy.Pdf, 64, 1 * GiB),
        Make("RTF", "rtf", FileCategory.Document, Ascii("{\\rtf1"), ExtentStrategy.Rtf, 32, 64 * MiB),
        Make("Text (UTF-8 mit BOM)", "txt", FileCategory.Document, [0xEF, 0xBB, 0xBF], ExtentStrategy.Text, 128, 8 * MiB),
        Make("RAR 4", "rar", FileCategory.Archive, [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00], ExtentStrategy.Rar, 20, 16 * GiB),
        Make("RAR 5", "rar", FileCategory.Archive, [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00], ExtentStrategy.Rar, 20, 16 * GiB),
        Make("7-Zip", "7z", FileCategory.Archive, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C], ExtentStrategy.SevenZip, 32, 16 * GiB),
        Make("TAR", "tar", FileCategory.Archive, Ascii("ustar"), ExtentStrategy.Tar, 1024, 16 * GiB, 257),
        Make("SQLite", "sqlite", FileCategory.Database, Ascii("SQLite format 3\0"), ExtentStrategy.Sqlite, 512, 16 * GiB)
    ];

    private static FileSignature Make(
        string name,
        string extension,
        FileCategory category,
        byte[] header,
        ExtentStrategy strategy,
        long minSize,
        long maxSize,
        int headerOffset = 0) =>
        new(name, extension, category, headerOffset, header, strategy, minSize, maxSize);

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
}
