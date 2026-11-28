namespace RicoRecovery.Core.Models;

public sealed record RecoveredPartition(
    long StartOffset,
    long Length,
    FileSystemType Type,
    string? Label,
    bool IsInPartitionTable,
    string Note);
