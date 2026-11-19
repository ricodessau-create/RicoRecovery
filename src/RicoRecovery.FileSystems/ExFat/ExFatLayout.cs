using RicoRecovery.Core.Abstractions;
using RicoRecovery.FileSystems.Fat;

namespace RicoRecovery.FileSystems.ExFat;

internal sealed record ExFatPendingDirectory(uint Cluster, long? Length, bool NoFatChain, string Path, bool Deleted);

internal sealed class ExFatLayout
{
    public ExFatLayout(IBlockSource source, long partitionOffset, ExFatVolume volume)
    {
        Source = source;
        Volume = volume;
        ClusterBytes = volume.ClusterBytes;
        HeapOffset = partitionOffset + volume.HeapOffsetSectors * volume.BytesPerSector;

        Table = new FatTable(
            source,
            partitionOffset + volume.FatOffsetSectors * volume.BytesPerSector,
            volume.BytesPerSector,
            FatKind.ExFat,
            volume.ClusterCount);
    }

    public IBlockSource Source { get; }

    public ExFatVolume Volume { get; }

    public int ClusterBytes { get; }

    public long HeapOffset { get; }

    public FatTable Table { get; }

    public long OffsetOf(uint cluster) => HeapOffset + (cluster - 2L) * ClusterBytes;
}
