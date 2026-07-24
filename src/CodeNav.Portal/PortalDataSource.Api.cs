using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace CodeNav.Portal;

internal sealed record PortalOperationQuery(
    string? WorkspaceId,
    string? InstanceId,
    string? Category,
    string? Tool,
    string? Outcome,
    string? Confidence,
    string? ColdState,
    long? MinDurationMs,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    string? Cursor,
    int Limit)
{
    internal static PortalOperationQuery Default { get; } = new(
        null, null, null, null, null, null, null, null, null, null, null, 100);

    internal static bool TryParse(
        IQueryCollection values,
        out PortalOperationQuery query,
        out string? error)
    {
        query = Default;
        error = null;
        if (!PortalQueryParsing.TryString(values, "workspaceId", out string? workspaceId, out error)
            || !PortalQueryParsing.TryString(values, "instanceId", out string? instanceId, out error)
            || !PortalQueryParsing.TryString(values, "category", out string? category, out error)
            || !PortalQueryParsing.TryString(values, "tool", out string? tool, out error)
            || !PortalQueryParsing.TryString(values, "outcome", out string? outcome, out error)
            || !PortalQueryParsing.TryString(values, "confidence", out string? confidence, out error)
            || !PortalQueryParsing.TryString(values, "coldState", out string? coldState, out error)
            || !PortalQueryParsing.TryNonNegativeLong(
                values,
                "minDurationMs",
                out long? minDurationMs,
                out error)
            || !PortalQueryParsing.TryTimestamp(values, "fromUtc", out DateTimeOffset? fromUtc, out error)
            || !PortalQueryParsing.TryTimestamp(values, "toUtc", out DateTimeOffset? toUtc, out error)
            || !PortalQueryParsing.TryCursor(values, "cursor", "o", out string? cursor, out error)
            || !PortalQueryParsing.TryLimit(values, 100, 500, out int limit, out error))
        {
            return false;
        }

        if (fromUtc > toUtc)
        {
            error = "fromUtc must not be later than toUtc.";
            return false;
        }

        query = new PortalOperationQuery(
            workspaceId,
            instanceId,
            category,
            tool,
            outcome,
            confidence,
            coldState,
            minDurationMs,
            fromUtc,
            toUtc,
            cursor,
            limit);
        return true;
    }
}

internal sealed record PortalEventQuery(
    string? WorkspaceId,
    string? InstanceId,
    string? IndexId,
    string? Severity,
    string? Component,
    string? Code,
    string? CorrelationId,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    string? Cursor,
    int Limit)
{
    internal static PortalEventQuery Default { get; } = new(
        null, null, null, null, null, null, null, null, null, null, 100);

    internal static bool TryParse(
        IQueryCollection values,
        out PortalEventQuery query,
        out string? error)
    {
        query = Default;
        error = null;
        if (!PortalQueryParsing.TryString(values, "workspaceId", out string? workspaceId, out error)
            || !PortalQueryParsing.TryString(values, "instanceId", out string? instanceId, out error)
            || !PortalQueryParsing.TryString(values, "indexId", out string? indexId, out error)
            || !PortalQueryParsing.TryString(values, "severity", out string? severity, out error)
            || !PortalQueryParsing.TryString(values, "component", out string? component, out error)
            || !PortalQueryParsing.TryString(values, "code", out string? code, out error)
            || !PortalQueryParsing.TryString(
                values,
                "correlationId",
                out string? correlationId,
                out error)
            || !PortalQueryParsing.TryTimestamp(values, "fromUtc", out DateTimeOffset? fromUtc, out error)
            || !PortalQueryParsing.TryTimestamp(values, "toUtc", out DateTimeOffset? toUtc, out error)
            || !PortalQueryParsing.TryCursor(values, "cursor", "e", out string? cursor, out error)
            || !PortalQueryParsing.TryLimit(values, 100, 500, out int limit, out error))
        {
            return false;
        }

        if (fromUtc > toUtc)
        {
            error = "fromUtc must not be later than toUtc.";
            return false;
        }

        query = new PortalEventQuery(
            workspaceId,
            instanceId,
            indexId,
            severity,
            component,
            code,
            correlationId,
            fromUtc,
            toUtc,
            cursor,
            limit);
        return true;
    }
}

