using System.Diagnostics;
using System.Text.Json;
using CodeNav.Core.Diagnostics;

namespace CodeNav.Tests;

internal static class FSharpSemanticTelemetryAssert
{
    internal static (JsonElement Response, JsonElement Record) Observe(
        TelemetryLog log, Func<string> invoke, string tool, string outcome,
        Action<JsonElement>? assertResponse = null)
    {
        string before = Guid.NewGuid().ToString("N"), after = Guid.NewGuid().ToString("N");
        Assert.StartsWith($"phoenix-{Environment.ProcessId}-", Path.GetFileName(log.FilePath));
        log.Emit(new { e = "fsharpTestFence", corr = before });
        using JsonDocument response = JsonDocument.Parse(invoke());
        log.Emit(new { e = "fsharpTestFence", corr = after });
        JsonElement record = Between(log.FilePath, before, after, tool);
        assertResponse?.Invoke(response.RootElement);
        Match(response.RootElement, record, tool, outcome);
        return (response.RootElement.Clone(), record);
    }

    internal static JsonElement[] Read(string path)
    {
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        string contents = reader.ReadToEnd();
        // A concurrent writer may have produced only a prefix of the final JSON line.
        int end = contents.LastIndexOf('\n');
        if (end < 0) return [];
        return contents[..end].Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => { using var json = JsonDocument.Parse(line); return json.RootElement.Clone(); })
            .ToArray();
    }

    internal static JsonElement[] WaitFor(string path, Func<JsonElement[], bool> observed)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            JsonElement[] records = Read(path);
            Assert.DoesNotContain(records, row => Event(row) is "telemetry_dropped" or "telemetry_truncated");
            if (observed(records)) return records;
            Thread.Sleep(10);
        }
        throw new Xunit.Sdk.XunitException($"Telemetry fence was not observed in {path}");
    }

    internal static string? Correlation(JsonElement row) =>
        row.TryGetProperty("corr", out JsonElement value) ? value.GetString() : null;

    internal static string? Event(JsonElement row) =>
        row.TryGetProperty("e", out JsonElement value) ? value.GetString() : null;

    internal static JsonElement Between(string path, string before, string after, string tool)
    {
        // A later marker is the FIFO observation barrier, not a sleep or a sampled count.
        JsonElement[] records = WaitFor(path, rows => rows.Any(row => Correlation(row) == after));
        int start = Array.FindIndex(records, row => Correlation(row) == before);
        int end = Array.FindIndex(records, row => Correlation(row) == after);
        Assert.True(start >= 0 && end > start, "Both unique markers must be present in order.");
        Assert.Single(records, row => Correlation(row) == before);
        Assert.Single(records, row => Correlation(row) == after);
        return Assert.Single(records[(start + 1)..end], row => Event(row) == "semanticOp" &&
            row.GetProperty("tool").GetString() == tool);
    }

    internal static void Match(JsonElement response, JsonElement record, string tool, string outcome)
    {
        Assert.Equal(tool, record.GetProperty("tool").GetString());
        Assert.Equal(outcome, record.GetProperty("result").GetString());
        Assert.False(string.IsNullOrEmpty(Correlation(record)));
        Assert.True(DateTimeOffset.TryParse(record.GetProperty("ts").GetString(), out _));
        Assert.Contains(record.GetProperty("accessMode").GetString(), new[] { "writer", "follower", "unattached" });
        string[] recordFields = ["e", "ts", "corr", "tool", "accessMode", "result", "reason", "semanticColdStart"];
        Assert.All(record.EnumerateObject(), property => Assert.Contains(property.Name, recordFields));
        JsonElement timing = response.GetProperty("timing").GetProperty("semanticColdStart");
        Assert.True(JsonElement.DeepEquals(timing, record.GetProperty("semanticColdStart")), record.ToString());
        Assert.Equal("fcs", timing.GetProperty("engine").GetString());
        string[] phases = ["admissionWaitMs", "snapshotCaptureMs", "fcsSetupMs", "projectParseAndCheckMs", "fileParseAndCheckMs"];
        Assert.All(timing.EnumerateObject(), property =>
        {
            if (property.Name == "engine") return;
            Assert.Contains(property.Name, phases);
            Assert.True(property.Value.GetInt64() >= 0, property.ToString());
        });
        Assert.True(timing.GetProperty("admissionWaitMs").GetInt64() >= 0);
        if (outcome is "exact" or "partial" or "degraded")
        {
            // Successful operations must prove their adapter path was instrumented,
            // not merely validate whichever fields survived a regression.
            string[] required = tool switch
            {
                "callees" => phases[..^1],
                "symbol_at" or "definition" or "references" or "implementations" or "callers" => phases,
                _ => throw new Xunit.Sdk.XunitException($"Unexpected F# tool: {tool}"),
            };
            Assert.Equal(required.Order().ToArray(), timing.EnumerateObject()
                .Where(property => property.Name != "engine").Select(property => property.Name)
                .Order().ToArray());
        }
        // The record must carry codes and scalars, never source, paths or symbols.
        Assert.DoesNotContain("PrivateTelemetry", record.ToString());
    }
}
