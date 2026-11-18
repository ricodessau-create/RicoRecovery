using RicoRecovery.Core.Models;

namespace RicoRecovery.Recovery;

public sealed class ScanProgressTracker
{
    private readonly long totalBytes;
    private long processedBytes;
    private long filesFound;

    public ScanProgressTracker(long totalBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytes);
        this.totalBytes = totalBytes;
    }

    public void Advance(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        Interlocked.Add(ref processedBytes, bytes);
    }

    public void RegisterFile() => Interlocked.Increment(ref filesFound);

    public ScanProgress Snapshot() =>
        new(Interlocked.Read(ref processedBytes), totalBytes, Interlocked.Read(ref filesFound));
}