internal static class PortalQueryParsing
{
    private const int MaxQueryStringBytes = 256;

    internal static bool TryString(
        IQueryCollection values,
        string name,
        out string? value,
        out string? error)
    {
        value = null;
        error = null;
        if (!values.TryGetValue(name, out StringValues raw) || StringValues.IsNullOrEmpty(raw))
            return true;
        if (raw.Count != 1)
        {
            error = $"{name} must be supplied at most once.";
            return false;
        }

        string text = raw[0]?.Trim() ?? string.Empty;
        if (text.Length == 0)
            return true;
        if (Encoding.UTF8.GetByteCount(text) > MaxQueryStringBytes)
        {
            error = $"{name} exceeds the 256-byte UTF-8 limit.";
            return false;
        }
        value = text;
        return true;
    }

    internal static bool TryTimestamp(
        IQueryCollection values,
        string name,
        out DateTimeOffset? value,
        out string? error)
    {
        value = null;
        if (!TryString(values, name, out string? raw, out error) || raw is null)
            return error is null;
        if (!DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed))
        {
            error = $"{name} must be an ISO-8601 UTC timestamp.";
            return false;
        }
        value = parsed;
        return true;
    }

    internal static bool TryNonNegativeLong(
        IQueryCollection values,
        string name,
        out long? value,
        out string? error)
    {
        value = null;
        if (!TryString(values, name, out string? raw, out error) || raw is null)
            return error is null;
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)
            || parsed < 0)
        {
            error = $"{name} must be a non-negative integer.";
            return false;
        }
        value = parsed;
        return true;
    }

    internal static bool TryLimit(
        IQueryCollection values,
        int defaultValue,
        int maximum,
        out int value,
        out string? error)
    {
        value = defaultValue;
        if (!TryString(values, "limit", out string? raw, out error) || raw is null)
            return error is null;
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            || parsed is < 1
            || parsed > maximum)
        {
            error = $"limit must be between 1 and {maximum}.";
            return false;
        }
        value = parsed;
        return true;
    }

    internal static bool TryCursor(
        IQueryCollection values,
        string name,
        string kind,
        out string? value,
        out string? error)
    {
        if (!TryString(values, name, out value, out error) || value is null)
            return error is null;
        if (!PortalDataSource.TryDecodeCursor(value, kind, out _))
        {
            error = "cursor is invalid for this endpoint.";
            return false;
        }
        return true;
    }
}

public sealed partial class PortalDataSource
{
    private const int MaxPageResponseBytes = 512 * 1024;

    private object BuildOperationPage(
        PortalOperation[] source,
        bool dataComplete,
        bool totalIsLowerBound,
        PortalOperationQuery query,
        string cursorGeneration)
    {
        string filterFingerprint = OperationFilterFingerprint(query);
        PortalOperation[] filtered = source
            .Where(item => Matches(item, query))
            .OrderByDescending(item => item.StartedAtUtc)
            .ThenBy(item => item.OperationId, StringComparer.Ordinal)
            .ToArray();
        IEnumerable<PortalOperation> afterCursor = filtered;
        if (query.Cursor is not null)
        {
            CursorPosition cursor = ValidateCursor(
                query.Cursor,
                "o",
                cursorGeneration,
                filterFingerprint);
            afterCursor = filtered.Where(item =>
                item.StartedAtUtc.UtcTicks < cursor.UtcTicks
                || (item.StartedAtUtc.UtcTicks == cursor.UtcTicks
                    && string.CompareOrdinal(item.OperationId, cursor.Id) > 0));
        }

        PortalOperation[] candidates = afterCursor.Take(query.Limit + 1).ToArray();
        var items = new List<PortalOperation>(Math.Min(query.Limit, candidates.Length));
        int estimatedBytes = 256;
        bool byteBudgetHit = false;
        foreach (PortalOperation candidate in candidates.Take(query.Limit))
        {
            int itemBytes = EstimateBytes(candidate);
            if (items.Count > 0 && estimatedBytes + itemBytes > MaxPageResponseBytes)
            {
                byteBudgetHit = true;
                break;
            }
            items.Add(candidate);
            estimatedBytes += itemBytes;
        }

        bool hasMore = candidates.Length > items.Count || byteBudgetHit;
        string? nextCursor = hasMore && items.Count > 0
            ? EncodeCursor(
                "o",
                cursorGeneration,
                filterFingerprint,
                items[^1].StartedAtUtc,
                items[^1].OperationId)
            : null;
        return new
        {
            items = items.ToArray(),
            nextCursor,
            returned = items.Count,
            total = filtered.Length,
            totalIsLowerBound,
            truncated = hasMore || !dataComplete,
            byteBudgetHit,
            responseByteLimit = MaxPageResponseBytes,
            dataComplete
        };
    }

