using System.Runtime.CompilerServices;
using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;

namespace RicoRecovery.FileSystems.Ext;

public sealed class ExtAnalyzer : IFileSystemAnalyzer, IFileSystemScanner
{
    private const long MaxDirectoryBytes = 64L * 1024 * 1024;
    private const int InodeChunkBytes = 1 << 20;

    public ExtAnalyzer(FileSystemType type)
    {
        if (type is not (FileSystemType.Ext2 or FileSystemType.Ext3 or FileSystemType.Ext4))
        {
            throw new ArgumentOutOfRangeException(nameof(type), "ExtAnalyzer unterstützt nur EXT2, EXT3 und EXT4.");
        }

        Type = type;
    }

    public FileSystemType Type { get; }

    public async Task<bool> ProbeAsync(IBlockSource source, long partitionOffset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var volume = await ExtSuperblock.ReadAsync(source, partitionOffset, cancellationToken).ConfigureAwait(false);

        return volume is not null && volume.Type == Type;
    }

    public async Task<VolumeInfo?> ReadVolumeInfoAsync(IBlockSource source, long partitionOffset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var volume = await ExtSuperblock.ReadAsync(source, partitionOffset, cancellationToken).ConfigureAwait(false);

        if (volume is null || volume.Type != Type)
        {
            return null;
        }

        var notes = new List<string>();

        if (volume.FromBackup)
        {
            notes.Add("Der Superblock wurde aus einer Sicherungskopie gelesen.");
        }

        if (volume.UnsupportedReason is not null)
        {
            notes.Add(volume.UnsupportedReason);
        }

        return new VolumeInfo(
            Type,
            volume.Label.Length == 0 ? null : volume.Label,
            volume.BlocksCount * volume.BlockSize,
            volume.BlockSize,
            notes.Count == 0 ? null : string.Join(' ', notes));
    }

    public async IAsyncEnumerable<FileSystemEntry> EnumerateAsync(
        IBlockSource source,
        long partitionOffset,
        FileSystemScanOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        var volume = await ExtSuperblock.ReadAsync(source, partitionOffset, cancellationToken).ConfigureAwait(false);

        if (volume is null || volume.Type != Type || volume.UnsupportedReason is not null)
        {
            yield break;
        }

        var groups = await ExtGroupTable.ReadAsync(source, partitionOffset, volume, cancellationToken).ConfigureAwait(false);

        if (groups is null)
        {
            yield break;
        }

        var context = new ExtScanContext(source, partitionOffset, volume, groups);
        var hints = new Dictionary<uint, (string Name, string Directory)>();
        var visited = new HashSet<uint> { 2 };
        var pending = new Stack<(uint Inode, string Path)>();

        pending.Push((2, string.Empty));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (directoryInode, directoryPath) = pending.Pop();
            var inode = await context.Inodes.ReadAsync(directoryInode, cancellationToken).ConfigureAwait(false);

            if (inode is null || !inode.IsDirectory)
            {
                continue;
            }

            var mapping = await context.Mapper.MapAsync(inode, MaxDirectoryBytes, cancellationToken).ConfigureAwait(false);
            var data = await RunReader.ReadAllAsync(source, mapping.Runs, MaxDirectoryBytes, cancellationToken).ConfigureAwait(false);

            foreach (var raw in ExtDirectoryParser.Parse(data, volume.BlockSize, volume.InodesCount, options.IncludeDeleted))
            {
                if (raw.IsSlack)
                {
                    hints.TryAdd(raw.Inode, (raw.Name, directoryPath));
                    continue;
                }

                var child = await context.Inodes.ReadAsync(raw.Inode, cancellationToken).ConfigureAwait(false);

                if (child is null)
                {
                    continue;
                }

                var path = directoryPath + "/" + raw.Name;

                if (child.IsDirectory)
                {
                    if (visited.Add(raw.Inode))
                    {
                        pending.Push((raw.Inode, path));
                    }

                    if (options.IncludeActive)
                    {
                        yield return new FileSystemEntry(
                            raw.Name,
                            path,
                            0,
                            true,
                            false,
                            EntryTimes.FromUnixSeconds(child.ModifiedTime),
                            Type,
                            DataAvailability.Allocated,
                            [],
                            null,
                            null);
                    }

                    continue;
                }

                if (!child.IsRegular || !options.IncludeActive)
                {
                    continue;
                }

                var file = await BuildFileAsync(context, child, raw.Name, path, false, cancellationToken).ConfigureAwait(false);

                if (file is not null)
                {
                    yield return file;
                }
            }
        }

        if (!options.IncludeDeleted)
        {
            yield break;
        }

