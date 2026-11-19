using System.Buffers.Binary;
using RicoRecovery.Core.Abstractions;

namespace RicoRecovery.FileSystems.Fat;

internal enum FatKind
{
    Fat16,
    Fat32,
    ExFat
}

internal enum FatLink
{
    Next,
    End,
    Free,
    Bad,
    Invalid,
    Unreadable
}

internal readonly record struct FatStep(FatLink Link, uint Next);

internal sealed class FatTable
{
    private const int MaxCachedSectors = 512;

    private readonly IBlockSource source;
    private readonly long tableOffset;
    private readonly int bytesPerSector;
    private readonly FatKind kind;
    private readonly uint lastCluster;
    private readonly Dictionary<long, byte[]?> cache = [];

    public FatTable(IBlockSource source, long tableOffset, int bytesPerSector, FatKind kind, uint clusterCount)
    {
        this.source = source;
        this.tableOffset = tableOffset;
        this.bytesPerSector = bytesPerSector;
        this.kind = kind;
        lastCluster = clusterCount + 1;
    }

    public async ValueTask<FatStep> ReadAsync(uint cluster, CancellationToken cancellationToken)
    {
        if (cluster < 2 || cluster > lastCluster)
        {
            return new FatStep(FatLink.Invalid, 0);
        }

        var entrySize = kind == FatKind.Fat16 ? 2 : 4;
        var byteOffset = (long)cluster * entrySize;
        var sectorIndex = byteOffset / bytesPerSector;
        var inSector = (int)(byteOffset % bytesPerSector);

        if (!cache.TryGetValue(sectorIndex, out var sector))
        {
            sector = await BlockReader.ReadAsync(source, tableOffset + sectorIndex * bytesPerSector, bytesPerSector, cancellationToken).ConfigureAwait(false);

            if (cache.Count >= MaxCachedSectors)
            {
                cache.Clear();
            }

            cache[sectorIndex] = sector;
        }

        if (sector is null)
        {
            return new FatStep(FatLink.Unreadable, 0);
        }

        var raw = kind == FatKind.Fat16
            ? BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(inSector))
            : BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(inSector));

        if (kind == FatKind.Fat32)
        {
            raw &= 0x0FFFFFFF;
        }

        return Classify(raw);
    }

    private FatStep Classify(uint raw)
    {
        if (raw == 0)
        {
            return new FatStep(FatLink.Free, 0);
        }

        var (endMarker, badMarker) = kind switch
        {
            FatKind.Fat16 => (0xFFF8u, 0xFFF7u),
            FatKind.Fat32 => (0x0FFFFFF8u, 0x0FFFFFF7u),
            _ => (0xFFFFFFF8u, 0xFFFFFFF7u)
        };

        if (raw >= endMarker)
        {
            return new FatStep(FatLink.End, 0);
        }

        if (raw == badMarker)
        {
            return new FatStep(FatLink.Bad, 0);
        }

        if (raw >= 2 && raw <= lastCluster)
        {
            return new FatStep(FatLink.Next, raw);
        }

        return new FatStep(FatLink.Invalid, 0);
    }
}
