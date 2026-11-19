namespace RicoRecovery.FileSystems.Ntfs;

internal sealed record NtfsVolume(
    int BytesPerSector,
    int SectorsPerCluster,
    long TotalSectors,
    long MftCluster,
    long MftMirrorCluster,
    int RecordSize)
{
    public int ClusterBytes => BytesPerSector * SectorsPerCluster;
}
