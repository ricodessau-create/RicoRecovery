using RicoRecovery.Core.Models;
using RicoRecovery.Recovery.Carving;
using RicoRecovery.Tests.TestSupport;
using Xunit;

namespace RicoRecovery.Tests;

public sealed class SignatureCarverTests
{
    private const int StartSector = 4;

    [Fact]
    public Task PngIsCarvedAndValidated() => AssertCarvedAsync(SampleFiles.Png(), "png", IntegrityStatus.Valid);

    [Fact]
    public Task JpegIsCarvedWithExactEnd() => AssertCarvedAsync(SampleFiles.Jpeg(), "jpg", IntegrityStatus.Valid);

    [Fact]
    public Task GifIsCarved() => AssertCarvedAsync(SampleFiles.Gif(), "gif", IntegrityStatus.Valid);

    [Fact]
    public Task BmpIsCarvedFromHeaderSize() => AssertCarvedAsync(SampleFiles.Bmp(), "bmp", IntegrityStatus.Plausible);

    [Fact]
    public Task WavIsCarvedFromRiffSize() => AssertCarvedAsync(SampleFiles.Wav(), "wav", IntegrityStatus.Plausible);

    [Fact]
    public Task Mp4IsCarvedByBoxWalk() => AssertCarvedAsync(SampleFiles.Mp4(), "mp4", IntegrityStatus.Valid);

    [Fact]
    public Task OggIsCarvedByPageWalk() => AssertCarvedAsync(SampleFiles.Ogg(), "ogg", IntegrityStatus.Valid);

    [Fact]
    public Task Mp3IsCarvedByFrameWalk() => AssertCarvedAsync(SampleFiles.Mp3(), "mp3", IntegrityStatus.Plausible);

    [Fact]
    public Task PdfIsCarvedUpToEndMarker() => AssertCarvedAsync(SampleFiles.Pdf(), "pdf", IntegrityStatus.Valid);

    [Fact]
    public Task RtfIsCarvedByBraceMatching() => AssertCarvedAsync(SampleFiles.Rtf(), "rtf", IntegrityStatus.Valid);

    [Fact]
    public Task TextWithBomIsCarved() => AssertCarvedAsync(SampleFiles.TextWithBom(), "txt", IntegrityStatus.Plausible);

    [Fact]
    public Task ZipIsCarvedFromCentralDirectory() => AssertCarvedAsync(SampleFiles.Zip(), "zip", IntegrityStatus.Valid);

    [Fact]
    public Task DocxIsRecognizedInsideZip() => AssertCarvedAsync(SampleFiles.Docx(), "docx", IntegrityStatus.Valid);

    [Fact]
    public Task OleDocumentIsRecognizedAsDoc() => AssertCarvedAsync(SampleFiles.Ole(), "doc", IntegrityStatus.Plausible);

    [Fact]
    public Task SevenZipIsCarvedFromStartHeader() => AssertCarvedAsync(SampleFiles.SevenZip(), "7z", IntegrityStatus.Valid);

    [Fact]
    public Task TarIsCarvedByHeaderWalk() => AssertCarvedAsync(SampleFiles.Tar(), "tar", IntegrityStatus.Valid);

    [Fact]
    public Task SqliteIsCarvedFromPageCount() => AssertCarvedAsync(SampleFiles.Sqlite(), "sqlite", IntegrityStatus.Plausible);

    [Fact]
    public async Task PngWithFlippedByteIsReportedAsDamaged()
    {
        var file = SampleFiles.Png();
        file[60] ^= 0xFF;

        var (_, result) = await CarveAsync(file);

        var carved = Assert.Single(result.Files);
        Assert.Equal(IntegrityStatus.Damaged, carved.Integrity);
        Assert.Equal(file.Length, carved.Length);
    }

    [Fact]
    public async Task PngWithoutEndChunkIsReportedAsTruncated()
    {
        var file = SampleFiles.Png();
        var cut = file[..^12];

        var (_, result) = await CarveAsync(cut);

        var carved = Assert.Single(result.Files);
        Assert.Equal(IntegrityStatus.Truncated, carved.Integrity);
        Assert.Equal(cut.Length, carved.Length);
    }

    [Fact]
    public async Task JpegWithoutEndMarkerIsReportedAsTruncated()
    {
        var file = SampleFiles.Jpeg();

        var (_, result) = await CarveAsync(file[..^2]);

        Assert.Equal(IntegrityStatus.Truncated, Assert.Single(result.Files).Integrity);
    }

    [Fact]
    public async Task CarvedFileConvertsToEntryWithRunAndCategory()
    {
        var file = SampleFiles.Png();

        var (_, result) = await CarveAsync(file);
        var entry = Assert.Single(result.Files).ToEntry();

        Assert.StartsWith("/(Carving)/Image/Datei_", entry.Path, StringComparison.Ordinal);
        Assert.EndsWith(".png", entry.Name, StringComparison.Ordinal);
        Assert.Equal(file.Length, entry.Size);
        Assert.Equal(StartSector * 512L, Assert.Single(entry.DataRuns).Offset);
    }

    [Fact]
    public async Task TwoFilesAreFoundIndependently()
    {
        var png = SampleFiles.Png();
        var gif = SampleFiles.Gif();
        var image = new byte[64 * 512];
        png.CopyTo(image, 4 * 512);
        gif.CopyTo(image, 10 * 512);

        var result = await new DeepScanner().ScanAsync(new MemoryBlockSource(image));

        Assert.Equal(2, result.Files.Count);
        Assert.Equal("png", result.Files[0].Extension);
        Assert.Equal("gif", result.Files[1].Extension);
    }

    [Fact]
    public async Task EmptyImageYieldsNoFiles()
    {
        var result = await new DeepScanner().ScanAsync(new MemoryBlockSource(new byte[64 * 512]));

        Assert.Empty(result.Files);
        Assert.Empty(result.UnknownCandidates);
    }

    private static (byte[] Image, int Sectors) Place(byte[] file)
    {
        var sectors = StartSector + (file.Length + 511) / 512 + 8;
        var image = new byte[sectors * 512];
        file.CopyTo(image, StartSector * 512);

        return (image, sectors);
    }

    private static async Task<(byte[] Image, DeepScanResult Result)> CarveAsync(byte[] file)
    {
        var (image, _) = Place(file);
        var result = await new DeepScanner().ScanAsync(new MemoryBlockSource(image));

        return (image, result);
    }

    private static async Task AssertCarvedAsync(byte[] file, string extension, IntegrityStatus integrity)
    {
        var (image, result) = await CarveAsync(file);

        var carved = Assert.Single(result.Files);
        Assert.Equal(StartSector * 512L, carved.Offset);
        Assert.Equal(file.Length, carved.Length);
        Assert.Equal(extension, carved.Extension);
        Assert.Equal(integrity, carved.Integrity);
        Assert.Equal(file, await ScanHelper.ReadContentAsync(image, carved.ToEntry()));
    }
}
