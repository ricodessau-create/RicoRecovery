using RicoRecovery.Recovery;
using Xunit;

namespace RicoRecovery.Tests;

public sealed class ScanProgressTrackerTests
{
    [Fact]
    public void SnapshotReportsFractionAndFileCount()
    {
        var tracker = new ScanProgressTracker(1000);

        tracker.Advance(250);
        tracker.RegisterFile();
        tracker.RegisterFile();

        var snapshot = tracker.Snapshot();

        Assert.Equal(250, snapshot.BytesProcessed);
        Assert.Equal(2, snapshot.FilesFound);
        Assert.Equal(0.25, snapshot.Fraction, 6);
    }

    [Fact]
    public void FractionNeverExceedsOne()
    {
        var tracker = new ScanProgressTracker(100);

        tracker.Advance(500);

        Assert.Equal(1.0, tracker.Snapshot().Fraction);
    }

    [Fact]
    public void ZeroTotalYieldsZeroFraction()
    {
        var tracker = new ScanProgressTracker(0);

        Assert.Equal(0.0, tracker.Snapshot().Fraction);
    }

    [Fact]
    public async Task ConcurrentAdvanceIsConsistent()
    {
        var tracker = new ScanProgressTracker(1_000_000);

        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                tracker.Advance(1);
                tracker.RegisterFile();
            }
        })));

        var snapshot = tracker.Snapshot();

        Assert.Equal(100_000, snapshot.BytesProcessed);
        Assert.Equal(100_000, snapshot.FilesFound);
    }

    [Fact]
    public void NegativeAdvanceThrows()
    {
        var tracker = new ScanProgressTracker(100);

        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.Advance(-1));
    }
}
