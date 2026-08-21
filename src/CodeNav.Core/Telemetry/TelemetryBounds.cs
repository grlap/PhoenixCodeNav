using System.Text;
using System.Text.Json;

namespace CodeNav.Core.Telemetry;

/// <summary>
/// Owns: the single privacy/bounds chokepoint of telemetry API v1 (x5ls.1) that every frame
/// passes at MATERIALIZATION — immediately before the socket write (identity fields are
/// bound at send, so this is the earliest point the final payload exists). Two layers:
/// (1) bounds — serialized data must fit the v1 frame ceiling (256 KiB minus envelope room);
/// (2) privacy tripwire — the serialized JSON must not look like it carries a local path
///     (drive-rooted `C:\`, UNC `\\server`, or the process's own canonical workspace root).
/// The tripwire is defense in depth: fields are designed redacted at their source; this gate
/// exists so a future field addition that forgets the rules is REJECTED loudly (diagnostic
/// counter + server log) instead of leaking quietly.
/// Does not own: per-field schemas (producers shape approved fields only) or the queue.
/// </summary>
internal static class TelemetryBounds
{
    /// <summary>V1 frame ceiling is 262,144 bytes; the envelope (protocol/version/type/ids/
    /// sequence/timestamp) needs at most ~256 bytes, rounded up generously.</summary>
    public const int MaxFrameBytes = 262_144;
    public const int MaxDataBytes = MaxFrameBytes - 1024;
    public const int MaxStringBytes = 256;

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes and validates one frame's data payload. Returns null (with a reason
    /// in <paramref name="rejectReason"/>) when the payload must not leave the process.</summary>
    public static string? SerializeData(object data, string canonicalWorkspaceRoot,
        out string? rejectReason)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(data, JsonOpts);
        }
        catch (Exception ex)
        {
            rejectReason = $"serialize_failed:{ex.GetType().Name}";
            return null;
        }
        if (Encoding.UTF8.GetByteCount(json) > MaxDataBytes)
        {
            rejectReason = "frame_too_large";
            return null;
        }
        // Privacy tripwire. In JSON string values a backslash is escaped, so a drive-rooted
        // path appears as `:\\` and a UNC root as `\\\\`; the workspace root must be checked
        // in its ESCAPED spelling (the raw one cannot occur inside a JSON string value) and
        // in its forward-slash spelling. Review F9: git-normalized absolute paths use FORWARD
        // slashes (`C:/repo/...`), which needs no JSON escaping and dodges `:\\` — `:/` covers
        // it. v1 fields carry no URLs (`https://` would trip this deliberately: a tripwire
        // false-positive is visible in validationRejected; a leak is not).
        // `//` covers UNC-forward spellings (`//server/share`, review r2: `:/` alone missed
        // them) — v1 fields carry no URLs and no legitimate double slash, same strict posture.
        if (json.Contains(":\\\\", StringComparison.Ordinal) ||
            json.Contains("\\\\\\\\", StringComparison.Ordinal) ||
            json.Contains(":/", StringComparison.Ordinal) ||
            json.Contains("//", StringComparison.Ordinal) ||
            json.Contains(canonicalWorkspaceRoot.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase) ||
            json.Contains(canonicalWorkspaceRoot.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            rejectReason = "privacy_tripwire";
            return null;
        }
        rejectReason = null;
        return json;
    }

    /// <summary>Bounded label helper: UTF-8-truncates to the v1 string ceiling so no caller
    /// can smuggle an unbounded string into an approved field.</summary>
    public static string BoundedLabel(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) <= MaxStringBytes) return value;
        var bytes = Encoding.UTF8.GetBytes(value);
        int len = MaxStringBytes;
        while (len > 0 && (bytes[len] & 0xC0) == 0x80) len--; // don't split a code point
        return Encoding.UTF8.GetString(bytes, 0, len);
    }
}
