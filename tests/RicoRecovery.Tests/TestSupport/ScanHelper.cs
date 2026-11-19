using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;
using RicoRecovery.FileSystems;

namespace RicoRecovery.Tests.TestSupport;

internal static class ScanHelper
{
    public static async Task<List<FileSystemEntry>> EnumerateAsync(IFileSystemScanner scanner, byte[] image, long partitionOffset = 0)
    {
        var entries = new List<FileSystemEntry>();

        await foreach (var entry in scanner.EnumerateAsync(new MemoryBlockSource(image), partitionOffset, FileSystemScanOptions.All))
        {
            entries.Add(entry);
        }

        return entries;
    }

    public static async Task<byte[]> ReadContentAsync(byte[] image, FileSystemEntry entry)
    {
        using var stream = new MemoryStream();

        await EntryContentReader.CopyToAsync(new MemoryBlockSource(image), entry, stream);

        return stream.ToArray();
    }

    public static byte[] Pattern(int length) =>
        Enumerable.Range(0, length).Select(index => (byte)(index % 251)).ToArray();
}
