namespace RicoRecovery.Core.Models;

public sealed record ScanProgress(long BytesProcessed, long TotalBytes, long FilesFound)
{
    public double Fraction => TotalBytes <= 0
        ? 0.0
        : Math.Min(1.0, (double)BytesProcessed / TotalBytes);
}
