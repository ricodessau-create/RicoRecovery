using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;

namespace RicoRecovery.Recovery.Carving;

internal sealed record CarveContext(SignatureMatcher Matcher, int Alignment);

internal static class ExtentResolver
{
    public static Task<ExtentResult?> ResolveAsync(
        IBlockSource source,
        long start,
        FileSignature signature,
        CarveContext context,
        CancellationToken cancellationToken) =>
        signature.Strategy switch
        {
            ExtentStrategy.Jpeg => ImageExtents.JpegAsync(source, start, signature, cancellationToken),
            ExtentStrategy.Png => ImageExtents.PngAsync(source, start, signature, cancellationToken),
            ExtentStrategy.Gif => ImageExtents.GifAsync(source, start, signature, cancellationToken),
            ExtentStrategy.Bmp => ImageExtents.BmpAsync(source, start, signature, cancellationToken),
            ExtentStrategy.Riff => MediaExtents.RiffAsync(source, start, signature, cancellationToken),
            ExtentStrategy.Mp4 => MediaExtents.Mp4Async(source, start, signature, cancellationToken),
            ExtentStrategy.Matroska => MediaExtents.MatroskaAsync(source, start, signature, cancellationToken),
            ExtentStrategy.Mp3 => MediaExtents.Mp3Async(source, start, signature, cancellationToken),
            ExtentStrategy.Adts => MediaExtents.AdtsAsync(source, start, signature, cancellationToken),
            ExtentStrategy.Ogg => MediaExtents.OggAsync(source, start, signature, cancellationToken),
            ExtentStrategy.MpegProgram => MediaExtents.MpegProgramAsync(source, start, signature, cancellationToken),
            ExtentStrategy.Ole => DocumentExtents.OleAsync(source, start, signature, cancellationToken),
            ExtentStrategy.Zip => ArchiveExtents.ZipAsync(source, start, signature, cancellationToken),
            ExtentStrategy.SevenZip => ArchiveExtents.SevenZipAsync(source, start, signature, cancellationToken),
            ExtentStrategy.Rar => ArchiveExtents.RarAsync(source, start, signature, cancellationToken),
            ExtentStrategy.Tar => ArchiveExtents.TarAsync(source, start, signature, cancellationToken),
            ExtentStrategy.Pdf => DocumentExtents.PdfAsync(source, start, signature, cancellationToken),
            ExtentStrategy.Rtf => DocumentExtents.RtfAsync(source, start, signature, cancellationToken),
            ExtentStrategy.Sqlite => DocumentExtents.SqliteAsync(source, start, signature, cancellationToken),
            ExtentStrategy.Text => DocumentExtents.TextAsync(source, start, signature, cancellationToken),
            ExtentStrategy.Unbounded => UnboundedExtent.ResolveAsync(source, start, signature, context, cancellationToken),
            _ => Task.FromResult<ExtentResult?>(null)
        };
}
