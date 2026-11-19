using System.Security.Cryptography;
using System.Text;
using RicoRecovery.Core.Models;
using RicoRecovery.FileSystems;
using RicoRecovery.FileSystems.Fat;
using RicoRecovery.Tests.TestSupport;
using Xunit;

namespace RicoRecovery.Tests;

public sealed class FatAnalyzerTests
{
    private static readonly DateTimeOffset? ExpectedTime = new DateTimeOffset(2024, 5, 17, 12, 30, 10, TimeSpan.Zero);

    [Fact]
    public async Task ActiveFileIsListedWithContent()
    {
        var builder = new Fat16ImageBuilder();
        var data = Encoding.ASCII.GetBytes("Hallo Welt");
        builder.WriteData(2, data);
        builder.SetFat(2, 0xFFFF);
        builder.AddEntry(0, "HELLO.TXT", 0x20, 2, data.Length, false);

        var entries = await ScanHelper.EnumerateAsync(new FatAnalyzer(FileSystemType.Fat16), builder.Image);

        var entry = Assert.Single(entries);
        Assert.Equal("HELLO.TXT", entry.Name);
        Assert.Equal("/HELLO.TXT", entry.Path);
        Assert.Equal(10, entry.Size);
        Assert.False(entry.IsDeleted);
        Assert.Equal(DataAvailability.Allocated, entry.Availability);
        Assert.Equal(ExpectedTime, entry.Modified);
        Assert.Equal(data, await ScanHelper.ReadContentAsync(builder.Image, entry));
    }

    [Fact]
    public async Task LongFileNameIsResolved()
    {
        var builder = new Fat16ImageBuilder();
        var data = Encoding.ASCII.GetBytes("abc");
        builder.WriteData(2, data);
        builder.SetFat(2, 0xFFFF);
        builder.AddEntry(0, "LANGED~1.TXT", 0x20, 2, data.Length, false, "Lange Datei.txt");

        var entries = await ScanHelper.EnumerateAsync(new FatAnalyzer(FileSystemType.Fat16), builder.Image);

        var entry = Assert.Single(entries);
        Assert.Equal("Lange Datei.txt", entry.Name);
    }

    [Fact]
    public async Task DeletedFileWithFreeClustersIsUnallocatedAndReadable()
    {
        var builder = new Fat16ImageBuilder();
        var data = ScanHelper.Pattern(600);
        builder.WriteData(5, data);
        builder.AddEntry(0, "GONE.BIN", 0x20, 5, data.Length, true);

        var entries = await ScanHelper.EnumerateAsync(new FatAnalyzer(FileSystemType.Fat16), builder.Image);

        var entry = Assert.Single(entries);
        Assert.True(entry.IsDeleted);
        Assert.Equal("_ONE.BIN", entry.Name);
        Assert.Equal(DataAvailability.Unallocated, entry.Availability);
        Assert.Equal(data, await ScanHelper.ReadContentAsync(builder.Image, entry));
    }

    [Fact]
    public async Task DeletedFileWithOneReusedClusterIsPartiallyReallocated()
    {
        var builder = new Fat16ImageBuilder();
        var data = ScanHelper.Pattern(600);
        builder.WriteData(5, data);
        builder.SetFat(6, 0xFFFF);
        builder.AddEntry(0, "GONE.BIN", 0x20, 5, data.Length, true);

        var entries = await ScanHelper.EnumerateAsync(new FatAnalyzer(FileSystemType.Fat16), builder.Image);

        Assert.Equal(DataAvailability.PartiallyReallocated, Assert.Single(entries).Availability);
    }