    private object BuildEventPage(
        PortalEvent[] source,
        bool dataComplete,
        bool totalIsLowerBound,
        PortalEventQuery query,
        string cursorGeneration)
    {
        string filterFingerprint = EventFilterFingerprint(query);
        PortalEvent[] filtered = source
            .Where(item => Matches(item, query))
            .OrderByDescending(item => item.TimestampUtc)
            .ThenBy(item => item.EventId, StringComparer.Ordinal)
            .ToArray();
        IEnumerable<PortalEvent> afterCursor = filtered;
        if (query.Cursor is not null)
        {
            CursorPosition cursor = ValidateCursor(
                query.Cursor,
                "e",
                cursorGeneration,
                filterFingerprint);
            afterCursor = filtered.Where(item =>
                item.TimestampUtc.UtcTicks < cursor.UtcTicks
                || (item.TimestampUtc.UtcTicks == cursor.UtcTicks
                    && string.CompareOrdinal(item.EventId, cursor.Id) > 0));
        }

        PortalEvent[] candidates = afterCursor.Take(query.Limit + 1).ToArray();
        var items = new List<PortalEvent>(Math.Min(query.Limit, candidates.Length));
        int estimatedBytes = 256;
        bool byteBudgetHit = false;
        foreach (PortalEvent candidate in candidates.Take(query.Limit))
        {
            int itemBytes = EstimateBytes(candidate);
            if (items.Count > 0 && estimatedBytes + itemBytes > MaxPageResponseBytes)
            {
                byteBudgetHit = true;
                break;
            }
            items.Add(candidate);
            estimatedBytes += itemBytes;
        }

        bool hasMore = candidates.Length > items.Count || byteBudgetHit;
        string? nextCursor = hasMore && items.Count > 0
            ? EncodeCursor(
                "e",
                cursorGeneration,
                filterFingerprint,
                items[^1].TimestampUtc,
                items[^1].EventId)
            : null;
        return new
        {
            items = items.ToArray(),
            nextCursor,
            returned = items.Count,
            total = filtered.Length,
            totalIsLowerBound,
            truncated = hasMore || !dataComplete,
            byteBudgetHit,
            responseByteLimit = MaxPageResponseBytes,
            dataComplete
        };
    }

    private static bool Matches(PortalOperation item, PortalOperationQuery query) =>
        Match(item.WorkspaceId, query.WorkspaceId)
        && Match(item.InstanceId, query.InstanceId)
        && Match(item.Category, query.Category)
        && Match(item.Tool, query.Tool)
        && Match(item.Outcome, query.Outcome)
        && Match(item.Confidence, query.Confidence)
        && Match(item.ColdState, query.ColdState)
        && (query.MinDurationMs is null
            || item.DurationMs is not null
            && item.DurationMs >= query.MinDurationMs)
        && (query.FromUtc is null || item.StartedAtUtc >= query.FromUtc)
        && (query.ToUtc is null || item.StartedAtUtc <= query.ToUtc);

