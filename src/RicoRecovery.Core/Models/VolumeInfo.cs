namespace RicoRecovery.Core.Models;

public sealed record VolumeInfo(FileSystemType Type, string? Label, long TotalBytes, int ClusterSize, string? Note);
