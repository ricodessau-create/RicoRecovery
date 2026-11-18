using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;
using RicoRecovery.FileSystems;
using Xunit;

namespace RicoRecovery.Tests;

public sealed class FileSystemRegistryTests
{
    [Fact]
    public void EmptyRegistryReportsNoSupportedTypes()
    {
        var registry = new FileSystemRegistry();

        Assert.Empty(registry.SupportedTypes);
    }

    [Fact]
    public void RegisteredTypeIsReportedAsSupported()
    {
        var registry = new FileSystemRegistry();

        registry.Register(new FixedAnalyzer(FileSystemType.Fat32, true));

        Assert.Equal([FileSystemType.Fat32], registry.SupportedTypes);
    }

    [Fact]
    public void DuplicateTypeIsRejected()
    {
        var registry = new FileSystemRegistry();
        registry.Register(new FixedAnalyzer(FileSystemType.Ntfs, true));

        Assert.Throws<InvalidOperationException>(() => registry.Register(new FixedAnalyzer(FileSystemType.Ntfs, false)));
    }

    [Fact]
    public async Task DetectReturnsFirstMatchingAnalyzer()
    {
        var registry = new FileSystemRegistry();
        registry.Register(new FixedAnalyzer(FileSystemType.Ntfs, false));
        registry.Register(new FixedAnalyzer(FileSystemType.ExFat, true));

        var detected = await registry.DetectAsync(new EmptySource(), 0);

        Assert.NotNull(detected);
        Assert.Equal(FileSystemType.ExFat, detected.Type);
    }

    [Fact]
    public async Task DetectReturnsNullWhenNothingMatches()
    {
        var registry = new FileSystemRegistry();
        registry.Register(new FixedAnalyzer(FileSystemType.Ntfs, false));

        var detected = await registry.DetectAsync(new EmptySource(), 0);

        Assert.Null(detected);
    }

    private sealed class FixedAnalyzer(FileSystemType type, bool matches) : IFileSystemAnalyzer
    {
        public FileSystemType Type { get; } = type;

        public Task<bool> ProbeAsync(IBlockSource source, long partitionOffset, CancellationToken cancellationToken = default) =>
            Task.FromResult(matches);
    }

    private sealed class EmptySource : IBlockSource
    {
        public string DisplayName => "leer";

        public long Length => 0;

        public int SectorSize => 512;

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(0);

        public void Dispose()
        {
        }
    }
}
