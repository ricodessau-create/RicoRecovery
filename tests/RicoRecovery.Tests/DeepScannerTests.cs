using System.Security.Cryptography;
using RicoRecovery.Core.Models;
using RicoRecovery.FileSystems;
using RicoRecovery.Recovery;
using RicoRecovery.Recovery.Carving;
using RicoRecovery.Tests.TestSupport;
using Xunit;

namespace RicoRecovery.Tests;

public sealed class DeepScannerTests
{
    [Fact]
    public async Task ExcludedRunsAreNotCarved()
    {
        var png = SampleFiles.Png();
        var image = new byte[32 * 512];
        png.CopyTo(image, 4 * 512);
        var options = new DeepScanOptions(0, long.MaxValue, 0, [new DataRun(4 * 512, 512, false)]);

        var result = await new DeepScanner().ScanAsync(new MemoryBlockSource(image), options);

        Assert.Empty(result.Files);
    }

    [Fact]
    public async Task ScanCanBeLimitedToARange()
    {
        var png = SampleFiles.Png();
        var gif = SampleFiles.Gif();
        var image = new byte[64 * 512];
        png.CopyTo(image, 4 * 512);
        gif.CopyTo(image, 20 * 512);

        var result = await new DeepScanner().ScanAsync(new MemoryBlockSource(image), new DeepScanOptions(10 * 512, 30 * 512, 0, []));

        Assert.Equal("gif", Assert.Single(result.Files).Extension);
    }

    [Fact]
    public async Task ActiveFilesFromQuickScanAreExcluded()
    {
        var builder = new Fat16ImageBuilder();
        var png = SampleFiles.Png();
        builder.WriteData(2, png);
        builder.SetFat(2, 0xFFFF);
        builder.AddEntry(0, "PIC.PNG", 0x20, 2, png.Length, false);
        var source = new MemoryBlockSource(builder.Image);

        var quick = await new QuickScanner(DefaultFileSystems.CreateRegistry()).ScanAsync(source);
        var withoutExclusion = await new DeepScanner().ScanAsync(source);
        var withExclusion = await new DeepScanner().ScanAsync(source, DeepScanOptions.ExcludingActiveFiles(quick));

        Assert.Single(withoutExclusion.Files);
        Assert.Empty(withExclusion.Files);
    }

    [Fact]
    public async Task UnknownHeadersAreProfiled()
    {
        var image = new byte[64 * 512];
        var marker = SampleFiles.Bytes(0xA1, 0xB2, 0xC3, 0xD4, 0x11, 0x22, 0x33, 0x44);

        foreach (var sector in new[] { 2, 7, 9, 15, 20 })
        {
            marker.CopyTo(image, sector * 512);
        }

        var result = await new DeepScanner().ScanAsync(new MemoryBlockSource(image));

        Assert.Empty(result.Files);

        var candidate = Assert.Single(result.UnknownCandidates);
        Assert.Equal("A1B2C3D4", candidate.PrefixHex);
        Assert.Equal(5, candidate.Count);
        Assert.Equal(2 * 512L, candidate.FirstOffset);
    }

