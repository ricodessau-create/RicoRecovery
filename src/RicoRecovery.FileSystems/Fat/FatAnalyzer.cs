using System.Runtime.CompilerServices;
using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;

namespace RicoRecovery.FileSystems.Fat;

public sealed class FatAnalyzer : IFileSystemAnalyzer, IFileSystemScanner
{
    private const long MaxDirectoryBytes = 4L * 1024 * 1024;

    public FatAnalyzer(FileSystemType type)
    {
        if (type is not (FileSystemType.Fat16 or FileSystemType.Fat32))
        {
            throw new ArgumentOutOfRangeException(nameof(type), "FatAnalyzer unterstützt nur FAT16 und FAT32.");
        }

        Type = type;
    }

    public FileSystemType Type { get; }

    public async Task<bool> ProbeAsync(IBlockSource source, long partitionOffset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var volume = await FatBootSector.ReadAsync(source, partitionOffset, cancellationToken).ConfigureAwait(false);

        return volume is not null && volume.Type == Type;
    }

    public async Task<VolumeInfo?> ReadVolumeInfoAsync(IBlockSource source, long partitionOffset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var volume = await FatBootSector.ReadAsync(source, partitionOffset, cancellationToken).ConfigureAwait(false);

        if (volume is null || volume.Type != Type)
        {
            return null;
        }

        return new VolumeInfo(
            Type,
            volume.Label.Length == 0 ? null : volume.Label,
            volume.TotalSectors * volume.BytesPerSector,
            volume.ClusterBytes,
            volume.FromBackup ? "Der Boot-Sektor wurde aus der Sicherungskopie gelesen." : null);
    }

