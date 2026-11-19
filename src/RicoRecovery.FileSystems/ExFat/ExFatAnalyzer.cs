using System.Runtime.CompilerServices;
using System.Text;
using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;
using RicoRecovery.FileSystems.Fat;

namespace RicoRecovery.FileSystems.ExFat;

public sealed class ExFatAnalyzer : IFileSystemAnalyzer, IFileSystemScanner
{
    private const long MaxDirectoryBytes = 64L * 1024 * 1024;

    public FileSystemType Type => FileSystemType.ExFat;

    public async Task<bool> ProbeAsync(IBlockSource source, long partitionOffset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var volume = await ExFatBootSector.ReadAsync(source, partitionOffset, cancellationToken).ConfigureAwait(false);

        return volume is not null;
    }

    public async Task<VolumeInfo?> ReadVolumeInfoAsync(IBlockSource source, long partitionOffset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var volume = await ExFatBootSector.ReadAsync(source, partitionOffset, cancellationToken).ConfigureAwait(false);

        if (volume is null)
        {
            return null;
        }

        var layout = new ExFatLayout(source, partitionOffset, volume);
        var root = new ExFatPendingDirectory(volume.RootCluster, null, false, string.Empty, false);
        var rootData = await LoadDirectoryAsync(layout, root, cancellationToken).ConfigureAwait(false);

        return new VolumeInfo(
            Type,
            ReadLabel(rootData),
            volume.VolumeSectors * volume.BytesPerSector,
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

        var volume = await ExFatBootSector.ReadAsync(source, partitionOffset, cancellationToken).ConfigureAwait(false);

        if (volume is null)
        {
            yield break;
        }

        var layout = new ExFatLayout(source, partitionOffset, volume);
        var root = new ExFatPendingDirectory(volume.RootCluster, null, false, string.Empty, false);
        var rootData = await LoadDirectoryAsync(layout, root, cancellationToken).ConfigureAwait(false);
        var bitmap = options.IncludeDeleted
            ? await ExFatBitmap.LoadAsync(layout, rootData, cancellationToken).ConfigureAwait(false)
            : null;
        var maxLength = (long)volume.ClusterCount * layout.ClusterBytes;
        var visited = new HashSet<uint> { volume.RootCluster };
        var pending = new Stack<ExFatPendingDirectory>();

        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directory = pending.Pop();
            var data = ReferenceEquals(directory, root)
                ? rootData
                : await LoadDirectoryAsync(layout, directory, cancellationToken).ConfigureAwait(false);

            foreach (var raw in ExFatDirectoryParser.Parse(data, volume.ClusterCount + 1, maxLength))
            {
                var deleted = raw.IsDeleted || directory.Deleted;
                var path = directory.Path + "/" + raw.Name;
                var wanted = deleted ? options.IncludeDeleted : options.IncludeActive;

                if (raw.IsDirectory)
                {
                    if (raw.Cluster >= 2 && visited.Add(raw.Cluster))
                    {
                        pending.Push(new ExFatPendingDirectory(raw.Cluster, raw.Length, raw.NoFatChain, path, deleted));
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
                    yield return await BuildFileAsync(layout, bitmap, raw, path, deleted, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task<byte[]> LoadDirectoryAsync(ExFatLayout layout, ExFatPendingDirectory directory, CancellationToken cancellationToken)
    {
        var clusterBytes = layout.ClusterBytes;
        var byteLimit = Math.Min(MaxDirectoryBytes, directory.Length ?? MaxDirectoryBytes);
        var maxClusters = Math.Max(1L, (byteLimit + clusterBytes - 1) / clusterBytes);
        ChainResult chain;

        if (directory.NoFatChain)
        {
            chain = FatChain.Contiguous(directory.Cluster, maxClusters, layout.OffsetOf, clusterBytes);
        }
        else
        {
            chain = await FatChain.FollowAsync(layout.Table, directory.Cluster, maxClusters, layout.OffsetOf, clusterBytes, cancellationToken).ConfigureAwait(false);

            if (directory.Deleted && directory.Length is not null && chain.Clusters < maxClusters)
            {
                chain = FatChain.Contiguous(directory.Cluster, maxClusters, layout.OffsetOf, clusterBytes);
            }
        }

        return await RunReader.ReadAllAsync(layout.Source, chain.Runs, byteLimit, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FileSystemEntry> BuildFileAsync(
        ExFatLayout layout,
        ExFatBitmap? bitmap,
        ExFatRawEntry raw,
        string path,
        bool deleted,
        CancellationToken cancellationToken)
    {
        if (raw.Length == 0)
        {
            return CreateFile(
                raw,
                path,
                deleted,
                deleted ? DataAvailability.Unallocated : DataAvailability.Allocated,
                [],
                deleted ? "Gelöscht. Die Datei ist leer." : null);
        }

        var clusterBytes = layout.ClusterBytes;
        var lastCluster = (long)layout.Volume.ClusterCount + 1;
        var needed = Math.Min((raw.Length + clusterBytes - 1) / clusterBytes, lastCluster - raw.Cluster + 1);
        string? note = null;
        ChainResult chain;

        if (raw.NoFatChain)
        {
            chain = FatChain.Contiguous(raw.Cluster, needed, layout.OffsetOf, clusterBytes);
        }
        else
        {
            chain = await FatChain.FollowAsync(layout.Table, raw.Cluster, needed, layout.OffsetOf, clusterBytes, cancellationToken).ConfigureAwait(false);

            if (!chain.Complete)
            {
                if (deleted)
                {
                    chain = FatChain.Contiguous(raw.Cluster, needed, layout.OffsetOf, clusterBytes);
                    note = "Die Clusterkette ist nicht mehr vorhanden, zusammenhängende Belegung angenommen.";
                }
                else
                {
                    note = "Die Clusterkette ist kürzer als die gespeicherte Dateigröße oder beschädigt.";
                }
            }
        }

        if (!deleted)
        {
            return CreateFile(raw, path, false, DataAvailability.Allocated, chain.Runs, note);
        }

        var availability = bitmap is null ? DataAvailability.Unknown : Evaluate(layout, bitmap, chain.Runs);

        var message = availability switch
        {
            DataAvailability.Unallocated => "Gelöscht. Die Cluster sind laut Belegungstabelle weiterhin frei.",
            DataAvailability.Reallocated => "Gelöscht. Die Cluster wurden inzwischen neu belegt, der Inhalt ist vermutlich überschrieben.",
            DataAvailability.PartiallyReallocated => "Gelöscht. Ein Teil der Cluster wurde inzwischen neu belegt, der Inhalt ist teilweise überschrieben.",
            _ => "Gelöscht. Der Clusterstatus konnte nicht ermittelt werden."
        };

        return CreateFile(raw, path, true, availability, chain.Runs, note is null ? message : note + " " + message);
    }

    private static DataAvailability Evaluate(ExFatLayout layout, ExFatBitmap bitmap, IReadOnlyList<DataRun> runs)
    {
        long total = 0;
        long allocated = 0;

        foreach (var run in runs)
        {
            var first = (run.Offset - layout.HeapOffset) / layout.ClusterBytes + 2;
            var count = run.Length / layout.ClusterBytes;

            for (long index = 0; index < count; index++)
            {
                var state = bitmap.IsAllocated((uint)(first + index));

                if (state is null)
                {
                    return DataAvailability.Unknown;
                }

                total++;

                if (state.Value)
                {
                    allocated++;
                }
            }
        }

        if (allocated == 0)
        {
            return DataAvailability.Unallocated;
        }

        return allocated == total ? DataAvailability.Reallocated : DataAvailability.PartiallyReallocated;
    }

    private static string? ReadLabel(byte[] rootDirectory)
    {
        var count = rootDirectory.Length / 32;

        for (var index = 0; index < count; index++)
        {
            var offset = index * 32;
            var type = rootDirectory[offset];

            if (type == 0x00)
            {
                break;
            }

            if (type != 0x83)
            {
                continue;
            }

            var length = Math.Min((int)rootDirectory[offset + 1], 11);
            var label = Encoding.Unicode.GetString(rootDirectory, offset + 2, length * 2);

            return label.Length == 0 ? null : label;
        }

        return null;
    }

    private FileSystemEntry CreateFile(
        ExFatRawEntry raw,
        string path,
        bool deleted,
        DataAvailability availability,
        IReadOnlyList<DataRun> runs,
        string? note) =>
        new(raw.Name, path, raw.Length, false, deleted, raw.Modified, Type, availability, runs, null, note);
}
