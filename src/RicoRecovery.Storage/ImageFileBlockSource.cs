using Microsoft.Win32.SafeHandles;
using RicoRecovery.Core.Abstractions;

namespace RicoRecovery.Storage;

public sealed class ImageFileBlockSource : IBlockSource
{
    private readonly SafeFileHandle handle;
    private bool disposed;

    public ImageFileBlockSource(string path, int sectorSize = 512)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!IsValidSectorSize(sectorSize))
        {
            throw new ArgumentOutOfRangeException(nameof(sectorSize), "Die Sektorgröße muss eine Zweierpotenz zwischen 512 und 4096 sein.");
        }

        handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        Length = RandomAccess.GetLength(handle);
        SectorSize = sectorSize;
        DisplayName = Path.GetFileName(path);
    }

    public string DisplayName { get; }

    public long Length { get; }

    public int SectorSize { get; }

    public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        if (offset >= Length)
        {
            return 0;
        }

        var total = 0;

        while (total < buffer.Length)
        {
            var read = await RandomAccess.ReadAsync(handle, buffer[total..], offset + total, cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
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

    private static bool IsValidSectorSize(int sectorSize) =>
        sectorSize is >= 512 and <= 4096 && (sectorSize & (sectorSize - 1)) == 0;
}
