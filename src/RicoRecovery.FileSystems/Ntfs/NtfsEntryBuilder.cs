using System.Buffers.Binary;
using System.Text;
using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;

namespace RicoRecovery.FileSystems.Ntfs;

internal sealed record NtfsFileName(long Parent, string Name, byte Namespace, DateTimeOffset? Modified);

internal sealed record NtfsDirectoryNode(string Name, long Parent);

internal sealed class NtfsEntryBuilder
{
    private const long MaxBitmapBytes = 256L * 1024 * 1024;
    private const long MaxAttributeListBytes = 1L << 20;
    private const int MaxCachedDirectories = 100_000;
    private const long RootRecord = 5;
    private const int FirstUserRecord = 24;

    private readonly IBlockSource source;
    private readonly long partitionOffset;
    private readonly NtfsVolume volume;
    private readonly NtfsMft mft;
    private readonly byte[]? bitmap;
    private readonly Dictionary<long, NtfsDirectoryNode?> directories = [];

    private NtfsEntryBuilder(IBlockSource source, long partitionOffset, NtfsVolume volume, NtfsMft mft, byte[]? bitmap)
    {
        this.source = source;
        this.partitionOffset = partitionOffset;
        this.volume = volume;
        this.mft = mft;
        this.bitmap = bitmap;
    }

    public static async Task<NtfsEntryBuilder> CreateAsync(
        IBlockSource source,
        long partitionOffset,
        NtfsVolume volume,
        NtfsMft mft,
        CancellationToken cancellationToken)
    {
        var bitmap = await LoadBitmapAsync(source, volume, mft, cancellationToken).ConfigureAwait(false);

        return new NtfsEntryBuilder(source, partitionOffset, volume, mft, bitmap);
    }

