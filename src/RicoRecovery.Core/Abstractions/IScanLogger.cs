using RicoRecovery.Core.Models;

namespace RicoRecovery.Core.Abstractions;

public interface IScanLogger
{
    Task WriteAsync(ScanLogEntry entry, CancellationToken cancellationToken = default);
}
