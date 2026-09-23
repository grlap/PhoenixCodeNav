namespace CodeNav.Core.Diagnostics;

/// <summary>Failure reporting must not replace the failure or interrupt its cleanup.</summary>
internal static class SafeDiagnosticLog
{
    internal static void Write(Action<string> log, string message)
    {
        try { log(message); }
        catch { /* A diagnostic sink is not an operational dependency. */ }
    }
}
