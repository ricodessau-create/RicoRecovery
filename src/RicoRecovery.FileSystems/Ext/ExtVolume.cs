using RicoRecovery.Core.Models;

namespace RicoRecovery.FileSystems.Ext;

internal sealed record ExtVolume(
    FileSystemType Type,
    int BlockSize,
    uint InodesCount,
    long BlocksCount,
    uint FirstDataBlock,
    uint BlocksPerGroup,
    uint InodesPerGroup,
    uint FirstInode,
    int InodeSize,
    int DescriptorSize,
    uint IncompatibleFeatures,
    uint ReadOnlyCompatibleFeatures,
    string Label,
    long GroupDescriptorOffset,
    bool FromBackup)
{
    public int GroupCount => (int)((BlocksCount - FirstDataBlock + BlocksPerGroup - 1) / BlocksPerGroup);

    public bool HasGroupChecksums => (ReadOnlyCompatibleFeatures & 0x410) != 0;

    public string? UnsupportedReason
    {
        get
        {
            if ((IncompatibleFeatures & 0x10) != 0)
            {
                return "Das Feature META_BG wird nicht unterstützt.";
            }

            if ((ReadOnlyCompatibleFeatures & 0x200) != 0)
            {
                return "Das Feature BIGALLOC wird nicht unterstützt.";
            }

            return null;
        }
    }
}
