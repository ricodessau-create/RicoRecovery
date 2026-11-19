using System.Security.Cryptography;
using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;
using RicoRecovery.FileSystems;
using RicoRecovery.Recovery;
using RicoRecovery.Tests.TestSupport;
using Xunit;

namespace RicoRecovery.Tests;

public sealed class QuickScannerTests
{
    private const int PartitionStartSector = 2048;

    [Fact]
    public async Task VolumeWithoutPartitionTableIsScannedDirectly()
    {
        var builder = new Fat16ImageBuilder();
        var data = ScanHelper.Pattern(300);
        builder.WriteData(2, data);
        builder.SetFat(2, 0xFFFF);
        builder.AddEntry(0, "KEEP.BIN", 0x20, 2, data.Length, false);
        builder.AddEntry(0, "GONE.BIN", 0x20, 5, 10, true);

        var result = await new QuickScanner(DefaultFileSystems.CreateRegistry()).ScanAsync(new MemoryBlockSource(builder.Image));

        var volume = Assert.Single(result.Volumes);
        Assert.Null(volume.Partition);
        Assert.Equal(FileSystemType.Fat16, volume.Info.Type);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(2, result.FilesFound);
        Assert.Equal(1, result.DeletedFound);
    }

    [Fact]
    public async Task PartitionedDiskIsScannedThroughItsPartitionTable()
    {
        var builder = new Fat16ImageBuilder();
        var data = ScanHelper.Pattern(300);
        builder.WriteData(2, data);
        builder.SetFat(2, 0xFFFF);
        builder.AddEntry(0, "KEEP.BIN", 0x20, 2, data.Length, false);

        var disk = CreatePartitionedDisk(builder.Image);

        var result = await new QuickScanner(DefaultFileSystems.CreateRegistry()).ScanAsync(new MemoryBlockSource(disk));

        Assert.Equal(PartitionScheme.Mbr, result.PartitionTable.Scheme);

        var volume = Assert.Single(result.Volumes);
        Assert.NotNull(volume.Partition);
        Assert.Equal(PartitionStartSector * 512L, volume.Offset);

        var item = Assert.Single(result.Items);
        Assert.Equal("/KEEP.BIN", item.Entry.Path);
        Assert.Equal(data, await ScanHelper.ReadContentAsync(disk, item.Entry));
    }

    [Fact]
    public async Task EmptyDiskYieldsWarningAndNoVolumes()
    {
        var result = await new QuickScanner(DefaultFileSystems.CreateRegistry()).ScanAsync(new MemoryBlockSource(new byte[1024 * 1024]));

        Assert.Empty(result.Volumes);
        Assert.Empty(result.Items);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task UnrecognizedPartitionIsReportedAsRaw()
    {
        var disk = new byte[10240 * 512];
        DiskImageBuilder.WriteMbrEntry(disk, 0, 0, 0x07, PartitionStartSector, 8192, true);
        DiskImageBuilder.WriteMbrSignature(disk, 0);

        var result = await new QuickScanner(DefaultFileSystems.CreateRegistry()).ScanAsync(new MemoryBlockSource(disk));

        Assert.Empty(result.Volumes);
        Assert.Contains(result.Warnings, warning => warning.Contains("RAW"));
    }

    [Fact]
    public async Task SinkReceivesItemsInsteadOfTheResultList()
    {
        var builder = new Fat16ImageBuilder();
        builder.SetFat(2, 0xFFFF);
        builder.AddEntry(0, "KEEP.BIN", 0x20, 2, 10, false);
        var received = new List<ScanItem>();

        var result = await new QuickScanner(DefaultFileSystems.CreateRegistry()).ScanAsync(
            new MemoryBlockSource(builder.Image),
            (item, _) =>
            {
                received.Add(item);

                return ValueTask.CompletedTask;
            });

        Assert.Single(received);
        Assert.Empty(result.Items);
        Assert.Equal(1, result.FilesFound);
    }

    [Fact]
    public async Task ProgressEndsAtOneHundredPercent()
    {
        var builder = new Fat16ImageBuilder();
        builder.SetFat(2, 0xFFFF);
        builder.AddEntry(0, "KEEP.BIN", 0x20, 2, 10, false);
        var progress = new CollectingProgress();

        await new QuickScanner(DefaultFileSystems.CreateRegistry()).ScanAsync(new MemoryBlockSource(CreatePartitionedDisk(builder.Image)), null, progress);

        Assert.NotEmpty(progress.Reports);
        Assert.Equal(1.0, progress.Reports[^1].Fraction);
    }

    [Fact]
    public async Task LogReceivesStartAndEndMessages()
    {
        var builder = new Fat16ImageBuilder();
        var logger = new CollectingLogger();

        await new QuickScanner(DefaultFileSystems.CreateRegistry(), logger).ScanAsync(new MemoryBlockSource(builder.Image));

        Assert.Contains(logger.Entries, entry => entry.Message.StartsWith("Schnellscan gestartet", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Message.StartsWith("Schnellscan beendet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancelledScanThrows()
    {
        var builder = new Fat16ImageBuilder();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new QuickScanner(DefaultFileSystems.CreateRegistry()).ScanAsync(new MemoryBlockSource(builder.Image), null, null, cancellation.Token));
    }

    [Fact]
    public async Task ScanDoesNotModifyTheDisk()
    {
        var builder = new Fat16ImageBuilder();
        builder.SetFat(2, 0xFFFF);
        builder.AddEntry(0, "KEEP.BIN", 0x20, 2, 10, false);
        builder.AddEntry(0, "GONE.BIN", 0x20, 5, 10, true);
        var disk = CreatePartitionedDisk(builder.Image);
        var hashBefore = SHA256.HashData(disk);

        await new QuickScanner(DefaultFileSystems.CreateRegistry()).ScanAsync(new MemoryBlockSource(disk));

        Assert.Equal(hashBefore, SHA256.HashData(disk));
    }

    private static byte[] CreatePartitionedDisk(byte[] volumeImage)
    {
        var disk = new byte[(PartitionStartSector + 8192) * 512];

        DiskImageBuilder.WriteMbrEntry(disk, 0, 0, 0x06, PartitionStartSector, 8192, true);
        DiskImageBuilder.WriteMbrSignature(disk, 0);
        volumeImage.CopyTo(disk, PartitionStartSector * 512);

        return disk;
    }

    private sealed class CollectingProgress : IProgress<ScanProgress>
    {
        public List<ScanProgress> Reports { get; } = [];

        public void Report(ScanProgress value) => Reports.Add(value);
    }

    private sealed class CollectingLogger : IScanLogger
    {
        public List<ScanLogEntry> Entries { get; } = [];

        public Task WriteAsync(ScanLogEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);

            return Task.CompletedTask;
        }
    }
}
