namespace RicoRecovery.Core.Models;

public sealed record PartitionEntry(
    int Index,
    long StartOffset,
    long Length,
    string TypeDescription,
    string? Name,
    bool IsBootable,
    bool IsLogical);
