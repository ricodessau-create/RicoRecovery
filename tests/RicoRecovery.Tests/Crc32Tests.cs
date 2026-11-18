using System.Text;
using RicoRecovery.Storage.Partitioning;
using Xunit;

namespace RicoRecovery.Tests;

public sealed class Crc32Tests
{
    [Fact]
    public void KnownCheckValueMatches()
    {
        var result = Crc32.Compute(Encoding.ASCII.GetBytes("123456789"));

        Assert.Equal(0xCBF43926u, result);
    }

    [Fact]
    public void EmptyInputYieldsZero()
    {
        Assert.Equal(0u, Crc32.Compute([]));
    }
}
