namespace RicoRecovery.Core.Models;

public sealed record LostPartitionResult(
    IReadOnlyList<RecoveredPartition> Partitions,
    PartitionTableInfo PartitionTable,
    long BytesScanned,
    IReadOnlyList<string> Warnings);
