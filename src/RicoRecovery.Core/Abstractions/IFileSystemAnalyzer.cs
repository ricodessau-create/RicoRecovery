using RicoRecovery.Core.Models;

namespace RicoRecovery.Core.Abstractions;

public interface IFileSystemAnalyzer
{
    FileSystemType Type { get; }

    Task<bool> ProbeAsync(IBlockSource source, long partitionOffset, CancellationToken cancellationToken = default);
}
