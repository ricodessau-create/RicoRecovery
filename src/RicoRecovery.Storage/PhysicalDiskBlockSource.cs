using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;
using RicoRecovery.Storage.Native;

namespace RicoRecovery.Storage;

public sealed class PhysicalDiskBlockSource : IBlockSource
{
    public const uint DesiredAccess = NativeMethods.GenericRead;

    private const int MaxChunkBytes = 1 << 20;

    private readonly SafeFileHandle handle;
    private bool disposed;

    private PhysicalDiskBlockSource(SafeFileHandle handle, PhysicalDiskInfo disk)
    {
        this.handle = handle;
        Length = disk.SizeBytes;
        SectorSize = disk.SectorSize;
        DisplayName = string.IsNullOrWhiteSpace(disk.Model)
            ? $"Datenträger {disk.Index}"
            : $"Datenträger {disk.Index} ({disk.Model})";
    }

    public string DisplayName { get; }

    public long Length { get; }

    public int SectorSize { get; }

    public static PhysicalDiskBlockSource Open(PhysicalDiskInfo disk)
    {
        ArgumentNullException.ThrowIfNull(disk);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Der direkte Datenträgerzugriff ist nur unter Windows verfügbar.");
        }

        var handle = NativeMethods.CreateFile(
            disk.DevicePath,
            DesiredAccess,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();

            if (error == 5)
            {
                throw new UnauthorizedAccessException(
                    "Zugriff verweigert. Für den direkten Lesezugriff auf Datenträger sind Administratorrechte erforderlich.");
            }

            throw new IOException($"Der Datenträger {disk.DevicePath} konnte nicht geöffnet werden (Windows-Fehlercode {error}).");
        }

        return new PhysicalDiskBlockSource(handle, disk);
    }

    public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        if (buffer.IsEmpty || offset >= Length)
        {
            return 0;
        }

        return await Task.Run(() => ReadAligned(offset, buffer), cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        handle.Dispose();
    }

    private int ReadAligned(long offset, Memory<byte> destination)
    {
        var wanted = (int)Math.Min(destination.Length, Length - offset);
        var alignedStart = offset - offset % SectorSize;
        var alignedEnd = RoundUp(offset + wanted, SectorSize);
        var alignedLength = checked((int)(alignedEnd - alignedStart));
        var rented = ArrayPool<byte>.Shared.Rent(alignedLength);

        try
        {
            var span = rented.AsSpan(0, alignedLength);
            var total = 0;

            while (total < alignedLength)
            {
                var chunk = Math.Min(alignedLength - total, MaxChunkBytes);
                var read = RandomAccess.Read(handle, span.Slice(total, chunk), alignedStart + total);

                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            var skip = (int)(offset - alignedStart);
            var available = Math.Max(0, Math.Min(wanted, total - skip));

            span.Slice(skip, available).CopyTo(destination.Span);

            return available;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static long RoundUp(long value, int multiple) =>
        (value + multiple - 1) / multiple * multiple;
}