    [Fact]
    public async Task UnreadableSectorsAreSkippedAndCounted()
    {
        var png = SampleFiles.Png();
        var image = new byte[64 * 512];
        png.CopyTo(image, 4 * 512);
        var source = new FaultyBlockSource(new MemoryBlockSource(image), (50 * 512L, 51 * 512L));

        var result = await new DeepScanner().ScanAsync(source);

        Assert.Equal(1, result.BadSectors);
        Assert.Single(result.Files);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task IdenticalContentIsDetectedAsDuplicate()
    {
        var png = SampleFiles.Png();
        var image = new byte[64 * 512];
        png.CopyTo(image, 4 * 512);
        png.CopyTo(image, 20 * 512);
        var source = new MemoryBlockSource(image);

        var result = await new DeepScanner().ScanAsync(source);
        var index = new DuplicateIndex();
        var duplicates = 0;

        foreach (var file in result.Files)
        {
            var entry = file.ToEntry();
            var hash = await ContentHasher.ComputeAsync(source, entry);

            if (index.Register(hash, entry.Path) is not null)
            {
                duplicates++;
            }
        }

        Assert.Equal(2, result.Files.Count);
        Assert.Equal(1, duplicates);
        Assert.Equal(1, index.UniqueCount);
    }

    [Fact]
    public async Task ContentHashMatchesSha256OfTheFile()
    {
        var png = SampleFiles.Png();
        var image = new byte[32 * 512];
        png.CopyTo(image, 4 * 512);
        var source = new MemoryBlockSource(image);

        var result = await new DeepScanner().ScanAsync(source);
        var hash = await ContentHasher.ComputeAsync(source, Assert.Single(result.Files).ToEntry());

        Assert.Equal(Convert.ToHexString(SHA256.HashData(png)), hash);
    }

    [Fact]
    public async Task SinkReceivesFilesInsteadOfTheResultList()
    {
        var image = new byte[32 * 512];
        SampleFiles.Png().CopyTo(image, 4 * 512);
        var received = new List<CarvedFile>();

        var result = await new DeepScanner().ScanAsync(
            new MemoryBlockSource(image),
            null,
            (file, _) =>
            {
                received.Add(file);

                return ValueTask.CompletedTask;
            });

        Assert.Single(received);
        Assert.Empty(result.Files);
        Assert.Equal(1, result.FilesFound);
    }

    [Fact]
    public async Task ProgressEndsAtOneHundredPercent()
    {
        var image = new byte[64 * 512];
        SampleFiles.Png().CopyTo(image, 4 * 512);
        var progress = new CollectingProgress();

        await new DeepScanner().ScanAsync(new MemoryBlockSource(image), null, null, progress);

        Assert.NotEmpty(progress.Reports);
        Assert.Equal(1.0, progress.Reports[^1].Fraction);
    }

    [Fact]
    public async Task CancelledScanThrows()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DeepScanner().ScanAsync(new MemoryBlockSource(new byte[64 * 512]), null, null, null, cancellation.Token));
    }

    [Fact]
    public async Task ScanDoesNotModifyTheImage()
    {
        var image = new byte[64 * 512];
        SampleFiles.Png().CopyTo(image, 4 * 512);
        SampleFiles.Zip().CopyTo(image, 20 * 512);
        var hashBefore = SHA256.HashData(image);

        await new DeepScanner().ScanAsync(new MemoryBlockSource(image));

        Assert.Equal(hashBefore, SHA256.HashData(image));
    }

    [Fact]
    public void CatalogCoversAllRequiredCategories()
    {
        var categories = SignatureCatalog.All.Select(signature => signature.Category).Distinct().ToList();

        Assert.Contains(FileCategory.Image, categories);
        Assert.Contains(FileCategory.Video, categories);
        Assert.Contains(FileCategory.Audio, categories);
        Assert.Contains(FileCategory.Document, categories);
        Assert.Contains(FileCategory.Archive, categories);
        Assert.Contains(FileCategory.Database, categories);
    }

    [Fact]
    public void CatalogEntriesAreWellFormed()
    {
        var catalog = SignatureCatalog.All;

        Assert.All(catalog, signature =>
        {
            Assert.NotEmpty(signature.Header);
            Assert.Equal(signature.Extension.ToLowerInvariant(), signature.Extension);
            Assert.True(signature.MinSize <= signature.MaxSize);
        });

        Assert.Equal(catalog.Count, catalog.Select(signature => signature.Name).Distinct().Count());
    }

    [Fact]
    public void MatcherPrefersTheMostSpecificHeader()
    {
        var matcher = new SignatureMatcher(SignatureCatalog.All);
        var cr2 = SampleFiles.Bytes(0x49, 0x49, 0x2A, 0x00, 0x10, 0x00, 0x00, 0x00, 0x43, 0x52, 0x02, 0x00);
        var tiff = SampleFiles.Bytes(0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00);

        Assert.Equal("cr2", matcher.Match(cr2)?.Extension);
        Assert.Equal("tif", matcher.Match(tiff)?.Extension);
    }

    private sealed class CollectingProgress : IProgress<ScanProgress>
    {
        public List<ScanProgress> Reports { get; } = [];

        public void Report(ScanProgress value) => Reports.Add(value);
    }
}
