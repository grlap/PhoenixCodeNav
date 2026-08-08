using CodeNav.Core.Indexing;

namespace CodeNav.Mcp;

internal enum PhoenixProcessMode
{
    Standalone,
    Daemon,
    Proxy,
    UnavailableShim,
}

internal static class PhoenixRuntimeMode
{
    private static int _mode = (int)PhoenixProcessMode.Standalone;

    internal static PhoenixProcessMode Current =>
        (PhoenixProcessMode)Volatile.Read(ref _mode);

    internal static void Set(PhoenixProcessMode mode) =>
        Volatile.Write(ref _mode, (int)mode);

    internal static string IndexMode(string accessMode)
    {
        if (string.Equals(accessMode, IndexManager.UnavailableAccessMode, StringComparison.Ordinal))
            return "unavailable";
        return Current == PhoenixProcessMode.Daemon ? "daemon" : "standalone";
    }
}