    [Fact]
    public async Task DeletedFileWithAllClustersReusedIsReallocated()
    {
        var builder = new Fat16ImageBuilder();
        var data = ScanHelper.Pattern(600);
        builder.WriteData(5, data);
        builder.SetFat(5, 0xFFFF);
        builder.SetFat(6, 0xFFFF);
        builder.AddEntry(0, "GONE.BIN", 0x20, 5, data.Length, true);

        var entries = await ScanHelper.EnumerateAsync(new FatAnalyzer(FileSystemType.Fat16), builder.Image);

        Assert.Equal(DataAvailability.Reallocated, Assert.Single(entries).Availability);
    }

    [Fact]
    public async Task DeletedFileWithLongNameKeepsOriginalName()
    {
        var builder = new Fat16ImageBuilder();
        var data = Encoding.ASCII.GetBytes("xyz");
        builder.WriteData(8, data);
        builder.AddEntry(0, "BERICH~1.TXT", 0x20, 8, data.Length, true, "Bericht Quartal.txt");

        var entries = await ScanHelper.EnumerateAsync(new FatAnalyzer(FileSystemType.Fat16), builder.Image);

        var entry = Assert.Single(entries);
        Assert.True(entry.IsDeleted);
        Assert.Equal("Bericht Quartal.txt", entry.Name);
    }

    [Fact]
    public async Task FragmentedFileFollowsTheFatChain()
    {
        var builder = new Fat16ImageBuilder();
        var data = ScanHelper.Pattern(800);
        builder.WriteData(7, data[..512]);
        builder.WriteData(9, data[512..]);
        builder.SetFat(7, 9);
        builder.SetFat(9, 0xFFFF);
        builder.AddEntry(0, "FRAG.BIN", 0x20, 7, data.Length, false);

        var entries = await ScanHelper.EnumerateAsync(new FatAnalyzer(FileSystemType.Fat16), builder.Image);

        var entry = Assert.Single(entries);
        Assert.Equal(2, entry.DataRuns.Count);
        Assert.Equal(data, await ScanHelper.ReadContentAsync(builder.Image, entry));
    }

    [Fact]
    public async Task SubdirectoryContentsAreListedWithFullPath()
    {
        var builder = new Fat16ImageBuilder();
        var data = Encoding.ASCII.GetBytes("abc");
        builder.SetFat(3, 0xFFFF);
        builder.SetFat(4, 0xFFFF);
        builder.WriteData(4, data);
        builder.AddEntry(0, "DOCS", 0x10, 3, 0, false);
        builder.AddEntry(3, "A.TXT", 0x20, 4, data.Length, false);

        var entries = await ScanHelper.EnumerateAsync(new FatAnalyzer(FileSystemType.Fat16), builder.Image);

        Assert.Equal(2, entries.Count);
        Assert.True(entries.Single(entry => entry.Path == "/DOCS").IsDirectory);
        Assert.Equal(data, await ScanHelper.ReadContentAsync(builder.Image, entries.Single(entry => entry.Path == "/DOCS/A.TXT")));
    }

    [Fact]
    public async Task DeletedOnlyOptionSkipsActiveEntries()
    {
        var builder = new Fat16ImageBuilder();
        builder.SetFat(2, 0xFFFF);
        builder.AddEntry(0, "KEEP.TXT", 0x20, 2, 3, false);
        builder.AddEntry(0, "GONE.TXT", 0x20, 5, 3, true);
        var entries = new List<FileSystemEntry>();

        await foreach (var entry in new FatAnalyzer(FileSystemType.Fat16).EnumerateAsync(new MemoryBlockSource(builder.Image), 0, FileSystemScanOptions.DeletedOnly))
        {
            entries.Add(entry);
        }

        Assert.True(Assert.Single(entries).IsDeleted);
    }

    [Fact]
    public async Task ProbeDistinguishesFat16FromFat32AndOthers()
    {
        var image = new Fat16ImageBuilder().Image;
        var source = new MemoryBlockSource(image);

        Assert.True(await new FatAnalyzer(FileSystemType.Fat16).ProbeAsync(source, 0));
        Assert.False(await new FatAnalyzer(FileSystemType.Fat32).ProbeAsync(source, 0));

        var detected = await DefaultFileSystems.CreateRegistry().DetectAsync(source, 0);

        Assert.NotNull(detected);
        Assert.Equal(FileSystemType.Fat16, detected.Type);
    }

