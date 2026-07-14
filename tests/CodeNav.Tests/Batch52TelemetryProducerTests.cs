using System.Text.Json;
using CodeNav.Core.Telemetry;

namespace CodeNav.Tests;

/// <summary>
/// Batch 52 (x5ls.1 Batch A): the telemetry API v1 producer core against the contract-test
/// broker. Pins the wire contracts the Operations Portal (Project B) builds on:
/// (1) hello/welcome negotiation with salted HMAC identities (wa_/ix_, portal-groupable);
/// (2) envelope shape + strictly increasing sequence on the wire;
/// (3) reconnect → fresh snapshot + honest telemetry.dropped after buffer pressure;
/// (4) the privacy tripwire — a path-carrying frame never reaches the pipe;
/// (5) resync → snapshot; reject → stand down; unknown control frames ignored;
/// (6) disabled/portal-absent producers never touch a request path.
/// </summary>
public class Batch52TelemetryProducerTests
{
    private static TelemetryProducer NewProducer(TestTelemetryBroker broker, string root,
        Func<TelemetryIds, object>? snapshot = null)
        => new(root, Path.Combine(root, ".codenav", "index.db"),
            snapshot ?? (ids => new
            {
                workspace = new { id = ids.WorkspaceId, label = "fixture" },
                index = new { id = ids.IndexId, accessMode = "writer" },
            }),
            pipeName: broker.PipeName, enabled: true);

    [Fact]
    public void NegotiatesAndPublishesSaltedSnapshotWithOrderedEnvelope()
    {
        string root = Directory.CreateTempSubdirectory("codenav-52-nego").FullName;
        using var broker = new TestTelemetryBroker();
        using (var producer = NewProducer(broker, root))
        {
            Assert.True(WaitUntil(() => broker.FramesOfType("instance.snapshot").Count > 0, 15_000),
                "no instance.snapshot arrived after negotiation");

            // hello carried the pre-negotiation identity fields and nothing sensitive.
            Assert.True(broker.HelloLines.TryPeek(out string? hello));
            using var helloDoc = JsonDocument.Parse(hello!);
            Assert.Equal("phoenix.telemetry", helloDoc.RootElement.GetProperty("protocol").GetString());
            Assert.Equal(1, helloDoc.RootElement.GetProperty("supportedVersions")[0].GetInt32());
            Assert.Equal(Environment.ProcessId, helloDoc.RootElement.GetProperty("processId").GetInt32());
            Assert.DoesNotContain(":\\\\", hello);

            var snap = broker.FramesOfType("instance.snapshot")[0].RootElement;
            Assert.Equal(1, snap.GetProperty("version").GetInt32());
            Assert.Equal(producer.InstanceId, snap.GetProperty("instanceId").GetString());
            var data = snap.GetProperty("data");

            // Identity contract: base64url HMACs the portal can recompute for grouping —
            // the test derives them independently from the broker's salt. CanonicalPath is
            // the shared folding rule (case-folds on Windows only — review F17).
            string canonicalRoot = TelemetryIdentity.CanonicalPath(root);
            Assert.Equal(
                "wa_" + Base64UrlHmac(broker.Salt, "workspace\0" + canonicalRoot),
                data.GetProperty("workspace").GetProperty("id").GetString());
            Assert.StartsWith("ix_",
                data.GetProperty("index").GetProperty("id").GetString());

            // Wire order: sequences strictly increase in arrival order.
            var sequences = broker.Frames.Select(f =>
                f.RootElement.GetProperty("sequence").GetInt64()).ToList();
            Assert.True(sequences.Count > 0);
            Assert.True(sequences.SequenceEqual(sequences.OrderBy(s => s)),
                "wire sequences must be non-decreasing in arrival order");
            Assert.Equal(sequences.Count, sequences.Distinct().Count());
        }
        TestWorkspaceCleanup.DeleteWorkspace(root);
    }

    [Fact]
    public void ReconnectAfterBufferPressureDisclosesDropsAndResendsSnapshot()
    {
        string root = Directory.CreateTempSubdirectory("codenav-52-drop").FullName;
        using var broker = new TestTelemetryBroker();
        using (var producer = NewProducer(broker, root))
        {
            Assert.True(WaitUntil(() => broker.FramesOfType("instance.snapshot").Count > 0, 15_000));
            broker.DropConnection();

            // Flood well past the queue capacity while the portal is gone.
            for (int i = 0; i < 2400; i++)
            {
                int n = i;
                producer.Emit("diagnostic.event", _ => new { code = "test.flood", n });
            }
            Assert.True(producer.DroppedRecords > 0, "flood past capacity must evict");

            Assert.True(WaitUntil(() => broker.Connections >= 2
                && broker.FramesOfType("instance.snapshot").Count >= 2, 30_000),
                "producer must reconnect and resend a fresh snapshot");
            Assert.True(WaitUntil(() => broker.FramesOfType("telemetry.dropped").Count > 0, 15_000),
                "drops must be disclosed after reconnect");
            var drop = broker.FramesOfType("telemetry.dropped")[0].RootElement.GetProperty("data");
            Assert.True(drop.GetProperty("records").GetInt64() > 0);
            Assert.Equal("producer_buffer_full", drop.GetProperty("reason").GetString());
        }
        TestWorkspaceCleanup.DeleteWorkspace(root);
    }

