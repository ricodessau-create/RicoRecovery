namespace RicoRecovery.Core.Models;

public sealed record ScanVolume(int Index, PartitionEntry? Partition, long Offset, VolumeInfo Info);
