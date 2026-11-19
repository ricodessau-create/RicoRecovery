using RicoRecovery.Core.Abstractions;

namespace RicoRecovery.FileSystems.Ext;

internal sealed class ExtScanContext
{
    public ExtScanContext(IBlockSource source, long partitionOffset, ExtVolume volume, ExtGroupTable groups)
    {
        Source = source;
        PartitionOffset = partitionOffset;
        Volume = volume;
        Groups = groups;
        Inodes = new ExtInodeReader(source, partitionOffset, volume, groups);
        Mapper = new ExtBlockMapper(source, partitionOffset, volume);
        Bitmaps = new ExtBlockBitmapCache(source, partitionOffset, volume, groups);
    }

    public IBlockSource Source { get; }

    public long PartitionOffset { get; }

    public ExtVolume Volume { get; }

    public ExtGroupTable Groups { get; }

    public ExtInodeReader Inodes { get; }

    public ExtBlockMapper Mapper { get; }

    public ExtBlockBitmapCache Bitmaps { get; }
}