    public async IAsyncEnumerable<FileSystemEntry> EnumerateAsync(
        IBlockSource source,
        long partitionOffset,
        FileSystemScanOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        var volume = await FatBootSector.ReadAsync(source, partitionOffset, cancellationToken).ConfigureAwait(false);

        if (volume is null || volume.Type != Type)
        {
            yield break;
        }

        var layout = new FatLayout(source, partitionOffset, volume);
        var isFat32 = volume.Type == FileSystemType.Fat32;
        var rootCluster = isFat32 ? volume.RootCluster : 0u;
        var visited = new HashSet<uint>();
        var pending = new Stack<FatPendingDirectory>();

        if (rootCluster != 0)
        {
            visited.Add(rootCluster);
        }

        pending.Push(new FatPendingDirectory(rootCluster, string.Empty, false));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directory = pending.Pop();
            var data = await LoadDirectoryAsync(layout, directory, cancellationToken).ConfigureAwait(false);

            foreach (var raw in FatDirectoryParser.Parse(data, isFat32, volume.ClusterCount + 1))
            {
                var deleted = raw.IsDeleted || directory.Deleted;
                var path = directory.Path + "/" + raw.Name;
                var wanted = deleted ? options.IncludeDeleted : options.IncludeActive;

                if (raw.IsDirectory)
                {
                    if (visited.Add(raw.Cluster))
                    {
                        pending.Push(new FatPendingDirectory(raw.Cluster, path, deleted));
                    }

                    if (wanted)
                    {
                        yield return new FileSystemEntry(
                            raw.Name,
                            path,
                            0,
                            true,
                            deleted,
                            raw.Modified,
                            Type,
                            deleted ? DataAvailability.Unknown : DataAvailability.Allocated,
                            [],
                            null,
                            null);
                    }

                    continue;
                }

                if (wanted)
                {
                    yield return await BuildFileAsync(layout, raw, path, deleted, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task<byte[]> LoadDirectoryAsync(FatLayout layout, FatPendingDirectory directory, CancellationToken cancellationToken)
    {
        if (directory.Cluster == 0)
        {
            var length = (int)(layout.Volume.RootDirectorySectors * layout.Volume.BytesPerSector);
            var root = await BlockReader.ReadAsync(layout.Source, layout.RootOffset, length, cancellationToken).ConfigureAwait(false);

            return root ?? Array.Empty<byte>();
        }

        if (directory.Deleted)
        {
            var first = await BlockReader.ReadAsync(layout.Source, layout.OffsetOf(directory.Cluster), layout.ClusterBytes, cancellationToken).ConfigureAwait(false);

            return first ?? Array.Empty<byte>();
        }

        var maxClusters = Math.Max(1L, MaxDirectoryBytes / layout.ClusterBytes);
        var chain = await FatChain.FollowAsync(layout.Table, directory.Cluster, maxClusters, layout.OffsetOf, layout.ClusterBytes, cancellationToken).ConfigureAwait(false);

        return await RunReader.ReadAllAsync(layout.Source, chain.Runs, MaxDirectoryBytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FileSystemEntry> BuildFileAsync(FatLayout layout, FatRawEntry raw, string path, bool deleted, CancellationToken cancellationToken)
    {
        if (raw.Size == 0 || raw.Cluster < 2)
        {
            return CreateFile(
                raw,
                path,
                deleted,
                deleted ? DataAvailability.Unallocated : DataAvailability.Allocated,
                [],
                deleted ? "Gelöscht. Die Datei ist leer." : null);
        }

        var needed = (raw.Size + layout.ClusterBytes - 1) / layout.ClusterBytes;

        if (!deleted)
        {
            var chain = await FatChain.FollowAsync(layout.Table, raw.Cluster, needed, layout.OffsetOf, layout.ClusterBytes, cancellationToken).ConfigureAwait(false);
            var note = chain.Complete ? null : "Die Clusterkette ist kürzer als die gespeicherte Dateigröße oder beschädigt.";

            return CreateFile(raw, path, false, DataAvailability.Allocated, chain.Runs, note);
        }

        var lastCluster = (long)layout.Volume.ClusterCount + 1;
        var available = Math.Min(needed, lastCluster - raw.Cluster + 1);
        var contiguous = FatChain.Contiguous(raw.Cluster, available, layout.OffsetOf, layout.ClusterBytes);
        var availability = await CheckFreeAsync(layout.Table, raw.Cluster, available, cancellationToken).ConfigureAwait(false);

        var message = availability switch
        {
            DataAvailability.Unallocated => "Gelöscht. Zusammenhängende Belegung angenommen, die Cluster sind weiterhin frei.",
            DataAvailability.Reallocated => "Gelöscht. Die Cluster wurden inzwischen von anderen Dateien belegt, der Inhalt ist vermutlich überschrieben.",
            DataAvailability.PartiallyReallocated => "Gelöscht. Ein Teil der Cluster wurde inzwischen neu belegt, der Inhalt ist teilweise überschrieben.",
            _ => "Gelöscht. Der Clusterstatus konnte nicht gelesen werden."
        };

        return CreateFile(raw, path, true, availability, contiguous.Runs, message);
    }

    private static async Task<DataAvailability> CheckFreeAsync(FatTable table, uint first, long count, CancellationToken cancellationToken)
    {
        long free = 0;

        for (long index = 0; index < count; index++)
        {
            var step = await table.ReadAsync((uint)(first + index), cancellationToken).ConfigureAwait(false);

            if (step.Link == FatLink.Unreadable)
            {
                return DataAvailability.Unknown;
            }

            if (step.Link == FatLink.Free)
            {
                free++;
            }
        }

        if (free == count)
        {
            return DataAvailability.Unallocated;
        }

        return free == 0 ? DataAvailability.Reallocated : DataAvailability.PartiallyReallocated;
    }

    private FileSystemEntry CreateFile(
        FatRawEntry raw,
        string path,
        bool deleted,
        DataAvailability availability,
        IReadOnlyList<DataRun> runs,
        string? note) =>
        new(raw.Name, path, raw.Size, false, deleted, raw.Modified, Type, availability, runs, null, note);
}