    [Fact]
    public void PrivacyTripwireBlocksPathCarryingFramesButNotCleanOnes()
    {
        string root = Directory.CreateTempSubdirectory("codenav-52-privacy").FullName;
        using var broker = new TestTelemetryBroker();
        using (var producer = NewProducer(broker, root))
        {
            Assert.True(WaitUntil(() => broker.FramesOfType("instance.snapshot").Count > 0, 15_000));

            // A future instrumentation bug: a raw path (drive-rooted AND the workspace root
            // itself) sneaks into an approved-looking field. The gate must eat both.
            producer.Emit("diagnostic.event", _ => new { code = "bad", detail = @"C:\secret\place" });
            producer.Emit("diagnostic.event", _ => new { code = "bad2", detail = root });
            producer.Emit("diagnostic.event", _ => new { code = "good", count = 3 });

            Assert.True(WaitUntil(() => broker.FramesOfType("diagnostic.event")
                .Any(f => f.RootElement.GetProperty("data").GetProperty("code").GetString() == "good"),
                15_000), "the clean frame must still arrive");
            foreach (var f in broker.FramesOfType("diagnostic.event"))
            {
                string code = f.RootElement.GetProperty("data").GetProperty("code").GetString()!;
                Assert.Equal("good", code); // the two path-carrying frames never hit the wire
            }

            // Review F4: the rejected frames consumed sequences — the gap they leave must be
            // disclosed in-band, not read as silent loss with all drop gauges at zero.
            Assert.True(WaitUntil(() => broker.FramesOfType("telemetry.dropped").Any(f =>
                f.RootElement.GetProperty("data").GetProperty("reason").GetString()
                    == "producer_validation_rejected"), 15_000),
                "send-time rejects must be disclosed as telemetry.dropped");
            Assert.True(producer.ValidationRejected >= 2);
        }
        TestWorkspaceCleanup.DeleteWorkspace(root);
    }

    [Fact]
    public void ResyncTriggersFreshSnapshotAndUnknownControlIsIgnored()
    {
        string root = Directory.CreateTempSubdirectory("codenav-52-resync").FullName;
        using var broker = new TestTelemetryBroker();
        using (var producer = NewProducer(broker, root))
        {
            Assert.True(WaitUntil(() => broker.FramesOfType("instance.snapshot").Count > 0, 15_000));
            broker.SendControl(new { protocol = "phoenix.telemetry", type = "future_thing" });
            broker.SendControl(new { protocol = "phoenix.telemetry", version = 1, type = "resync", reason = "sequence_gap" });
            Assert.True(WaitUntil(() => broker.FramesOfType("instance.snapshot").Count >= 2, 15_000),
                "resync must produce a fresh snapshot (and the unknown frame must not kill the session)");
        }
        TestWorkspaceCleanup.DeleteWorkspace(root);
    }

    [Fact]
    public void RejectStandsDownWithoutRetryStorm()
    {
        string root = Directory.CreateTempSubdirectory("codenav-52-reject").FullName;
        using var broker = new TestTelemetryBroker(rejectMode: true);
        using (var producer = NewProducer(broker, root))
        {
            Assert.True(WaitUntil(() => broker.Connections >= 1, 15_000));
            int seen = broker.Connections;
            Thread.Sleep(3000); // the reject backoff is minutes; no second attempt this soon
            Assert.Equal(seen, broker.Connections);
            producer.Emit("diagnostic.event", _ => new { code = "still.safe" }); // never throws
        }
        TestWorkspaceCleanup.DeleteWorkspace(root);
    }

    [Fact]
    public void DisabledProducerNeverConnectsAndEmitIsFreeToCall()
    {
        string root = Directory.CreateTempSubdirectory("codenav-52-disabled").FullName;
        using var broker = new TestTelemetryBroker();
        using (var producer = new TelemetryProducer(root, Path.Combine(root, "index.db"),
            ids => new { }, pipeName: broker.PipeName, enabled: false))
        {
            producer.Emit("diagnostic.event", _ => new { code = "ignored" });
            Thread.Sleep(500);
            Assert.Equal(0, broker.Connections);
            Assert.Equal(0, producer.QueuedRecords);
        }
        TestWorkspaceCleanup.DeleteWorkspace(root);
    }

