namespace RicoRecovery.Core.Models;

public sealed record FileSystemScanOptions(bool IncludeActive, bool IncludeDeleted)
{
    public static FileSystemScanOptions All { get; } = new(true, true);

    public static FileSystemScanOptions DeletedOnly { get; } = new(false, true);
}