    public async Task<FileSystemEntry?> BuildAsync(NtfsRecord record, CancellationToken cancellationToken)
    {
        if (record.Number < FirstUserRecord || record.BaseRecord != 0)
        {
            return null;
        }

        var fileName = SelectName(record);

        if (fileName is null)
        {
            return null;
        }

        var deleted = !record.InUse;
        var parentPath = await ResolveParentPathAsync(fileName.Parent, cancellationToken).ConfigureAwait(false);
        var name = EntryNames.Clean(fileName.Name);
        var path = parentPath + "/" + name;
        var modified = ReadModified(record) ?? fileName.Modified;

        if (record.IsDirectory)
        {
            return new FileSystemEntry(
                name,
                path,
                0,
                true,
                deleted,
                modified,
                FileSystemType.Ntfs,
                deleted ? DataAvailability.Unknown : DataAvailability.Allocated,
                [],
                null,
                null);
        }

        return await BuildFileAsync(record, name, path, deleted, modified, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FileSystemEntry> BuildFileAsync(
        NtfsRecord record,
        string name,
        string path,
        bool deleted,
        DateTimeOffset? modified,
        CancellationToken cancellationToken)
    {
        var extents = record.Attributes.Where(IsUnnamedData).ToList();
        var attributeList = record.Attributes.FirstOrDefault(attribute => attribute.Type == 0x20);

        if (attributeList is not null)
        {
            extents.AddRange(await LoadExtensionDataAsync(record, attributeList, cancellationToken).ConfigureAwait(false));
        }

        extents.Sort((left, right) => left.StartVcn.CompareTo(right.StartVcn));

        if (extents.Count == 0)
        {
            return Create(name, path, 0, deleted, modified, DataAvailability.Unknown, [], null, "Das Datenattribut wurde nicht gefunden.");
        }

        var first = extents[0];

        if (!first.NonResident)
        {
            var value = first.Value ?? Array.Empty<byte>();

            return Create(
                name,
                path,
                value.Length,
                deleted,
                modified,
                deleted ? DataAvailability.Unallocated : DataAvailability.Allocated,
                [],
                value,
                deleted ? "Gelöscht. Der Inhalt liegt im MFT-Eintrag und ist solange erhalten, bis der Eintrag wiederverwendet wird." : null);
        }

        if (first.StartVcn != 0)
        {
            return Create(name, path, first.RealSize, deleted, modified, DataAvailability.Unknown, [], null, "Der Anfang der Datenläufe fehlt, ein Erweiterungseintrag ist nicht auflösbar.");
        }

        var compressed = (first.Flags & 0x0001) != 0 || first.CompressionUnit != 0;
        var encrypted = (first.Flags & 0x4000) != 0;

        if (compressed || encrypted)
        {
            return Create(
                name,
                path,
                first.RealSize,
                deleted,
                modified,
                DataAvailability.Unknown,
                [],
                null,
                compressed ? "Komprimierte NTFS-Dateien werden nicht unterstützt." : "Verschlüsselte NTFS-Dateien (EFS) werden nicht unterstützt.");
        }

        var runs = extents.SelectMany(attribute => attribute.Runs).ToList();

        if (!deleted)
        {
            return Create(name, path, first.RealSize, false, modified, DataAvailability.Allocated, runs, null, null);
        }

        var availability = EvaluateBitmap(runs);

        var message = availability switch
        {
            DataAvailability.Unallocated => "Gelöscht. Die Cluster sind laut $Bitmap weiterhin frei.",
            DataAvailability.Reallocated => "Gelöscht. Die Cluster wurden inzwischen neu belegt, der Inhalt ist vermutlich überschrieben.",
            DataAvailability.PartiallyReallocated => "Gelöscht. Ein Teil der Cluster wurde inzwischen neu belegt, der Inhalt ist teilweise überschrieben.",
            _ => "Gelöscht. Der Clusterstatus konnte nicht ermittelt werden."
        };

        return Create(name, path, first.RealSize, true, modified, availability, runs, null, message);
    }

    private async Task<List<NtfsAttribute>> LoadExtensionDataAsync(NtfsRecord record, NtfsAttribute list, CancellationToken cancellationToken)
    {
        var result = new List<NtfsAttribute>();
        byte[]? content;

        if (list.NonResident)
        {
            if (list.RealSize > MaxAttributeListBytes)
            {
                return result;
            }

            content = await RunReader.ReadAllAsync(source, list.Runs, list.RealSize, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            content = list.Value;
        }

        if (content is null)
        {
            return result;
        }

        var referenced = new HashSet<long>();
        var position = 0;

        while (position + 26 <= content.Length)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(position));
            int length = BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan(position + 4));
            var nameLength = content[position + 6];
            var reference = (long)(BinaryPrimitives.ReadUInt64LittleEndian(content.AsSpan(position + 16)) & 0x0000FFFFFFFFFFFF);

            if (length < 26)
            {
                break;
            }

            if (type == 0x80 && nameLength == 0 && reference != record.Number)
            {
                referenced.Add(reference);
            }

            position += length;
        }

        foreach (var number in referenced)
        {
            var extension = await mft.ReadRecordAsync(number, cancellationToken).ConfigureAwait(false);

            if (extension is null || extension.BaseRecord != record.Number)
            {
                continue;
            }

            result.AddRange(extension.Attributes.Where(IsUnnamedData));
        }

        return result;
    }