    private static bool Matches(PortalEvent item, PortalEventQuery query) =>
        Match(item.WorkspaceId, query.WorkspaceId)
        && Match(item.InstanceId, query.InstanceId)
        && Match(item.IndexId, query.IndexId)
        && Match(item.Severity, query.Severity)
        && Match(item.Component, query.Component)
        && Match(item.Code, query.Code)
        && Match(item.CorrelationId, query.CorrelationId)
        && (query.FromUtc is null || item.TimestampUtc >= query.FromUtc)
        && (query.ToUtc is null || item.TimestampUtc <= query.ToUtc);

    private static bool Match(string? actual, string? expected) =>
        expected is null || string.Equals(actual, expected, StringComparison.Ordinal);

    private string EncodeCursor(
        string kind,
        string generation,
        string filterFingerprint,
        DateTimeOffset timestamp,
        string id)
    {
        string text = string.Create(
            CultureInfo.InvariantCulture,
            $"{kind}|{_sessionId}|{generation}|{filterFingerprint}|{timestamp.UtcTicks}|{id}");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    internal static bool TryDecodeCursor(
        string value,
        string kind,
        out CursorPosition cursor)
    {
        cursor = default;
        if (value.Length is 0 or > 512)
            return false;
        try
        {
            string padded = value.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            string[] parts = decoded.Split('|', 6);
            if (parts.Length != 6
                || !string.Equals(parts[0], kind, StringComparison.Ordinal)
                || !long.TryParse(
                    parts[4],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out long ticks)
                || ticks < DateTimeOffset.MinValue.UtcTicks
                || ticks > DateTimeOffset.MaxValue.UtcTicks
                || parts[1].Length != 32
                || parts[2].Length != 12
                || parts[3].Length != 12
                || Encoding.UTF8.GetByteCount(parts[5]) > 256)
            {
                return false;
            }
            cursor = new CursorPosition(
                parts[1],
                parts[2],
                parts[3],
                ticks,
                parts[5]);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private CursorPosition ValidateCursor(
        string value,
        string kind,
        string generation,
        string filterFingerprint)
    {
        if (!TryDecodeCursor(value, kind, out CursorPosition cursor)
            || !string.Equals(cursor.SessionId, _sessionId, StringComparison.Ordinal)
            || !string.Equals(cursor.Generation, generation, StringComparison.Ordinal)
            || !string.Equals(
                cursor.FilterFingerprint,
                filterFingerprint,
                StringComparison.Ordinal))
        {
            throw new PortalCursorExpiredException();
        }
        return cursor;
    }

    private static string OperationFilterFingerprint(PortalOperationQuery query) =>
        CursorFilterFingerprint(
            query.WorkspaceId,
            query.InstanceId,
            query.Category,
            query.Tool,
            query.Outcome,
            query.Confidence,
            query.ColdState,
            query.MinDurationMs?.ToString(CultureInfo.InvariantCulture),
            query.FromUtc?.UtcTicks.ToString(CultureInfo.InvariantCulture),
            query.ToUtc?.UtcTicks.ToString(CultureInfo.InvariantCulture));

    private static string EventFilterFingerprint(PortalEventQuery query) =>
        CursorFilterFingerprint(
            query.WorkspaceId,
            query.InstanceId,
            query.IndexId,
            query.Severity,
            query.Component,
            query.Code,
            query.CorrelationId,
            query.FromUtc?.UtcTicks.ToString(CultureInfo.InvariantCulture),
            query.ToUtc?.UtcTicks.ToString(CultureInfo.InvariantCulture));

    private static string CursorFilterFingerprint(params string?[] values)
    {
        var canonical = new StringBuilder();
        foreach (string? value in values)
        {
            string text = value ?? string.Empty;
            canonical.Append(text.Length.ToString(CultureInfo.InvariantCulture));
            canonical.Append(':');
            canonical.Append(text);
            canonical.Append(';');
        }
        return StableId(canonical.ToString());
    }

    internal readonly record struct CursorPosition(
        string SessionId,
        string Generation,
        string FilterFingerprint,
        long UtcTicks,
        string Id);
}

internal sealed class PortalCursorExpiredException : Exception;
