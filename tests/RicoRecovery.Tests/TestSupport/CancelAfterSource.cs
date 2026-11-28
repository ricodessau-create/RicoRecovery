using RicoRecovery.Core.Abstractions;

namespace RicoRecovery.Tests.TestSupport;

internal sealed class CancelAfterSource(IBlockSource inner, CancellationTokenSource cancellation, int readsBeforeCancel) : IBlockSource
{
    private int reads;

    public string DisplayName => inner.DisplayName;

    public long Length => inner.Length;

    public int SectorSize => inner.SectorSize;

    public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (++reads == readsBeforeCancel)
        {
            cancellation.Cancel();
        }

        return inner.ReadAsync(offset, buffer, cancellationToken);
    }

    public void Dispose() => inner.Dispose();
}