    [Fact]
    public async Task VolumeInfoReportsLabelAndGeometry()
    {
        var image = new Fat16ImageBuilder().Image;

        var info = await new FatAnalyzer(FileSystemType.Fat16).ReadVolumeInfoAsync(new MemoryBlockSource(image), 0);

        Assert.NotNull(info);
        Assert.Equal("TESTVOL", info.Label);
        Assert.Equal(512, info.ClusterSize);
        Assert.Equal(Fat16ImageBuilder.TotalSectors * 512L, info.TotalBytes);
        Assert.Null(info.Note);
    }

    [Fact]
    public async Task EnumerationDoesNotModifyTheImage()
    {
        var builder = new Fat16ImageBuilder();
        builder.SetFat(2, 0xFFFF);
        builder.AddEntry(0, "KEEP.TXT", 0x20, 2, 3, false);
        builder.AddEntry(0, "GONE.TXT", 0x20, 5, 3, true);
        var hashBefore = SHA256.HashData(builder.Image);

        await ScanHelper.EnumerateAsync(new FatAnalyzer(FileSystemType.Fat16), builder.Image);

        Assert.Equal(hashBefore, SHA256.HashData(builder.Image));
    }

    [Fact]
    public async Task Fat32VolumeIsDetectedAndListed()
    {
        const int totalSectors = 70000;
        const int reservedSectors = 32;
        const int fatSectors = 600;
        const int dataStartSector = reservedSectors + 2 * fatSectors;

        var image = new byte[totalSectors * 512];

        ImageWriter.U16(image, 11, 512);
        image[13] = 1;
        ImageWriter.U16(image, 14, reservedSectors);
        image[16] = 2;
        ImageWriter.U32(image, 32, totalSectors);
        ImageWriter.U32(image, 36, fatSectors);
        ImageWriter.U32(image, 44, 2);
        image[66] = 0x29;
        Encoding.ASCII.GetBytes("FAT32VOL   ").CopyTo(image, 71);
        image[510] = 0x55;
        image[511] = 0xAA;

        ImageWriter.U32(image, reservedSectors * 512 + 0, 0x0FFFFFF8);
        ImageWriter.U32(image, reservedSectors * 512 + 4, 0x0FFFFFFF);
        ImageWriter.U32(image, reservedSectors * 512 + 8, 0x0FFFFFFF);
        ImageWriter.U32(image, reservedSectors * 512 + 12, 0x0FFFFFFF);

        var rootOffset = dataStartSector * 512;
        Encoding.ASCII.GetBytes("A       TXT").CopyTo(image, rootOffset);
        image[rootOffset + 11] = 0x20;
        ImageWriter.U16(image, rootOffset + 26, 3);
        ImageWriter.U32(image, rootOffset + 28, 4);
        Encoding.ASCII.GetBytes("Test").CopyTo(image, (dataStartSector + 1) * 512);

        var source = new MemoryBlockSource(image);

        Assert.True(await new FatAnalyzer(FileSystemType.Fat32).ProbeAsync(source, 0));
        Assert.False(await new FatAnalyzer(FileSystemType.Fat16).ProbeAsync(source, 0));

        var entries = await ScanHelper.EnumerateAsync(new FatAnalyzer(FileSystemType.Fat32), image);

        var entry = Assert.Single(entries);
        Assert.Equal("/A.TXT", entry.Path);
        Assert.Equal("Test"u8.ToArray(), await ScanHelper.ReadContentAsync(image, entry));
    }

    [Fact]
    public void ConstructorRejectsOtherFileSystemTypes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FatAnalyzer(FileSystemType.Ntfs));
    }
}
