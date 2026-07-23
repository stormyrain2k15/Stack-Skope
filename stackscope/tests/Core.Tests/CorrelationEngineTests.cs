using StackScope.Core.Correlation;
using StackScope.Core.Transactions;
using Xunit;

namespace StackScope.Core.Tests;

public class CorrelationEngineTests
{
    [Fact]
    public void DirectMatch_Wins_Over_Time()
    {
        var workerMarker = new TraceMarker("layer.7", 1000, 2000, 0, 1, 0, 42);
        var worker = new TransactionEvent(
            10, "T", 1500, EventKind.LayerBegin,
            1, 7, -1, 1, 0, 0, Array.Empty<byte>(), new[] { workerMarker });

        var driverMarker = new TraceMarker("cuda.kernel", 1500, 1800, 0, 1, 0, 42);
        var driver = new TransactionEvent(
            100, "T", 1500, EventKind.KernelLaunch,
            -1, -1, -1, 1, 0, 0, Array.Empty<byte>(), new[] { driverMarker });

        var engine = new CorrelationEngine(new[] { worker }, new[] { driver });
        var pair = Assert.Single(engine.Correlate());
        Assert.Equal(CorrelationConfidence.Direct, pair.Confidence);
        Assert.Equal(worker, pair.Left);
        Assert.Equal(driver, pair.Right);
    }

    [Fact]
    public void MarkerContainment_When_No_Direct_Id()
    {
        var workerMarker = new TraceMarker("layer.3", 5000, 9000, 0, 1, 0, /*no corr id*/ 0);
        var worker = new TransactionEvent(
            1, "T", 5000, EventKind.LayerBegin,
            0, 3, -1, 1, 0, 0, Array.Empty<byte>(), new[] { workerMarker });
        // Give the marker back a nonzero corr id so it registers under
        // markersByThread but not markerById (correlation id path fails
        // because the driver-side has correlation_id = 0).
        var workerMarker2 = new TraceMarker("layer.3", 5000, 9000, 0, 1, 0, 77);
        var worker2 = worker with { Markers = new[] { workerMarker2 } };

        var driverMarker = new TraceMarker("cuda.kernel", 6000, 6500, 0, 1, 0, /*no match*/ 999);
        var driver = new TransactionEvent(
            2, "T", 6200, EventKind.KernelLaunch,
            -1, -1, -1, 1, 0, 0, Array.Empty<byte>(), new[] { driverMarker });

        var engine = new CorrelationEngine(new[] { worker2 }, new[] { driver });
        var pair = Assert.Single(engine.Correlate());
        Assert.Equal(CorrelationConfidence.MarkerCorrelated, pair.Confidence);
    }

    [Fact]
    public void FallsBackTo_TimeCorrelated_When_Same_Stream()
    {
        var worker = new TransactionEvent(
            1, "T", 1_000_000, EventKind.Activation,
            -1, -1, -1, 1, 3, 0, Array.Empty<byte>(),
            new[] { new TraceMarker("a", 0, 0, 0, 99, 99, 5) }); // unrelated markers

        var driver = new TransactionEvent(
            2, "T", 1_000_500, EventKind.KernelLaunch,
            -1, -1, -1, 1, 3, 0, Array.Empty<byte>(), Array.Empty<TraceMarker>());

        var engine = new CorrelationEngine(new[] { worker }, new[] { driver });
        var pair = Assert.Single(engine.Correlate());
        Assert.Equal(CorrelationConfidence.TimeCorrelated, pair.Confidence);
    }
}
