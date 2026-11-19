using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;

namespace RicoRecovery.FileSystems.Fat;

internal sealed record FatPendingDirectory(uint Cluster, string Path, bool Deleted);

internal sealed class FatLayout
{
    public FatLayout(IBlockSource source, long partitionOffset, FatVolume volume)
    {
        Source = source;
        Volume = volume;
        ClusterBytes = volume.ClusterBytes;
        DataOffset = partitionOffset + volume.DataStartSector * volume.BytesPerSector;
        RootOffset = partitionOffset + volume.RootDirectoryStartSector * volume.BytesPerSector;

        var kind = volume.Type == FileSystemType.Fat16 ? FatKind.Fat16 : FatKind.Fat32;

        Table = new FatTable(
            source,
            partitionOffset + volume.ReservedSectors * (long)volume.BytesPerSector,
            volume.BytesPerSector,
            kind,
            volume.ClusterCount);
    }

    public IBlockSource Source { get; }

    public FatVolume Volume { get; }

    public int ClusterBytes { get; }

    public long DataOffset { get; }

    public long RootOffset { get; }

    public FatTable Table { get; }

    public long OffsetOf(uint cluster) => DataOffset + (cluster - 2L) * ClusterBytes;
}
