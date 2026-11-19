using RicoRecovery.Core.Models;
using RicoRecovery.FileSystems.Ext;
using RicoRecovery.FileSystems.ExFat;
using RicoRecovery.FileSystems.Fat;
using RicoRecovery.FileSystems.Ntfs;

namespace RicoRecovery.FileSystems;

public static class DefaultFileSystems
{
    public static FileSystemRegistry CreateRegistry()
    {
        var registry = new FileSystemRegistry();

        registry.Register(new NtfsAnalyzer());
        registry.Register(new ExFatAnalyzer());
        registry.Register(new FatAnalyzer(FileSystemType.Fat32));
        registry.Register(new FatAnalyzer(FileSystemType.Fat16));
        registry.Register(new ExtAnalyzer(FileSystemType.Ext4));
        registry.Register(new ExtAnalyzer(FileSystemType.Ext3));
        registry.Register(new ExtAnalyzer(FileSystemType.Ext2));

        return registry;
    }
}
