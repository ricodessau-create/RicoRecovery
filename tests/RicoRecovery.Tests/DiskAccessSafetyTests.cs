using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;
using RicoRecovery.Storage;
using Xunit;

namespace RicoRecovery.Tests;

public sealed class DiskAccessSafetyTests
{
    [Fact]
    public void PhysicalDiskAccessRequestsReadOnlyRights()
    {
        Assert.Equal(0x80000000u, PhysicalDiskBlockSource.DesiredAccess);
    }

    [Fact]
    public void BlockSourceImplementationsExposeNoWriteMembers()
    {
        var types = typeof(PhysicalDiskBlockSource).Assembly
            .GetTypes()
            .Where(type => type.IsClass && typeof(IBlockSource).IsAssignableFrom(type));

        foreach (var type in types)
        {
            var names = type.GetMethods().Select(method => method.Name).ToArray();

            Assert.DoesNotContain(names, name => name.StartsWith("Write", StringComparison.Ordinal));
            Assert.DoesNotContain(names, name => name.Contains("Flush", StringComparison.Ordinal));
            Assert.DoesNotContain(names, name => name.Contains("Format", StringComparison.Ordinal));
            Assert.DoesNotContain(names, name => name.Contains("SetLength", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void DevicePathUsesPhysicalDriveNaming()
    {
        var disk = new PhysicalDiskInfo(3, "V", "M", "S", 1000, 512, DiskBusType.Usb, null, true);

        Assert.Equal(@"\\.\PhysicalDrive3", disk.DevicePath);
    }

    [Fact]
    public async Task EnumerationCompletesOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var disks = await new DiskEnumerator().EnumerateAsync();

        Assert.NotNull(disks);
        Assert.All(disks, disk => Assert.True(disk.SizeBytes > 0));
    }
}
