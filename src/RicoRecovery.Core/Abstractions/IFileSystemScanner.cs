using RicoRecovery.Core.Models;

namespace RicoRecovery.Core.Abstractions;

public interface IFileSystemScanner
{
    FileSystemType Type { get; }

    Task<VolumeInfo?> ReadVolumeInfoAsync(IBlockSource source, long partitionOffset, CancellationToken cancellationToken = default);

    IAsyncEnumerable<FileSystemEntry> EnumerateAsync(
        IBlockSource source,
        long partitionOffset,
        FileSystemScanOptions options,
        CancellationToken cancellationToken = default);
}
