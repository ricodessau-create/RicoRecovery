using RicoRecovery.Core.Safety;
using Xunit;

namespace RicoRecovery.Tests;

public sealed class SafetyGuardTests
{
    [Fact]
    public void SameDriveIsUnsafe()
    {
        var result = SafetyGuard.EvaluateDestination(@"E:\", @"E:\Wiederhergestellt");

        Assert.False(result.IsSafe);
        Assert.NotEmpty(result.Message);
    }

    [Fact]
    public void SameDriveWithDifferentCaseIsUnsafe()
    {
        var result = SafetyGuard.EvaluateDestination(@"e:\", @"E:\Ziel");

        Assert.False(result.IsSafe);
    }

    [Fact]
    public void DifferentDriveIsSafe()
    {
        var result = SafetyGuard.EvaluateDestination(@"E:\", @"D:\Wiederhergestellt");

        Assert.True(result.IsSafe);
    }

    [Fact]
    public void EmptyPathThrows()
    {
        Assert.Throws<ArgumentException>(() => SafetyGuard.EvaluateDestination(" ", @"D:\Ziel"));
    }
}
