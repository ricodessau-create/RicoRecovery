namespace RicoRecovery.Core.Models;

public sealed record PartitionTableInfo(
    PartitionScheme Scheme,
    IReadOnlyList<PartitionEntry> Partitions,
    IReadOnlyList<string> Warnings,
    bool UsedBackupTable);
