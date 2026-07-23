using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace CodeNav.Core.Semantic;

/// <summary>
/// Process-wide CPU snapshots for diagnostic attribution. These values intentionally include
/// every managed/runtime thread in the MCP process, so wall time remains the duration authority.
/// Measurement must never replace a semantic result; unsupported process counters degrade to zero.
/// </summary>
internal static class SemanticProcessCpu
{
    internal static TimeSpan? Snapshot()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            return process.TotalProcessorTime;
        }
        catch
        {
            return null;
        }
    }

    internal static double? ElapsedMilliseconds(TimeSpan? started)
    {
        if (started is not { } start || Snapshot() is not { } finished) return null;
        return Math.Max(0, (finished - start).TotalMilliseconds);
    }
}

/// <summary>
/// EventPipe-visible semantic phase boundaries. Markers contain only a fixed phase name and the
/// privacy-safe operation correlation id also published on the matching semanticOp record.
/// </summary>
[EventSource(Name = "PhoenixCodeNav-Semantic")]
internal sealed class SemanticPhaseEventSource : EventSource
{
    internal static readonly SemanticPhaseEventSource Log = new();

    private SemanticPhaseEventSource()
    {
    }

    [Event(1, Level = EventLevel.Informational)]
    public void PhaseStart(string phaseName, string operationId) =>
        WriteEvent(1, phaseName, operationId);

    [Event(2, Level = EventLevel.Informational)]
    public void PhaseStop(string phaseName, string operationId) =>
        WriteEvent(2, phaseName, operationId);

    [NonEvent]
    internal PhaseScope Measure(string phaseName, string? operationId)
    {
        if (operationId is null || !IsEnabled()) return default;
        PhaseStart(phaseName, operationId);
        return new PhaseScope(this, phaseName, operationId);
    }

    internal readonly struct PhaseScope : IDisposable
    {
        private readonly SemanticPhaseEventSource? _source;
        private readonly string? _phaseName;
        private readonly string? _operationId;

        internal PhaseScope(SemanticPhaseEventSource source, string phaseName, string operationId)
        {
            _source = source;
            _phaseName = phaseName;
            _operationId = operationId;
        }

        public void Dispose()
        {
            if (_source is not null)
                _source.PhaseStop(_phaseName!, _operationId!);
        }
    }
}
