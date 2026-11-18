using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;
using RicoRecovery.Storage.Native;

namespace RicoRecovery.Storage;

public sealed class DiskEnumerator : IDiskEnumerator
{
    private const int MaxDiskIndex = 64;
    private const int DescriptorMinimumLength = 36;
    private const int GeometryMinimumLength = 32;
    private const int StorageDeviceProperty = 0;
    private const int StorageSeekPenaltyProperty = 7;

    public Task<IReadOnlyList<PhysicalDiskInfo>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Die Datenträgererkennung ist nur unter Windows verfügbar.");
        }

        return Task.Run(() => Enumerate(cancellationToken), cancellationToken);
    }

    private static IReadOnlyList<PhysicalDiskInfo> Enumerate(CancellationToken cancellationToken)
    {
        var disks = new List<PhysicalDiskInfo>();

        for (var index = 0; index < MaxDiskIndex; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var disk = Query(index);

            if (disk is not null)
            {
                disks.Add(disk);
            }
        }

        return disks;
    }

    private static PhysicalDiskInfo? Query(int index)
    {
        var path = $@"\\.\PhysicalDrive{index}";

        using var handle = NativeMethods.CreateFile(
            path,
            0,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return null;
        }

        var geometry = new byte[256];

        var geometryOk = NativeMethods.DeviceIoControl(
            handle,
            NativeMethods.IoctlDiskGetDriveGeometryEx,
            null,
            0,
            geometry,
            geometry.Length,
            out var geometryBytes,
            IntPtr.Zero);

        if (!geometryOk || geometryBytes < GeometryMinimumLength)
        {
            return null;
        }

        var sectorSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(geometry.AsSpan(20));
        var size = BinaryPrimitives.ReadInt64LittleEndian(geometry.AsSpan(24));

        if (size <= 0)
        {
            return null;
        }

        if (sectorSize < 512 || sectorSize > 4096 || (sectorSize & (sectorSize - 1)) != 0)
        {
            sectorSize = 512;
        }

        var vendor = string.Empty;
        var model = string.Empty;
        var serial = string.Empty;
        var bus = DiskBusType.Unknown;
        var removable = false;

        var descriptor = QueryProperty(handle, StorageDeviceProperty, 1024);

        if (descriptor is { Length: >= DescriptorMinimumLength })
        {
            removable = descriptor[10] != 0;
            vendor = ReadText(descriptor, (int)BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(12)));
            model = ReadText(descriptor, (int)BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(16)));
            serial = ReadText(descriptor, (int)BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(24)));

            var busValue = (int)BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(28));
            bus = Enum.IsDefined((DiskBusType)busValue) ? (DiskBusType)busValue : DiskBusType.Unknown;
        }

        var seekPenalty = QueryProperty(handle, StorageSeekPenaltyProperty, 16);
        bool? solidState = seekPenalty is { Length: >= 9 } ? seekPenalty[8] == 0 : null;

        return new PhysicalDiskInfo(index, vendor, model, serial, size, sectorSize, bus, solidState, removable);
    }

    private static byte[]? QueryProperty(SafeFileHandle handle, int propertyId, int outputSize)
    {
        var query = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(query, propertyId);

        var output = new byte[outputSize];

        var ok = NativeMethods.DeviceIoControl(
            handle,
            NativeMethods.IoctlStorageQueryProperty,
            query,
            query.Length,
            output,
            output.Length,
            out var returned,
            IntPtr.Zero);

        if (!ok || returned <= 0)
        {
            return null;
        }

        return output.AsSpan(0, returned).ToArray();
    }

    private static string ReadText(byte[] buffer, int offset)
    {
        if (offset <= 0 || offset >= buffer.Length)
        {
            return string.Empty;
        }

        var end = Array.IndexOf(buffer, (byte)0, offset);

        if (end < 0)
        {
            end = buffer.Length;
        }

        return Encoding.ASCII.GetString(buffer, offset, end - offset).Trim();
    }
}
