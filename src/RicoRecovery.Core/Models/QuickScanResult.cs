namespace RicoRecovery.Core.Models;

public sealed record QuickScanResult(
    PartitionTableInfo PartitionTable,
    IReadOnlyList<ScanVolume> Volumes,
    IReadOnlyList<ScanItem> Items,
    long FilesFound,
    long DeletedFound,
    IReadOnlyList<string> Warnings);
