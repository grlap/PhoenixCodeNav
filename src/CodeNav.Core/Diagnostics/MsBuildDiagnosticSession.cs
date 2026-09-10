using System.Reflection;
using System.Text.Json;

namespace CodeNav.Core.Diagnostics;

/// <summary>
/// Opt-in, local raw-expression diagnostics, NOT privacy-safe telemetry. A separate filename
/// keeps these records out of the portal's phoenix-* input. No background queue or retained file
/// handle: enabled diagnostics pay synchronous append costs and have no size/retention cap.
/// Callers own enablement and workspace authority; expression evaluation only receives a session.
/// </summary>
internal sealed class MsBuildDiagnosticSession : IDisposable
{
    internal const string EnvironmentVariable = "PHOENIX_MSBUILD_DIAGNOSTICS";
    private static readonly object FileGate = new();
    private static readonly string FileName = $"msbuild-{Environment.ProcessId}-{Guid.NewGuid():N}.jsonl";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly Action<string> _write;
    private readonly string _evaluationId = Guid.NewGuid().ToString("N");
    private readonly string _project;
    private readonly string _targetFramework;
    private readonly string _origin;
    private long _sequence;
    private long _span;
    private bool _failed;
    private bool _ended;

    internal MsBuildDiagnosticSession(string project, string targetFramework, string origin, Action<string> write)
    {
        _project = project;
        _targetFramework = targetFramework;
        _origin = origin;
        _write = write;
        Write("evaluation.start", new
        {
            processId = Environment.ProcessId,
            coreModuleId = typeof(MsBuildDiagnosticSession).Module.ModuleVersionId,
            coreBuild = typeof(MsBuildDiagnosticSession).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        });
    }

    internal static MsBuildDiagnosticSession? Start(string? workspaceRoot, string project,
        string targetFramework, string origin)
    {
        // Read only Phoenix diagnostic enablement, never any MSBuild property/environment value.
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Path.IsPathFullyQualified(workspaceRoot) ||
            Environment.GetEnvironmentVariable(EnvironmentVariable) != "1") return null;
        try
        {
            string path = Path.Combine(workspaceRoot, ".codenav", "telemetry", FileName);
            return new(project, targetFramework, origin, line =>
            {
                lock (FileGate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.AppendAllText(path, line + "\n");
                }
            });
        }
        catch { return null; } // Instrumentation must not change evaluation results.
    }

    internal long NextSpan() => ++_span;

    internal void Write(string kind, object data)
    {
        if (_failed) return;
        try
        {
            _write(JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.UtcNow,
                evaluationId = _evaluationId,
                sequence = ++_sequence,
                project = _project,
                targetFramework = _targetFramework,
                origin = _origin,
                kind,
                data,
            }, JsonOptions));
        }
        catch { _failed = true; } // Disk/listener failure is diagnostic loss, not a parser failure.
    }

    internal void End(string? error, string? partialReason)
    {
        _ended = true;
        Write("evaluation.end", new { outcome = error is null ? "succeeded" : "failed", error, partialReason });
    }

    public void Dispose()
    {
        if (!_ended) Write("evaluation.end", new { outcome = "aborted" });
    }
}
