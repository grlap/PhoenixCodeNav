using System.Diagnostics;
using CodeNav.FSharp;

namespace CodeNav.Core.Semantic;

public sealed partial class SemanticService
{
    /// <summary>Observed FCS request spans, not a cold/cache verdict or an additive total.
    /// Null means not entered; zero means entered for less than one millisecond.</summary>
    public sealed record FSharpSemanticTiming(
        long? AdmissionWaitMs,
        long? SnapshotCaptureMs,
        long? FcsSetupMs,
        long? ProjectParseAndCheckMs,
        long? FileParseAndCheckMs)
    {
        public string Engine => "fcs";
    }

    /// <summary>Explicit request-local carrier. A frozen snapshot includes elapsed time in
    /// interrupted spans and cannot be changed by late FCS completion or disposal.
    /// Same-phase spans are sequential by contract; overlapping starts are unsupported.</summary>
    public sealed class FSharpSemanticTimingBox : ISemanticTiming
    {
        private readonly object _sync = new();
        private readonly long[] _ticks = new long[5];
        private readonly long?[] _started = new long?[5];
        private readonly bool[] _entered = new bool[5];

        internal IDisposable Admission() => Start(0);
        internal IDisposable Capture() => Start(1);
        IDisposable ISemanticTiming.StartPhase(SemanticPhase phase) => Start((int)phase + 2);

        private IDisposable Start(int phase)
        {
            lock (_sync)
            {
                _entered[phase] = true;
                _started[phase] = Stopwatch.GetTimestamp();
            }
            return new Span(this, phase);
        }

        private sealed class Span(FSharpSemanticTimingBox owner, int phase) : IDisposable
        {
            private bool _disposed;
            public void Dispose()
            {
                lock (owner._sync)
                {
                    if (_disposed) return;
                    _disposed = true;
                    owner._ticks[phase] += Stopwatch.GetTimestamp() - owner._started[phase]!.Value;
                    owner._started[phase] = null;
                }
            }
        }

        public FSharpSemanticTiming? Snapshot()
        {
            lock (_sync)
            {
                if (!_entered[0]) return null;
                long now = Stopwatch.GetTimestamp();
                long? Milliseconds(int phase) => !_entered[phase] ? null :
                    (long)((_ticks[phase] + (_started[phase] is long start ? now - start : 0)) *
                           (1000.0 / Stopwatch.Frequency));
                return new(Milliseconds(0), Milliseconds(1), Milliseconds(2),
                    Milliseconds(3), Milliseconds(4));
            }
        }
    }

    internal void EmitFSharpTelemetry(string tool, string result, string? reason,
        FSharpSemanticTiming? semanticColdStart)
    {
        _manager.Telemetry.Emit(new
        {
            e = "semanticOp",
            ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ",
                System.Globalization.CultureInfo.InvariantCulture),
            corr = Guid.NewGuid().ToString("N")[..8],
            tool,
            accessMode = _manager.IsWriter ? "writer" : _manager.IsFollower ? "follower" : "unattached",
            result,
            reason,
            semanticColdStart,
        });
    }
}
