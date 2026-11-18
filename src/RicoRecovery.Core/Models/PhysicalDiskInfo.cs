namespace RicoRecovery.Core.Models;

public sealed record PhysicalDiskInfo(
    int Index,
    string Vendor,
    string Model,
    string SerialNumber,
    long SizeBytes,
    int SectorSize,
    DiskBusType BusType,
    bool? IsSolidState,
    bool IsRemovable)
{
    public string DevicePath => $@"\\.\PhysicalDrive{Index}";
}
