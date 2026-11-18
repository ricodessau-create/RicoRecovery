using System.Security.Cryptography;
using RicoRecovery.Core.Models;
using RicoRecovery.Storage.Partitioning;
using RicoRecovery.Tests.TestSupport;
using Xunit;

namespace RicoRecovery.Tests;

public sealed class PartitionTableReaderTests
{
    private static readonly Guid BasicDataType = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
    private static readonly Guid EfiSystemType = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");

    [Fact]
    public async Task MbrWithTwoPrimaryPartitionsIsParsed()
    {
        var image = new byte[4096 * 512];
        DiskImageBuilder.WriteMbrEntry(image, 0, 0, 0x07, 2048, 1000, true);
        DiskImageBuilder.WriteMbrEntry(image, 0, 1, 0x0C, 3100, 900, false);
        DiskImageBuilder.WriteMbrSignature(image, 0);

        var info = await PartitionTableReader.ReadAsync(new MemoryBlockSource(image));

        Assert.Equal(PartitionScheme.Mbr, info.Scheme);
        Assert.Equal(2, info.Partitions.Count);
        Assert.Equal(2048L * 512, info.Partitions[0].StartOffset);
        Assert.Equal(1000L * 512, info.Partitions[0].Length);
        Assert.True(info.Partitions[0].IsBootable);
        Assert.False(info.Partitions[1].IsBootable);
        Assert.Contains("FAT32", info.Partitions[1].TypeDescription);
        Assert.Empty(info.Warnings);
    }

    [Fact]
    public async Task MissingSignatureYieldsNoPartitionTable()
    {
        var image = new byte[2048 * 512];

        var info = await PartitionTableReader.ReadAsync(new MemoryBlockSource(image));

        Assert.Equal(PartitionScheme.None, info.Scheme);
        Assert.Empty(info.Partitions);
        Assert.NotEmpty(info.Warnings);
    }

    [Fact]
    public async Task ExtendedPartitionChainYieldsLogicalPartitions()
    {
        var image = new byte[2048 * 512];
        DiskImageBuilder.WriteMbrEntry(image, 0, 0, 0x0F, 100, 1000, false);
        DiskImageBuilder.WriteMbrSignature(image, 0);

        DiskImageBuilder.WriteMbrEntry(image, 100, 0, 0x83, 1, 50, false);
        DiskImageBuilder.WriteMbrEntry(image, 100, 1, 0x0F, 200, 100, false);
        DiskImageBuilder.WriteMbrSignature(image, 100);

        DiskImageBuilder.WriteMbrEntry(image, 300, 0, 0x07, 1, 40, false);
        DiskImageBuilder.WriteMbrSignature(image, 300);

        var info = await PartitionTableReader.ReadAsync(new MemoryBlockSource(image));

        Assert.Equal(PartitionScheme.Mbr, info.Scheme);
        Assert.Equal(2, info.Partitions.Count);
        Assert.All(info.Partitions, partition => Assert.True(partition.IsLogical));
        Assert.Equal(101L * 512, info.Partitions[0].StartOffset);
        Assert.Equal(301L * 512, info.Partitions[1].StartOffset);
    }

    [Fact]
    public async Task PartitionBeyondImageEndProducesWarning()
    {
        var image = new byte[1024 * 512];
        DiskImageBuilder.WriteMbrEntry(image, 0, 0, 0x07, 512, 4000, false);
        DiskImageBuilder.WriteMbrSignature(image, 0);

        var info = await PartitionTableReader.ReadAsync(new MemoryBlockSource(image));

        Assert.Single(info.Partitions);
        Assert.NotEmpty(info.Warnings);
    }

    [Fact]
    public async Task GptPartitionsAreParsedWithNames()
    {
        var image = DiskImageBuilder.CreateGptImage(
            16384,
            new GptPartitionSpec(EfiSystemType, 40, 999, "System"),
            new GptPartitionSpec(BasicDataType, 1000, 4999, "Daten"));

        var info = await PartitionTableReader.ReadAsync(new MemoryBlockSource(image));

        Assert.Equal(PartitionScheme.Gpt, info.Scheme);
        Assert.False(info.UsedBackupTable);
        Assert.Equal(2, info.Partitions.Count);
        Assert.Equal("System", info.Partitions[0].Name);
        Assert.Equal(40L * 512, info.Partitions[0].StartOffset);
        Assert.Equal(960L * 512, info.Partitions[0].Length);
        Assert.Equal("Daten", info.Partitions[1].Name);
        Assert.Empty(info.Warnings);
    }

    [Fact]
    public async Task CorruptPrimaryHeaderFallsBackToBackupGpt()
    {
        var image = DiskImageBuilder.CreateGptImage(
            16384,
            new GptPartitionSpec(BasicDataType, 40, 999, "Daten"));

        image[512 + 40] ^= 0xFF;

        var info = await PartitionTableReader.ReadAsync(new MemoryBlockSource(image));

        Assert.Equal(PartitionScheme.Gpt, info.Scheme);
        Assert.True(info.UsedBackupTable);
        Assert.Single(info.Partitions);
        Assert.NotEmpty(info.Warnings);
    }

    [Fact]
    public async Task CorruptEntriesInBothTablesYieldNoPartitions()
    {
        const int sectors = 16384;

        var image = DiskImageBuilder.CreateGptImage(
            sectors,
            new GptPartitionSpec(BasicDataType, 40, 999, "Daten"));

        image[2 * 512 + 5] ^= 0xFF;
        image[(int)(DiskImageBuilder.BackupEntriesLba(sectors) * 512) + 5] ^= 0xFF;

        var info = await PartitionTableReader.ReadAsync(new MemoryBlockSource(image));

        Assert.Empty(info.Partitions);
        Assert.Contains(info.Warnings, warning => warning.Contains("Prüfsumme"));
    }

    [Fact]
    public async Task ReadingDoesNotModifyTheImage()
    {
        var image = DiskImageBuilder.CreateGptImage(
            16384,
            new GptPartitionSpec(BasicDataType, 40, 999, "Daten"));

        var hashBefore = SHA256.HashData(image);

        await PartitionTableReader.ReadAsync(new MemoryBlockSource(image));

        Assert.Equal(hashBefore, SHA256.HashData(image));
    }
}