    [Fact]
    public async Task QueueEvictsOldestNonLifecycleFirstAndCountsIt()
    {
        var queue = new TelemetryQueue(capacity: 4);
        void Add(string type, bool lifecycle)
            => queue.Enqueue(type, _ => new { }, "2026-01-01T00:00:00.000Z", lifecycle);

        Add("instance.snapshot", lifecycle: true);
        Add("a", false);
        Add("b", false);
        Add("c", false);
        Add("d", false); // full: evicts "a" (oldest NON-lifecycle), never the snapshot

        var drained = await queue.DequeueBatchAsync(10, CancellationToken.None);
        Assert.Equal(new[] { "instance.snapshot", "b", "c", "d" },
            drained.Select(f => f.Type).ToArray());
        var report = queue.TakeDropReport();
        Assert.NotNull(report);
        Assert.Equal(1, report!.Value.Records);
        Assert.Equal(2, report.Value.SinceSequence); // "a" held sequence 2
        Assert.Null(queue.TakeDropReport()); // report resets after disclosure
    }

    [Fact]
    public async Task ConcurrentEmittersKeepWireOrderStrictlyIncreasing()
    {
        // Review F3: sequence assignment and enqueue must be ATOMIC — assigning the sequence
        // before taking the queue gate let a preempted emitter enqueue its lower sequence
        // AFTER a rival's higher one, inverting FIFO wire order and tripping portal
        // replay/gap detection. Hammer from four threads and assert drain order == sequence
        // order with no duplicates.
        var queue = new TelemetryQueue(capacity: 5000);
        var threads = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                queue.Enqueue("hammer", _ => new { }, "2026-01-01T00:00:00.000Z", false);
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        var sequences = new List<long>();
        while (queue.Count > 0)
        {
            var batch = await queue.DequeueBatchAsync(100, CancellationToken.None);
            sequences.AddRange(batch.Select(f => f.Sequence));
        }
        Assert.Equal(2000, sequences.Count);
        Assert.True(sequences.SequenceEqual(sequences.OrderBy(s => s)),
            "drain order must equal sequence order");
        Assert.Equal(sequences.Count, sequences.Distinct().Count());
    }

    [Fact]
    public async Task StaleEvictionPermitsNeverYieldAnEmptyBatch()
    {
        // Eviction removes a frame without consuming its semaphore permit; the sender treats
        // an EMPTY batch as cancellation and tears down the session. A stale permit must be
        // absorbed by waiting again, not surfaced as a phantom wake.
        var queue = new TelemetryQueue(capacity: 2);
        void Add(string type)
            => queue.Enqueue(type, _ => new { }, "2026-01-01T00:00:00.000Z", false);

        Add("a");
        Add("b");
        Add("c"); // evicts "a": 3 permits, 2 frames
        var first = await queue.DequeueBatchAsync(10, CancellationToken.None);
        Assert.Equal(2, first.Count); // drained everything; 2 stale permits remain

        var pending = queue.DequeueBatchAsync(10, CancellationToken.None);
        Assert.NotSame(pending, await Task.WhenAny(pending, Task.Delay(300)));
        Add("d");
        var second = await pending;
        Assert.Equal(new[] { "d" }, second.Select(f => f.Type).ToArray());
    }

    [Fact]
    public void IdentityDerivationIsSaltScopedAndPathCaseInsensitive()
    {
        byte[] saltA = new byte[32], saltB = new byte[32];
        Random.Shared.NextBytes(saltA);
        Random.Shared.NextBytes(saltB);
        string idLower = TelemetryIdentity.WorkspaceId(saltA,
            TelemetryIdentity.CanonicalPath(@"C:\Some\Workspace"));
        string idUpper = TelemetryIdentity.WorkspaceId(saltA,
            TelemetryIdentity.CanonicalPath(@"C:\SOME\WORKSPACE\"));
        Assert.Equal(idLower, idUpper); // same physical workspace groups under one id
        Assert.NotEqual(idLower, TelemetryIdentity.WorkspaceId(saltB,
            TelemetryIdentity.CanonicalPath(@"C:\Some\Workspace"))); // new portal session → new ids
        Assert.StartsWith("wa_", idLower);
        Assert.DoesNotContain("=", idLower); // base64url, unpadded
        Assert.DoesNotContain("+", idLower);
        Assert.DoesNotContain("/", idLower);
    }

    private static string Base64UrlHmac(byte[] salt, string value)
    {
        byte[] hash = System.Security.Cryptography.HMACSHA256.HashData(
            salt, System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static bool WaitUntil(Func<bool> cond, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            Thread.Sleep(50);
        }
        return cond();
    }
}
