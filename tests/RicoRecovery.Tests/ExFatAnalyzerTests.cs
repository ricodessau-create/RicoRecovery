using System.Security.Cryptography;
using RicoRecovery.Core.Models;
using RicoRecovery.FileSystems;
using RicoRecovery.FileSystems.ExFat;
using RicoRecovery.Tests.TestSupport;
using Xunit;

namespace RicoRecovery.Tests;

public sealed class ExFatAnalyzerTests
{
    private static readonly DateTimeOffset? ExpectedTime = new DateTimeOffset(2024, 5, 17, 12, 30, 10, TimeSpan.Zero);

    [Fact]
    public async Task ActiveFileIsListedWithContentAndTimestamp()
    {
        var builder = new ExFatImageBuilder();
        var data = ScanHelper.Pattern(5000);
        builder.AddFile(ExFatImageBuilder.RootCluster, "report.txt", 5, data, false, true);

        var entries = await ScanHelper.EnumerateAsync(new ExFatAnalyzer(), builder.Image);

        var entry = Assert.Single(entries);
        Assert.Equal("/report.txt", entry.Path);
        Assert.Equal(5000, entry.Size);
        Assert.Equal(DataAvailability.Allocated, entry.Availability);
        Assert.Equal(ExpectedTime, entry.Modified);
        Assert.Equal(data, await ScanHelper.ReadContentAsync(builder.Image, entry));
    }

    [Fact]
    public async Task LongNameAcrossSeveralNameEntriesIsAssembled()
    {
        var builder = new ExFatImageBuilder();
        builder.AddFile(ExFatImageBuilder.RootCluster, "Ein sehr langer Dateiname fuer exFAT.dat", 5, ScanHelper.Pattern(10), false, true);

        var entries = await ScanHelper.EnumerateAsync(new ExFatAnalyzer(), builder.Image);

        Assert.Equal("Ein sehr langer Dateiname fuer exFAT.dat", Assert.Single(entries).Name);
    }

    [Fact]
    public async Task DeletedFileWithFreeBitmapBitsIsUnallocatedAndReadable()
    {
        var builder = new ExFatImageBuilder();
        var data = ScanHelper.Pattern(100);
        builder.AddFile(ExFatImageBuilder.RootCluster, "old.bin", 20, data, true, false);

        var entries = await ScanHelper.EnumerateAsync(new ExFatAnalyzer(), builder.Image);

        var entry = Assert.Single(entries);
        Assert.True(entry.IsDeleted);
        Assert.Equal("old.bin", entry.Name);
        Assert.Equal(DataAvailability.Unallocated, entry.Availability);
        Assert.Equal(data, await ScanHelper.ReadContentAsync(builder.Image, entry));
    }

    [Fact]
    public async Task DeletedFileWithAllocatedBitmapBitsIsReallocated()
    {
        var builder = new ExFatImageBuilder();
        builder.AddFile(ExFatImageBuilder.RootCluster, "old.bin", 20, ScanHelper.Pattern(100), true, true);

        var entries = await ScanHelper.EnumerateAsync(new ExFatAnalyzer(), builder.Image);

        Assert.Equal(DataAvailability.Reallocated, Assert.Single(entries).Availability);
    }

    [Fact]
    public async Task SubdirectoryContentsAreListedWithFullPath()
    {
        var builder = new ExFatImageBuilder();
        var data = ScanHelper.Pattern(30);
        builder.AddDirectory(ExFatImageBuilder.RootCluster, "Ordner", 7);
        builder.AddFile(7, "inner.txt", 8, data, false, true);

        var entries = await ScanHelper.EnumerateAsync(new ExFatAnalyzer(), builder.Image);

        Assert.Equal(2, entries.Count);
        Assert.True(entries.Single(entry => entry.Path == "/Ordner").IsDirectory);
        Assert.Equal(data, await ScanHelper.ReadContentAsync(builder.Image, entries.Single(entry => entry.Path == "/Ordner/inner.txt")));
    }

    [Fact]
    public async Task VolumeInfoReportsLabelAndGeometry()
    {
        var image = new ExFatImageBuilder().Image;

        var info = await new ExFatAnalyzer().ReadVolumeInfoAsync(new MemoryBlockSource(image), 0);

        Assert.NotNull(info);
        Assert.Equal("TEST", info.Label);
        Assert.Equal(ExFatImageBuilder.ClusterSize, info.ClusterSize);
        Assert.Equal(ExFatImageBuilder.TotalSectors * 512L, info.TotalBytes);
        Assert.Null(info.Note);
    }

    [Fact]
    public async Task DamagedBootSectorFallsBackToBackup()
    {
        var image = new ExFatImageBuilder().Image;
        Array.Clear(image, 0, 512);
        var source = new MemoryBlockSource(image);

        Assert.True(await new ExFatAnalyzer().ProbeAsync(source, 0));

        var info = await new ExFatAnalyzer().ReadVolumeInfoAsync(source, 0);

        Assert.NotNull(info);
        Assert.Contains("Sicherungskopie", info.Note);
    }

    [Fact]
    public async Task RegistryDetectsExFat()
    {
        var image = new ExFatImageBuilder().Image;

        var detected = await DefaultFileSystems.CreateRegistry().DetectAsync(new MemoryBlockSource(image), 0);

        Assert.NotNull(detected);
        Assert.Equal(FileSystemType.ExFat, detected.Type);
    }

    [Fact]
    public async Task EnumerationDoesNotModifyTheImage()
    {
        var builder = new ExFatImageBuilder();
        builder.AddFile(ExFatImageBuilder.RootCluster, "keep.txt", 5, ScanHelper.Pattern(50), false, true);
        builder.AddFile(ExFatImageBuilder.RootCluster, "gone.txt", 20, ScanHelper.Pattern(50), true, false);
        var hashBefore = SHA256.HashData(builder.Image);

        await ScanHelper.EnumerateAsync(new ExFatAnalyzer(), builder.Image);

        Assert.Equal(hashBefore, SHA256.HashData(builder.Image));
    }
}
