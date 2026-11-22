namespace RicoRecovery.Core.Models;

public sealed record DeepScanOptions(long StartOffset, long Length, int Alignment, IReadOnlyList<DataRun> ExcludedRuns)
{
    public static DeepScanOptions Whole { get; } = new(0, long.MaxValue, 0, []);

    public static DeepScanOptions ExcludingActiveFiles(QuickScanResult quickScan, int alignment = 0)
    {
        ArgumentNullException.ThrowIfNull(quickScan);

        var runs = quickScan.Items
            .Where(item => !item.Entry.IsDeleted)
            .SelectMany(item => item.Entry.DataRuns)
            .Where(run => !run.IsSparse)
            .ToArray();

        return new DeepScanOptions(0, long.MaxValue, alignment, runs);
    }
}
