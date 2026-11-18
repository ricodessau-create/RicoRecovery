using RicoRecovery.Core.Models;

namespace RicoRecovery.Core.Abstractions;

public interface IDiskEnumerator
{
    Task<IReadOnlyList<PhysicalDiskInfo>> EnumerateAsync(CancellationToken cancellationToken = default);
}
