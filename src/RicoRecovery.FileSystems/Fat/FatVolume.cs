using RicoRecovery.Core.Models;

namespace RicoRecovery.FileSystems.Fat;

internal sealed record FatVolume(
    FileSystemType Type,
    int BytesPerSector,
    int SectorsPerCluster,
    int ReservedSectors,
    int FatCount,
    int RootEntryCount,
    long FatSectors,
    long TotalSectors,
    uint ClusterCount,
    uint RootCluster,
    string Label,
    bool FromBackup)
{
    public int ClusterBytes => BytesPerSector * SectorsPerCluster;

    public long RootDirectorySectors => ((long)RootEntryCount * 32 + BytesPerSector - 1) / BytesPerSector;

    public long RootDirectoryStartSector => ReservedSectors + FatCount * FatSectors;

    public long DataStartSector => RootDirectoryStartSector + RootDirectorySectors;
}
