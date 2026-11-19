namespace RicoRecovery.FileSystems.ExFat;

internal sealed record ExFatVolume(
    int BytesPerSector,
    int SectorsPerCluster,
    long VolumeSectors,
    long FatOffsetSectors,
    long FatSectors,
    long HeapOffsetSectors,
    uint ClusterCount,
    uint RootCluster,
    bool FromBackup)
{
    public int ClusterBytes => BytesPerSector * SectorsPerCluster;
}
