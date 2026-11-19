using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;

namespace RicoRecovery.FileSystems.Ntfs;

internal sealed class NtfsMft
{
    private const int ChunkBytes = 4 * 1024 * 1024;

    private readonly IBlockSource source;
    private readonly RunMap map;
    private readonly NtfsRecordParser parser;

    private NtfsMft(IBlockSource source, NtfsRecordParser parser, int recordSize, IReadOnlyList<DataRun> runs, long realSize)
    {
        this.source = source;
        this.parser = parser;
        map = new RunMap(runs);
        RecordSize = recordSize;
        RecordCount = realSize / recordSize;
    }

    public int RecordSize { get; }

    public long RecordCount { get; }

    public static async Task<NtfsMft?> OpenAsync(IBlockSource source, long partitionOffset, NtfsVolume volume, CancellationToken cancellationToken)
    {
        var parser = new NtfsRecordParser(partitionOffset, volume.ClusterBytes, volume.RecordSize);

        foreach (var cluster in new[] { volume.MftCluster, volume.MftMirrorCluster })
        {
            var buffer = await BlockReader.ReadAsync(source, partitionOffset + cluster * volume.ClusterBytes, volume.RecordSize, cancellationToken).ConfigureAwait(false);

            if (buffer is null)
            {
                continue;
            }

            var record = parser.Parse(buffer, 0, 0);
            var data = record?.Attributes.FirstOrDefault(attribute =>
                attribute.Type == 0x80 && attribute.Name.Length == 0 && attribute.NonResident && attribute.StartVcn == 0);

            if (data is null || data.Runs.Count == 0 || data.RealSize < volume.RecordSize)
            {
                continue;
            }

            return new NtfsMft(source, parser, volume.RecordSize, data.Runs, data.RealSize);
        }

        return null;
    }

    public async Task<NtfsRecord?> ReadRecordAsync(long number, CancellationToken cancellationToken)
    {
        if (number < 0 || number >= RecordCount)
        {
            return null;
        }

        if (!map.TryTranslate(number * RecordSize, out var physical, out var contiguous) || contiguous < RecordSize)
        {
            return null;
        }

        var buffer = await BlockReader.ReadAsync(source, physical, RecordSize, cancellationToken).ConfigureAwait(false);

        return buffer is null ? null : parser.Parse(buffer, 0, number);
    }

    public async Task<(byte[]? Buffer, int Count)> ReadChunkAsync(long firstNumber, CancellationToken cancellationToken)
    {
        if (!map.TryTranslate(firstNumber * RecordSize, out var physical, out var contiguous))
        {
            return (null, 1);
        }

        var count = (int)Math.Min(Math.Min(contiguous / RecordSize, ChunkBytes / RecordSize), RecordCount - firstNumber);

        if (count < 1)
        {
            return (null, 1);
        }

        var buffer = await BlockReader.ReadAsync(source, physical, count * RecordSize, cancellationToken).ConfigureAwait(false);

        return (buffer, count);
    }

    public NtfsRecord? ParseRecord(byte[] buffer, int index, long number) =>
        parser.Parse(buffer, index * RecordSize, number);
}
