using System.Security.Cryptography;
using RicoRecovery.Core.Models;
using RicoRecovery.FileSystems;
using RicoRecovery.FileSystems.Ext;
using RicoRecovery.Tests.TestSupport;
using Xunit;

namespace RicoRecovery.Tests;

public sealed class ExtAnalyzerTests
{
    [Fact]
    public async Task ActiveEntriesAreListedWithContent()
    {
        var image = Ext2ImageBuilder.CreateSample().Image;

        var entries = await ScanHelper.EnumerateAsync(new ExtAnalyzer(FileSystemType.Ext2), image);

        var hello = entries.Single(entry => entry.Path == "/hello.txt");
        Assert.False(hello.IsDeleted);
        Assert.Equal(DataAvailability.Allocated, hello.Availability);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(Ext2ImageBuilder.ModifiedTime), hello.Modified);
        Assert.Equal(Ext2ImageBuilder.HelloContent, await ScanHelper.ReadContentAsync(image, hello));

        Assert.True(entries.Single(entry => entry.Path == "/sub").IsDirectory);
        Assert.Equal("abc"u8.ToArray(), await ScanHelper.ReadContentAsync(image, entries.Single(entry => entry.Path == "/sub/in.txt")));
    }

    [Fact]
    public async Task DeletedInodeIsListedWithNameFromDirectorySlack()
    {
        var image = Ext2ImageBuilder.CreateSample().Image;

        var entries = await ScanHelper.EnumerateAsync(new ExtAnalyzer(FileSystemType.Ext2), image);
        var gone = entries.Single(entry => entry.IsDeleted);

        Assert.Equal("gone.txt", gone.Name);
        Assert.Equal("/gone.txt", gone.Path);
        Assert.Equal(DataAvailability.Unallocated, gone.Availability);
        Assert.Equal(Ext2ImageBuilder.GoneContent, await ScanHelper.ReadContentAsync(image, gone));
    }

    [Fact]
    public async Task DeletedInodeWithReusedBlocksIsReallocated()
    {
        var image = Ext2ImageBuilder.CreateSample().Image;
        image[3 * Ext2ImageBuilder.BlockSize + 1] |= 0x30;

        var entries = await ScanHelper.EnumerateAsync(new ExtAnalyzer(FileSystemType.Ext2), image);

        Assert.Equal(DataAvailability.Reallocated, entries.Single(entry => entry.IsDeleted).Availability);
    }

    [Fact]
    public async Task ProbeDistinguishesExtGenerations()
    {
        var source = new MemoryBlockSource(Ext2ImageBuilder.CreateSample().Image);

        Assert.True(await new ExtAnalyzer(FileSystemType.Ext2).ProbeAsync(source, 0));
        Assert.False(await new ExtAnalyzer(FileSystemType.Ext3).ProbeAsync(source, 0));
        Assert.False(await new ExtAnalyzer(FileSystemType.Ext4).ProbeAsync(source, 0));

        var detected = await DefaultFileSystems.CreateRegistry().DetectAsync(source, 0);

        Assert.NotNull(detected);
        Assert.Equal(FileSystemType.Ext2, detected.Type);
    }

    [Fact]
    public async Task JournalFeatureIsClassifiedAsExt3()
    {
        var image = Ext2ImageBuilder.CreateSample().Image;
        ImageWriter.U32(image, 1024 + 92, 0x4);

        Assert.True(await new ExtAnalyzer(FileSystemType.Ext3).ProbeAsync(new MemoryBlockSource(image), 0));
    }

    [Fact]
    public async Task ExtentFeatureIsClassifiedAsExt4()
    {
        var image = Ext2ImageBuilder.CreateSample().Image;
        ImageWriter.U32(image, 1024 + 96, 0x42);

        Assert.True(await new ExtAnalyzer(FileSystemType.Ext4).ProbeAsync(new MemoryBlockSource(image), 0));
    }

    [Fact]
    public async Task VolumeInfoReportsLabelAndGeometry()
    {
        var image = Ext2ImageBuilder.CreateSample().Image;

        var info = await new ExtAnalyzer(FileSystemType.Ext2).ReadVolumeInfoAsync(new MemoryBlockSource(image), 0);

        Assert.NotNull(info);
        Assert.Equal("EXTTEST", info.Label);
        Assert.Equal(Ext2ImageBuilder.BlockSize, info.ClusterSize);
        Assert.Equal(Ext2ImageBuilder.TotalBlocks * 1024L, info.TotalBytes);
        Assert.Null(info.Note);
    }

    [Fact]
    public async Task EnumerationDoesNotModifyTheImage()
    {
        var image = Ext2ImageBuilder.CreateSample().Image;
        var hashBefore = SHA256.HashData(image);

        await ScanHelper.EnumerateAsync(new ExtAnalyzer(FileSystemType.Ext2), image);

        Assert.Equal(hashBefore, SHA256.HashData(image));
    }

    [Fact]
    public void ConstructorRejectsOtherFileSystemTypes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExtAnalyzer(FileSystemType.Fat32));
    }
}