    private DataAvailability EvaluateBitmap(IReadOnlyList<DataRun> runs)
    {
        if (bitmap is null)
        {
            return DataAvailability.Unknown;
        }

        long total = 0;
        long allocated = 0;

        foreach (var run in runs)
        {
            if (run.IsSparse)
            {
                continue;
            }

            var first = (run.Offset - partitionOffset) / volume.ClusterBytes;
            var count = run.Length / volume.ClusterBytes;

            for (long index = 0; index < count; index++)
            {
                var cluster = first + index;
                var byteIndex = cluster >> 3;

                if (byteIndex >= bitmap.Length)
                {
                    return DataAvailability.Unknown;
                }

                total++;

                if ((bitmap[byteIndex] & (1 << (int)(cluster & 7))) != 0)
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

    private async Task<string> ResolveParentPathAsync(long parent, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        var current = parent;

        for (var depth = 0; depth < 64; depth++)
        {
            if (current == RootRecord)
            {
                return BuildPath(names);
            }

            var node = await GetNodeAsync(current, cancellationToken).ConfigureAwait(false);

            if (node is null)
            {
                break;
            }

            names.Add(node.Name);
            current = node.Parent;
        }

        return "/(Verlorener Pfad)" + BuildPath(names);
    }

    private async Task<NtfsDirectoryNode?> GetNodeAsync(long number, CancellationToken cancellationToken)
    {
        if (directories.TryGetValue(number, out var cached))
        {
            return cached;
        }

        var record = await mft.ReadRecordAsync(number, cancellationToken).ConfigureAwait(false);
        NtfsDirectoryNode? node = null;

        if (record is not null && record.IsDirectory)
        {
            var fileName = SelectName(record);

            if (fileName is not null)
            {
                node = new NtfsDirectoryNode(EntryNames.Clean(fileName.Name), fileName.Parent);
            }
        }

        if (directories.Count >= MaxCachedDirectories)
        {
            directories.Clear();
        }

        directories[number] = node;

        return node;
    }

    private static string BuildPath(List<string> names)
    {
        var builder = new StringBuilder();

        for (var index = names.Count - 1; index >= 0; index--)
        {
            builder.Append('/').Append(names[index]);
        }

        return builder.ToString();
    }

    private static async Task<byte[]?> LoadBitmapAsync(IBlockSource source, NtfsVolume volume, NtfsMft mft, CancellationToken cancellationToken)
    {
        var record = await mft.ReadRecordAsync(6, cancellationToken).ConfigureAwait(false);
        var data = record?.Attributes.FirstOrDefault(IsUnnamedData);

        if (data is null)
        {
            return null;
        }

        var required = (volume.TotalSectors / volume.SectorsPerCluster + 7) / 8;

        if (!data.NonResident)
        {
            return data.Value is { } value && value.Length >= required ? value : null;
        }

        if (data.RealSize > MaxBitmapBytes || data.RealSize < required)
        {
            return null;
        }

        var bytes = await RunReader.ReadAllAsync(source, data.Runs, data.RealSize, cancellationToken).ConfigureAwait(false);

        return bytes.Length >= required ? bytes : null;
    }

    private static DateTimeOffset? ReadModified(NtfsRecord record)
    {
        var attribute = record.Attributes.FirstOrDefault(candidate => candidate.Type == 0x10 && !candidate.NonResident);

        if (attribute?.Value is not { Length: >= 24 } value)
        {
            return null;
        }

        return EntryTimes.FromFileTime(BinaryPrimitives.ReadInt64LittleEndian(value.AsSpan(8)));
    }

    private static NtfsFileName? SelectName(NtfsRecord record)
    {
        NtfsFileName? best = null;

        foreach (var attribute in record.Attributes)
        {
            if (attribute.Type != 0x30 || attribute.NonResident || attribute.Value is null)
            {
                continue;
            }

            var candidate = ParseFileName(attribute.Value);

            if (candidate is not null && (best is null || Rank(candidate.Namespace) < Rank(best.Namespace)))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static NtfsFileName? ParseFileName(byte[] value)
    {
        if (value.Length < 66)
        {
            return null;
        }

        var nameLength = value[64];

        if (66 + nameLength * 2 > value.Length)
        {
            return null;
        }

        var parent = (long)(BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(0)) & 0x0000FFFFFFFFFFFF);
        var modified = EntryTimes.FromFileTime(BinaryPrimitives.ReadInt64LittleEndian(value.AsSpan(16)));

        return new NtfsFileName(parent, Encoding.Unicode.GetString(value, 66, nameLength * 2), value[65], modified);
    }

    private static int Rank(byte fileNamespace) => fileNamespace switch
    {
        1 or 3 => 0,
        0 => 1,
        _ => 2
    };

    private static bool IsUnnamedData(NtfsAttribute attribute) => attribute.Type == 0x80 && attribute.Name.Length == 0;

    private static FileSystemEntry Create(
        string name,
        string path,
        long size,
        bool deleted,
        DateTimeOffset? modified,
        DataAvailability availability,
        IReadOnlyList<DataRun> runs,
        byte[]? resident,
        string? note) =>
        new(name, path, size, false, deleted, modified, FileSystemType.Ntfs, availability, runs, resident, note);
}
