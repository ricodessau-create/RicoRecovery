using System.Security.Cryptography;
using RicoRecovery.Core.Models;
using RicoRecovery.FileSystems;
using RicoRecovery.Recovery.Partitions;
using RicoRecovery.Tests.TestSupport;
using Xunit;

namespace RicoRecovery.Tests;

public sealed class LostPartitionScannerTests
{
    private const int MiB = 1024 * 1024;

    [Fact]
    public async Task FileSystemsWithoutPartitionTableAreFoundWithTheirBoundaries()
    {
        var disk = CreateDisk();

        var result = await new LostPartitionScanner(DefaultFileSystems.CreateRegistry()).ScanAsync(new MemoryBlockSource(disk));

        Assert.Equal(4, result.Partitions.Count);

        var fat = result.Partitions[0];
        Assert.Equal(1L * MiB, fat.StartOffset);
        Assert.Equal(Fat16ImageBuilder.TotalSectors * 512L, fat.Length);
        Assert.Equal(FileSystemType.Fat16, fat.Type);
        Assert.Equal("TESTVOL", fat.Label);

        var ntfs = result.Partitions[1];
        Assert.Equal(6L * MiB, ntfs.StartOffset);
        Assert.Equal(NtfsImageBuilder.TotalClusters * 512L + 512, ntfs.Length);
        Assert.Equal(FileSystemType.Ntfs, ntfs.Type);

        var exFat = result.Partitions[2];
        Assert.Equal(9L * MiB, exFat.StartOffset);
        Assert.Equal(ExFatImageBuilder.TotalSectors * 512L, exFat.Length);
        Assert.Equal(FileSystemType.ExFat, exFat.Type);
        Assert.Equal("TEST", exFat.Label);

        var ext = result.Partitions[3];
        Assert.Equal(11L * MiB, ext.StartOffset);
        Assert.Equal(Ext2ImageBuilder.TotalBlocks * 1024L, ext.Length);
        Assert.Equal(FileSystemType.Ext2, ext.Type);
        Assert.Equal("EXTTEST", ext.Label);

        Assert.All(result.Partitions, partition => Assert.False(partition.IsInPartitionTable));
    }

    [Fact]
    public async Task PartitionsMissingFromTheTableAreMarked()
    {
        var disk = CreateDisk();
        DiskImageBuilder.WriteMbrEntry(disk, 0, 0, 0x06, 2048, 8192, true);
        DiskImageBuilder.WriteMbrSignature(disk, 0);

        var result = await new LostPartitionScanner(DefaultFileSystems.CreateRegistry()).ScanAsync(new MemoryBlockSource(disk));

        Assert.Equal(PartitionScheme.Mbr, result.PartitionTable.Scheme);
        Assert.Equal(4, result.Partitions.Count);
        Assert.True(result.Partitions[0].IsInPartitionTable);
        Assert.False(result.Partitions[1].IsInPartitionTable);
        Assert.Contains("fehlt", result.Partitions[1].Note);
    }

    [Fact]
    public async Task BackupBootSectorsInsideAFileSystemAreNotReportedAsPartitions()
    {
        var disk = new byte[3 * MiB];
        new ExFatImageBuilder().Image.CopyTo(disk, 1 * MiB);

        var result = await new LostPartitionScanner(DefaultFileSystems.CreateRegistry()).ScanAsync(new MemoryBlockSource(disk));

        var partition = Assert.Single(result.Partitions);
        Assert.Equal(1L * MiB, partition.StartOffset);
    }

    [Fact]
    public async Task EmptyDiskYieldsNoPartitions()
    {
        var result = await new LostPartitionScanner(DefaultFileSystems.CreateRegistry()).ScanAsync(new MemoryBlockSource(new byte[2 * MiB]));

        Assert.Empty(result.Partitions);
    }

    [Fact]
    public async Task ProgressEndsAtOneHundredPercent()
    {
        var progress = new CollectingProgress();

        await new LostPartitionScanner(DefaultFileSystems.CreateRegistry()).ScanAsync(new MemoryBlockSource(CreateDisk()), progress);

        Assert.NotEmpty(progress.Reports);
        Assert.Equal(1.0, progress.Reports[^1].Fraction);
    }

    [Fact]
    public async Task CancelledScanThrows()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LostPartitionScanner(DefaultFileSystems.CreateRegistry()).ScanAsync(new MemoryBlockSource(CreateDisk()), null, cancellation.Token));
    }

    [Fact]
    public async Task ScanDoesNotModifyTheDisk()
    {
        var disk = CreateDisk();
        var hashBefore = SHA256.HashData(disk);

        await new LostPartitionScanner(DefaultFileSystems.CreateRegistry()).ScanAsync(new MemoryBlockSource(disk));

        Assert.Equal(hashBefore, SHA256.HashData(disk));
    }

    private static byte[] CreateDisk()
    {
        var disk = new byte[12 * MiB];

        new Fat16ImageBuilder().Image.CopyTo(disk, 1 * MiB);
        NtfsImageBuilder.CreateSample().CopyTo(disk, 6 * MiB);
        new ExFatImageBuilder().Image.CopyTo(disk, 9 * MiB);
        Ext2ImageBuilder.CreateSample().Image.CopyTo(disk, 11 * MiB);

        return disk;
    }

    private sealed class CollectingProgress : IProgress<ScanProgress>
    {
        public List<ScanProgress> Reports { get; } = [];

        public void Report(ScanProgress value) => Reports.Add(value);
    }
}
