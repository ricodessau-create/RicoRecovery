using System.Buffers.Binary;
using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;
using RicoRecovery.FileSystems;
using RicoRecovery.Recovery.Carving;
using RicoRecovery.Storage.Partitioning;

namespace RicoRecovery.Recovery.Partitions;

public sealed class LostPartitionScanner
{
    private const int ChunkBytes = 8 * 1024 * 1024;
    private const int OverlapBytes = 1024;

    private readonly FileSystemRegistry registry;
    private readonly IScanLogger? logger;

    public LostPartitionScanner(FileSystemRegistry registry, IScanLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        this.registry = registry;
        this.logger = logger;
    }

    public async Task<LostPartitionResult> ScanAsync(
        IBlockSource source,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var warnings = new List<string>();
        var found = new List<RecoveredPartition>();
        var sectorSize = Math.Max(512, source.SectorSize);
        var tracker = new ScanProgressTracker(source.Length);

        await LogAsync("Info", $"Partitionsanalyse gestartet: {source.DisplayName}, {source.Length} Bytes.", cancellationToken).ConfigureAwait(false);

        var table = await PartitionTableReader.ReadAsync(source, cancellationToken).ConfigureAwait(false);

        foreach (var warning in table.Warnings)
        {
            warnings.Add(warning);
        }

        for (long chunkStart = 0; chunkStart < source.Length; chunkStart += ChunkBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunkEnd = Math.Min(chunkStart + ChunkBytes, source.Length);
            var readLength = (int)(Math.Min(chunkEnd + OverlapBytes, source.Length) - chunkStart);
            var data = await SourceReader.ReadAsync(source, chunkStart, readLength, cancellationToken).ConfigureAwait(false);

            if (data is null)
            {
                var warning = $"Der Bereich ab Offset {chunkStart} ist nicht lesbar und wurde übersprungen.";
                warnings.Add(warning);
                await LogAsync("Warnung", warning, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                foreach (var candidate in FindCandidates(data, chunkStart, chunkEnd, sectorSize))
                {
                    if (found.Any(partition => candidate >= partition.StartOffset && candidate < partition.StartOffset + partition.Length))
                    {
                        continue;
                    }

                    var partition = await VerifyAsync(source, candidate, table, cancellationToken).ConfigureAwait(false);

                    if (partition is null)
                    {
                        continue;
                    }

                    found.Add(partition);
                    await LogAsync(
                        "Info",
                        $"Partition bei Offset {partition.StartOffset} gefunden: {partition.Type}, {partition.Length} Bytes, Partitionstabelle: {(partition.IsInPartitionTable ? "eingetragen" : "nicht eingetragen")}.",
                        cancellationToken).ConfigureAwait(false);
                }
            }

            tracker.Advance(chunkEnd - chunkStart);
            progress?.Report(tracker.Snapshot());
        }

        await LogAsync("Info", $"Partitionsanalyse beendet: {found.Count} Dateisysteme gefunden.", cancellationToken).ConfigureAwait(false);

        return new LostPartitionResult(found, table, source.Length, warnings);
    }

    private async Task<RecoveredPartition?> VerifyAsync(IBlockSource source, long offset, PartitionTableInfo table, CancellationToken cancellationToken)
    {
        var analyzer = await registry.DetectAsync(source, offset, cancellationToken).ConfigureAwait(false);

        if (analyzer is not IFileSystemScanner scanner)
        {
            return null;
        }

        var info = await scanner.ReadVolumeInfoAsync(source, offset, cancellationToken).ConfigureAwait(false);

        if (info is null || info.TotalBytes <= 0)
        {
            return null;
        }

        var length = info.TotalBytes + (info.Type == FileSystemType.Ntfs ? source.SectorSize : 0);
        length = Math.Min(length, source.Length - offset);

        var listed = table.Partitions.Any(partition => partition.StartOffset == offset);
        var note = listed
            ? "Die Partition ist in der Partitionstabelle eingetragen."
            : "Die Partition fehlt in der Partitionstabelle, Grenzen aus der Dateisystemgröße abgeleitet.";

        return new RecoveredPartition(offset, length, info.Type, info.Label, listed, info.Note is null ? note : note + " " + info.Note);
    }

    private static List<long> FindCandidates(byte[] data, long chunkStart, long chunkEnd, int sectorSize)
    {
        var result = new SortedSet<long>();

        for (var position = chunkStart; position < chunkEnd; position += sectorSize)
        {
            var sector = data.AsSpan((int)(position - chunkStart));

            if (sector.Length >= 512 && sector[510] == 0x55 && sector[511] == 0xAA)
            {
                if (sector.Slice(3, 8).SequenceEqual("NTFS    "u8)
                    || sector.Slice(3, 8).SequenceEqual("EXFAT   "u8)
                    || LooksLikeFat(sector))
                {
                    result.Add(position);
                }
            }

            if (position >= 1024 && sector.Length >= 64 && sector[56] == 0x53 && sector[57] == 0xEF && LooksLikeExt(sector))
            {
                result.Add(position - 1024);
            }
        }

        return result.ToList();
    }

    private static bool LooksLikeFat(ReadOnlySpan<byte> sector)
    {
        var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(sector[11..]);
        var sectorsPerCluster = sector[13];
        var reserved = BinaryPrimitives.ReadUInt16LittleEndian(sector[14..]);
        var fats = sector[16];

        return bytesPerSector is 512 or 1024 or 2048 or 4096
            && sectorsPerCluster != 0
            && (sectorsPerCluster & (sectorsPerCluster - 1)) == 0
            && reserved != 0
            && fats is 1 or 2;
    }

    private static bool LooksLikeExt(ReadOnlySpan<byte> sector) =>
        BinaryPrimitives.ReadUInt32LittleEndian(sector[24..]) <= 6
        && BinaryPrimitives.ReadUInt32LittleEndian(sector[32..]) != 0
        && BinaryPrimitives.ReadUInt32LittleEndian(sector[40..]) != 0;

    private Task LogAsync(string level, string message, CancellationToken cancellationToken) =>
        logger is null
            ? Task.CompletedTask
            : logger.WriteAsync(new ScanLogEntry(DateTimeOffset.Now, level, message), cancellationToken);
}
