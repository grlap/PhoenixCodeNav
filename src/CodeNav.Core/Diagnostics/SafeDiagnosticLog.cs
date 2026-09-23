namespace CodeNav.Core.Diagnostics;

/// <summary>Failure reporting must not replace the failure or interrupt its cleanup.</summary>
internal static class SafeDiagnosticLog
{
    internal static void Write(Action<string> log, string message)
    {
        try { log(message); }
        catch { /* A diagnostic sink is not an operational dependency. */ }
    }

    internal static void Write(Action<string> log, string message, Exception exception)
    {
        // Formatting belongs inside the boundary too: Exception.ToString is virtual.
        try { log($"{message}: {exception}"); }
        catch { /* Neither exception formatting nor a diagnostic sink may escape. */ }
    }
}
