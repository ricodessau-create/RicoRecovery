namespace RicoRecovery.Core.Abstractions;

public interface IBlockSource : IDisposable
{
    string DisplayName { get; }

    long Length { get; }

    int SectorSize { get; }

    ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default);
}
