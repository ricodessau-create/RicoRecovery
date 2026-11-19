using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;
using RicoRecovery.FileSystems;
using RicoRecovery.Storage.Partitioning;

namespace RicoRecovery.Recovery;

public sealed class QuickScanner
{
    private const int ProgressInterval = 256;

    private readonly FileSystemRegistry registry;
    private readonly IScanLogger? logger;

    public QuickScanner(FileSystemRegistry registry, IScanLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        this.registry = registry;
        this.logger = logger;
    }

    public async Task<QuickScanResult> ScanAsync(
        IBlockSource source,
        Func<ScanItem, CancellationToken, ValueTask>? sink = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var tracker = new ScanProgressTracker(source.Length);
        var warnings = new List<string>();
        var volumes = new List<ScanVolume>();
        var items = new List<ScanItem>();
        long deletedFound = 0;

        await LogAsync("Info", $"Schnellscan gestartet: {source.DisplayName}, {source.Length} Bytes, Sektorgröße {source.SectorSize}.", cancellationToken).ConfigureAwait(false);

        var table = await PartitionTableReader.ReadAsync(source, cancellationToken).ConfigureAwait(false);

        foreach (var warning in table.Warnings)
        {
            warnings.Add(warning);
            await LogAsync("Warnung", warning, cancellationToken).ConfigureAwait(false);
        }

        await LogAsync("Info", $"Partitionsschema: {table.Scheme}, {table.Partitions.Count} Partition(en) erkannt.", cancellationToken).ConfigureAwait(false);

        var whole = await registry.DetectAsync(source, 0, cancellationToken).ConfigureAwait(false);
        var candidates = new List<Candidate>();

        if (whole is not null)
        {
            candidates.Add(new Candidate(null, 0, source.Length));
            await LogAsync("Info", "Auf dem Datenträger wurde ohne Partitionstabelle direkt ein Dateisystem erkannt.", cancellationToken).ConfigureAwait(false);
        }
        else if (table.Partitions.Count == 0)
        {
            const string message = "Weder eine Partitionstabelle noch ein Dateisystem wurde erkannt. Für RAW-Datenträger ist der Tiefenscan erforderlich.";

            warnings.Add(message);
            await LogAsync("Warnung", message, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            foreach (var partition in table.Partitions)
            {
                candidates.Add(new Candidate(partition, partition.StartOffset, partition.Length));
            }
        }

        var volumeIndex = 0;

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var label = candidate.Partition is null ? "Datenträger" : $"Partition {candidate.Partition.Index}";

            if (candidate.Offset >= source.Length)
            {
                await AddWarningAsync(warnings, $"{label}: Der Beginn liegt hinter dem Ende des Datenträgers.", cancellationToken).ConfigureAwait(false);
                continue;
            }

            var analyzer = candidate.Partition is null
                ? whole
                : await registry.DetectAsync(source, candidate.Offset, cancellationToken).ConfigureAwait(false);

            if (analyzer is not IFileSystemScanner scanner)
            {
                await AddWarningAsync(warnings, $"{label}: Dateisystem nicht erkannt oder nicht unterstützt (RAW).", cancellationToken).ConfigureAwait(false);
                continue;
            }

            var info = await scanner.ReadVolumeInfoAsync(source, candidate.Offset, cancellationToken).ConfigureAwait(false);

            if (info is null)
            {
                await AddWarningAsync(warnings, $"{label}: Die Volume-Informationen konnten nicht gelesen werden.", cancellationToken).ConfigureAwait(false);
                continue;
            }

            volumes.Add(new ScanVolume(volumeIndex, candidate.Partition, candidate.Offset, info));
            await LogAsync("Info", $"{label}: {info.Type}, Beschriftung \"{info.Label}\", {info.TotalBytes} Bytes, Clustergröße {info.ClusterSize}.", cancellationToken).ConfigureAwait(false);

            if (info.Note is not null)
            {
                warnings.Add($"{label}: {info.Note}");
                await LogAsync("Warnung", $"{label}: {info.Note}", cancellationToken).ConfigureAwait(false);
            }

            await foreach (var entry in scanner.EnumerateAsync(source, candidate.Offset, FileSystemScanOptions.All, cancellationToken).ConfigureAwait(false))
            {
                var item = new ScanItem(volumeIndex, entry);

                if (sink is not null)
                {
                    await sink(item, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    items.Add(item);
                }

                tracker.RegisterFile();

                if (entry.IsDeleted)
                {
                    deletedFound++;
                }

                var snapshot = tracker.Snapshot();

                if (snapshot.FilesFound % ProgressInterval == 0)
                {
                    progress?.Report(snapshot);
                }
            }

            tracker.Advance(Math.Min(candidate.Length, source.Length - candidate.Offset));
            progress?.Report(tracker.Snapshot());
            volumeIndex++;
        }

        var processed = tracker.Snapshot().BytesProcessed;
        tracker.Advance(Math.Max(0, source.Length - processed));

        var final = tracker.Snapshot();
        progress?.Report(final);

        await LogAsync("Info", $"Schnellscan beendet: {final.FilesFound} Einträge, davon {deletedFound} gelöscht.", cancellationToken).ConfigureAwait(false);

        return new QuickScanResult(table, volumes, items, final.FilesFound, deletedFound, warnings);
    }

    private async Task AddWarningAsync(List<string> warnings, string message, CancellationToken cancellationToken)
    {
        warnings.Add(message);
        await LogAsync("Warnung", message, cancellationToken).ConfigureAwait(false);
    }

    private Task LogAsync(string level, string message, CancellationToken cancellationToken) =>
        logger is null
            ? Task.CompletedTask
            : logger.WriteAsync(new ScanLogEntry(DateTimeOffset.Now, level, message), cancellationToken);

    private sealed record Candidate(PartitionEntry? Partition, long Offset, long Length);
}