        var inodesPerChunk = Math.Max(1, InodeChunkBytes / volume.InodeSize);

        for (var group = 0; group < groups.Count; group++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var descriptor = groups[group];

            if (descriptor.InodeTable <= 0 || (volume.HasGroupChecksums && (descriptor.Flags & 0x1) != 0))
            {
                continue;
            }

            long usable = volume.InodesPerGroup;

            if (volume.HasGroupChecksums && descriptor.ItableUnused <= usable)
            {
                usable -= descriptor.ItableUnused;
            }

            for (long start = 0; start < usable; start += inodesPerChunk)
            {
                var chunkCount = (int)Math.Min(inodesPerChunk, usable - start);
                var tableOffset = partitionOffset + descriptor.InodeTable * volume.BlockSize + start * volume.InodeSize;
                var buffer = await BlockReader.ReadAsync(source, tableOffset, chunkCount * volume.InodeSize, cancellationToken).ConfigureAwait(false);

                if (buffer is null)
                {
                    continue;
                }

                for (var index = 0; index < chunkCount; index++)
                {
                    var number = (long)group * volume.InodesPerGroup + start + index + 1;

                    if (number < volume.FirstInode || number > volume.InodesCount)
                    {
                        continue;
                    }

                    var candidate = ExtInode.Parse((uint)number, buffer, index * volume.InodeSize);

                    if (candidate is null || !candidate.IsDeleted || !candidate.IsRegular || candidate.Size == 0)
                    {
                        continue;
                    }

                    string name;
                    string path;

                    if (hints.TryGetValue((uint)number, out var hint))
                    {
                        name = hint.Name;
                        path = hint.Directory + "/" + hint.Name;
                    }
                    else
                    {
                        name = "Inode_" + number;
                        path = "/(Gelöscht)/" + name;
                    }

                    var file = await BuildFileAsync(context, candidate, name, path, true, cancellationToken).ConfigureAwait(false);

                    if (file is not null)
                    {
                        yield return file;
                    }
                }
            }
        }
    }

    private async Task<FileSystemEntry?> BuildFileAsync(
        ExtScanContext context,
        ExtInode inode,
        string name,
        string path,
        bool deleted,
        CancellationToken cancellationToken)
    {
        var modified = EntryTimes.FromUnixSeconds(inode.ModifiedTime);

        if (inode.HasInlineData)
        {
            if (inode.Size < 0 || inode.Size > 60)
            {
                return deleted
                    ? null
                    : new FileSystemEntry(name, path, inode.Size, false, false, modified, Type, DataAvailability.Unknown, [], null, "Inline-Daten außerhalb des Inodes werden nicht unterstützt.");
            }

            return new FileSystemEntry(
                name,
                path,
                inode.Size,
                false,
                deleted,
                modified,
                Type,
                deleted ? DataAvailability.Unallocated : DataAvailability.Allocated,
                [],
                inode.Blocks.AsSpan(0, (int)inode.Size).ToArray(),
                deleted ? "Gelöscht. Der Inhalt liegt im Inode." : null);
        }

        var mapping = await context.Mapper.MapAsync(inode, long.MaxValue, cancellationToken).ConfigureAwait(false);

        if (!deleted)
        {
            return new FileSystemEntry(
                name,
                path,
                inode.Size,
                false,
                false,
                modified,
                Type,
                DataAvailability.Allocated,
                mapping.Runs,
                null,
                mapping.Complete ? null : "Die Blockzuordnung ist unvollständig oder beschädigt.");
        }

        if (mapping.Runs.Count == 0)
        {
            return null;
        }

        var availability = await context.Bitmaps.EvaluateAsync(mapping.Runs, cancellationToken).ConfigureAwait(false);

        var message = availability switch
        {
            DataAvailability.Unallocated => "Gelöscht. Die Blockzeiger stammen aus dem Inode, die Blöcke sind laut Bitmap weiterhin frei.",
            DataAvailability.Reallocated => "Gelöscht. Die Blöcke wurden inzwischen neu belegt, der Inhalt ist vermutlich überschrieben.",
            DataAvailability.PartiallyReallocated => "Gelöscht. Ein Teil der Blöcke wurde inzwischen neu belegt, der Inhalt ist teilweise überschrieben.",
            _ => "Gelöscht. Der Blockstatus konnte nicht ermittelt werden."
        };

        return new FileSystemEntry(
            name,
            path,
            inode.Size,
            false,
            true,
            modified,
            Type,
            availability,
            mapping.Runs,
            null,
            mapping.Complete ? message : message + " Die Blockzuordnung ist unvollständig.");
    }
}
