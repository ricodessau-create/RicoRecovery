using System.Security.Cryptography;
using RicoRecovery.Core.Abstractions;
using RicoRecovery.Core.Models;
using RicoRecovery.FileSystems;

namespace RicoRecovery.Recovery.Carving;

public static class ContentHasher
{
    public static async Task<string> ComputeAsync(IBlockSource source, FileSystemEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(entry);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new HashingStream(hash);

        await EntryContentReader.CopyToAsync(source, entry, stream, cancellationToken).ConfigureAwait(false);

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class HashingStream(IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            hash.AppendData(buffer.Span);

            return ValueTask.CompletedTask;
        }
    }
}
