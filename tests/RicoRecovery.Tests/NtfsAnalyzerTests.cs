using System.Security.Cryptography;
using RicoRecovery.Core.Models;
using RicoRecovery.FileSystems;
using RicoRecovery.FileSystems.Fat;
using RicoRecovery.FileSystems.Ntfs;
using RicoRecovery.Tests.TestSupport;
using Xunit;

namespace RicoRecovery.Tests;

public sealed class NtfsAnalyzerTests
{
    private static readonly DateTimeOffset? ExpectedTime = new DateTimeOffset(2024, 5, 17, 12, 30, 10, TimeSpan.Zero);

    [Fact]
    public async Task ResidentFileIsListedWithContentAndTimestamp()
    {
        var image = NtfsImageBuilder.CreateSample();

        var entries = await ScanHelper.EnumerateAsync(new NtfsAnalyzer(), image);
        var entry = entries.Single(candidate => candidate.Path == "/doc.txt");

        Assert.False(entry.IsDeleted);
        Assert.Equal(NtfsImageBuilder.ResidentContent.Length, entry.Size);
        Assert.Equal(ExpectedTime, entry.Modified);
        Assert.Equal(NtfsImageBuilder.ResidentContent, await ScanHelper.ReadContentAsync(image, entry));
    }

    [Fact]
    public async Task NonResidentFileIsReadFromDataRuns()
    {
        var image = NtfsImageBuilder.CreateSample();

        var entries = await ScanHelper.EnumerateAsync(new NtfsAnalyzer(), image);
        var entry = entries.Single(candidate => candidate.Path == "/big.bin");

        Assert.Equal(DataAvailability.Allocated, entry.Availability);
        Assert.Equal(700, entry.Size);
        Assert.Equal(NtfsImageBuilder.BigContent, await ScanHelper.ReadContentAsync(image, entry));
    }

    [Fact]
    public async Task DeletedFileWithFreeClustersIsUnallocatedAndReadable()
    {
        var image = NtfsImageBuilder.CreateSample();

        var entries = await ScanHelper.EnumerateAsync(new NtfsAnalyzer(), image);
        var entry = entries.Single(candidate => candidate.Path == "/old.bin");

        Assert.True(entry.IsDeleted);
        Assert.Equal(DataAvailability.Unallocated, entry.Availability);
        Assert.Equal(NtfsImageBuilder.OldContent, await ScanHelper.ReadContentAsync(image, entry));
    }

    [Fact]
    public async Task DeletedFileWithAllocatedClustersIsReallocated()
    {
        var image = NtfsImageBuilder.CreateSample();

        var entries = await ScanHelper.EnumerateAsync(new NtfsAnalyzer(), image);
        var entry = entries.Single(candidate => candidate.Path == "/reused.bin");

        Assert.True(entry.IsDeleted);
        Assert.Equal(DataAvailability.Reallocated, entry.Availability);
    }

    [Fact]
    public async Task ParentDirectoryPathIsResolved()
    {
        var image = NtfsImageBuilder.CreateSample();

        var entries = await ScanHelper.EnumerateAsync(new NtfsAnalyzer(), image);

        Assert.True(entries.Single(candidate => candidate.Path == "/Ordner").IsDirectory);
        Assert.Equal("xyz"u8.ToArray(), await ScanHelper.ReadContentAsync(image, entries.Single(candidate => candidate.Path == "/Ordner/inner.txt")));
    }

    [Fact]
    public async Task SystemRecordsAreNotListed()
    {
        var image = NtfsImageBuilder.CreateSample();

        var entries = await ScanHelper.EnumerateAsync(new NtfsAnalyzer(), image);

        Assert.Equal(6, entries.Count);
    }

    [Fact]
    public async Task DamagedMftRecordFallsBackToMirror()
    {
        var image = NtfsImageBuilder.CreateSample();
        Array.Clear(image, NtfsImageBuilder.MftCluster * NtfsImageBuilder.ClusterSize, NtfsImageBuilder.RecordSize);

        var entries = await ScanHelper.EnumerateAsync(new NtfsAnalyzer(), image);

        Assert.Equal(6, entries.Count);
    }

    [Fact]
    public async Task VolumeInfoReportsGeometry()
    {
        var image = NtfsImageBuilder.CreateSample();

        var info = await new NtfsAnalyzer().ReadVolumeInfoAsync(new MemoryBlockSource(image), 0);

        Assert.NotNull(info);
        Assert.Equal(FileSystemType.Ntfs, info.Type);
        Assert.Equal(NtfsImageBuilder.ClusterSize, info.ClusterSize);
        Assert.Equal(NtfsImageBuilder.TotalClusters * 512L, info.TotalBytes);
        Assert.Null(info.Note);
    }

    [Fact]
    public async Task ProbeAcceptsNtfsAndRejectsFat()
    {
        var image = NtfsImageBuilder.CreateSample();
        var source = new MemoryBlockSource(image);

        Assert.True(await new NtfsAnalyzer().ProbeAsync(source, 0));
        Assert.False(await new FatAnalyzer(FileSystemType.Fat16).ProbeAsync(source, 0));
        Assert.False(await new FatAnalyzer(FileSystemType.Fat32).ProbeAsync(source, 0));

        var detected = await DefaultFileSystems.CreateRegistry().DetectAsync(source, 0);

        Assert.NotNull(detected);
        Assert.Equal(FileSystemType.Ntfs, detected.Type);
    }

    [Fact]
    public async Task EnumerationDoesNotModifyTheImage()
    {
        var image = NtfsImageBuilder.CreateSample();
        var hashBefore = SHA256.HashData(image);

        await ScanHelper.EnumerateAsync(new NtfsAnalyzer(), image);

        Assert.Equal(hashBefore, SHA256.HashData(image));
    }
}
