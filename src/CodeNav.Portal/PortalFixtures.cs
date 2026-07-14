namespace CodeNav.Portal;

internal static class PortalFixtures
{
    private static readonly DateTimeOffset FixtureNow = new(2026, 7, 13, 23, 48, 36, TimeSpan.Zero);

    internal static object Bootstrap()
    {
        return new
        {
            apiVersion = 1,
            portal = new
            {
                sessionId = "portal-preview-session",
                version = "0.1.0-preview",
                startedAtUtc = FixtureNow.AddMinutes(-18),
                nowUtc = FixtureNow
            },
            summary = new
            {
                workspaceCount = 2,
                indexCount = 2,
                connectedInstanceCount = 3,
                staleInstanceCount = 0,
                activeOperationCount = 1,
                warningCount = 1
            },
            workspaces = new object[]
            {
                new
                {
                    workspaceId = "ws-phoenix",
                    name = "PhoenixCodeNav",
                    shortName = "PHX",
                    state = "indexing",
                    stateLabel = "Indexing",
                    indexId = "idx-phoenix",
                    instanceIds = new[] { "instance-writer", "instance-follower" },
                    description = "Main workspace · writer + follower"
                },
                new
                {
                    workspaceId = "ws-ravendb",
                    name = "RavenDB",
                    shortName = "RDB",
                    state = "ready",
                    stateLabel = "Ready",
                    indexId = "idx-ravendb",
                    instanceIds = new[] { "instance-ravendb" },
                    description = "Benchmark workspace · ready"
                }
            },
            indexes = new object[]
            {
                new
                {
                    indexId = "idx-phoenix",
                    workspaceId = "ws-phoenix",
                    state = "building",
                    stateLabel = "Building index",
                    freshness = "working_tree",
                    epoch = 1842,
                    databaseSizeBytes = 187_432_960L,
                    currentBuild = new
                    {
                        buildId = "build-1842",
                        phase = "indexing_files",
                        phaseLabel = "Indexing source",
                        startedAtUtc = FixtureNow.AddSeconds(-14.2),
                        elapsedMs = 14_200,
                        filesProcessed = 18_432,
                        filesTotal = 24_108,
                        progress = 0.7645,
                        throughputPerSecond = 1_296,
                        etaSeconds = 4.4,
                        projectsParsed = 37,
                        projectsTotal = 37,
                        filesSkipped = 4,
                        projectsFailed = 0,
                        phases = new object[]
                        {
                            new { id = "scanning", label = "Scan", state = "complete", durationMs = 1_120 },
                            new { id = "parsing_projects", label = "Projects", state = "complete", durationMs = 2_310 },
                            new { id = "indexing_files", label = "Symbols", state = "active", durationMs = 10_770 },
                            new { id = "finalizing", label = "Publish", state = "pending", durationMs = (int?)null }
                        }
                    },
                    refresh = new
                    {
                        state = "watching",
                        queueDepth = 0,
                        changesProcessed = 128,
                        lastRefreshDurationMs = 312
                    }
                },
                new
                {
                    indexId = "idx-ravendb",
                    workspaceId = "ws-ravendb",
                    state = "ready",
                    stateLabel = "Ready",
                    freshness = "head",
                    epoch = 932,
                    databaseSizeBytes = 1_476_395_008L,
                    currentBuild = (object?)null,
                    refresh = new
                    {
                        state = "watching",
                        queueDepth = 0,
                        changesProcessed = 43,
                        lastRefreshDurationMs = 188
                    }
                }
            },
            instances = new object[]
            {
                new
                {
                    instanceId = "instance-writer",
                    workspaceId = "ws-phoenix",
                    indexId = "idx-phoenix",
                    displayName = "VS Code · Codex",
                    role = "writer",
                    connectionState = "connected",
                    semanticState = "warming",
                    version = "0.11.8",
                    processId = 14832,
                    lastSeenUtc = FixtureNow.AddMilliseconds(-410)
                },
                new
                {
                    instanceId = "instance-follower",
                    workspaceId = "ws-phoenix",
                    indexId = "idx-phoenix",
                    displayName = "Claude Desktop",
                    role = "follower",
                    connectionState = "connected",
                    semanticState = "warm",
                    version = "0.11.8",
                    processId = 19304,
                    lastSeenUtc = FixtureNow.AddMilliseconds(-760)
                },
                new
                {
                    instanceId = "instance-ravendb",
                    workspaceId = "ws-ravendb",
                    indexId = "idx-ravendb",
                    displayName = "Terminal · Bench",
                    role = "writer",
                    connectionState = "connected",
                    semanticState = "warm",
                    version = "0.11.8",
                    processId = 22440,
                    lastSeenUtc = FixtureNow.AddMilliseconds(-220)
                }
            },
            activeOperations = new object[]
            {
                new
                {
                    operationId = "op-live",
                    workspaceId = "ws-phoenix",
                    instanceId = "instance-writer",
                    tool = "implementations",
                    category = "semantic",
                    startedAtUtc = FixtureNow.AddSeconds(-1.82),
                    coldState = "warming",
                    state = "running"
                }
            },
            cursor = "preview-cursor-42",
            dataComplete = true
        };
    }

    internal static object Operations()
    {
        object[] items =
        {
            new
            {
                operationId = "op-live",
                workspaceId = "ws-phoenix",
                instanceId = "instance-writer",
                tool = "implementations",
                category = "semantic",
                startedAtUtc = FixtureNow.AddSeconds(-1.82),
                durationMs = 1_820,
                state = "running",
                outcome = "running",
                confidence = "pending",
                coldState = "warming",
                reason = "cluster_cold_load",
                summary = "Loading the owner cluster",
                timings = new { gateWaitMs = 22, fingerprintMs = 84, topologyMs = 118, projectLoadMs = 1_596 },
                counts = new { requested = 6, loaded = 4, reloaded = 0, failed = 0 }
            },
            new
            {
                operationId = "op-041",
                workspaceId = "ws-phoenix",
                instanceId = "instance-follower",
                tool = "references",
                category = "semantic",
                startedAtUtc = FixtureNow.AddSeconds(-7),
                durationMs = 184,
                state = "complete",
                outcome = "success",
                confidence = "exact",
                coldState = "warm",
                reason = (string?)null,
                summary = "Compiler-exact · 14 references",
                timings = new { gateWaitMs = 4, fingerprintMs = 12, topologyMs = 18, projectLoadMs = 0 },
                counts = new { requested = 3, loaded = 3, reloaded = 0, failed = 0 }
            },
            new
            {
                operationId = "op-040",
                workspaceId = "ws-phoenix",
                instanceId = "instance-writer",
                tool = "search_symbol",
                category = "indexed",
                startedAtUtc = FixtureNow.AddSeconds(-18),
                durationMs = 31,
                state = "complete",
                outcome = "success",
                confidence = "indexed",
                coldState = "not_applicable",
                reason = (string?)null,
                summary = "Indexed · 8 matches",
                timings = new { gateWaitMs = 0, fingerprintMs = 0, topologyMs = 0, projectLoadMs = 0 },
                counts = new { requested = 0, loaded = 0, reloaded = 0, failed = 0 }
            },
            new
            {
                operationId = "op-039",
                workspaceId = "ws-phoenix",
                instanceId = "instance-writer",
                tool = "type_hierarchy",
                category = "semantic",
                startedAtUtc = FixtureNow.AddSeconds(-36),
                durationMs = 4_000,
                state = "complete",
                outcome = "degraded",
                confidence = "partial",
                coldState = "cold",
                reason = "semantic_timeout",
                summary = "Deadline reached · lower bound retained",
                timings = new { gateWaitMs = 28, fingerprintMs = 166, topologyMs = 384, projectLoadMs = 3_422 },
                counts = new { requested = 12, loaded = 7, reloaded = 2, failed = 0 }
            },
            new
            {
                operationId = "op-038",
                workspaceId = "ws-ravendb",
                instanceId = "instance-ravendb",
                tool = "callers",
                category = "semantic",
                startedAtUtc = FixtureNow.AddMinutes(-1.4),
                durationMs = 242,
                state = "complete",
                outcome = "success",
                confidence = "exact",
                coldState = "warm",
                reason = (string?)null,
                summary = "Compiler-exact · 6 callers",
                timings = new { gateWaitMs = 5, fingerprintMs = 18, topologyMs = 32, projectLoadMs = 0 },
                counts = new { requested = 5, loaded = 5, reloaded = 0, failed = 0 }
            }
        };

        return new
        {
            items,
            nextCursor = (string?)null,
            returned = items.Length,
            total = items.Length,
            truncated = false,
            dataComplete = true
        };
    }

    internal static object Events()
    {
        object[] items =
        {
            new
            {
                eventId = "event-012",
                workspaceId = "ws-phoenix",
                instanceId = "instance-writer",
                timestampUtc = FixtureNow.AddSeconds(-42),
                severity = "warning",
                component = "semantic",
                code = "semantic.deadline_near",
                message = "Semantic work approached its response deadline.",
                correlationId = "op-039"
            },
            new
            {
                eventId = "event-011",
                workspaceId = "ws-phoenix",
                instanceId = "instance-writer",
                timestampUtc = FixtureNow.AddMinutes(-3.2),
                severity = "info",
                component = "index",
                code = "index.refresh_complete",
                message = "Incremental refresh published 14 file changes.",
                correlationId = "refresh-128"
            }
        };

        return new
        {
            items,
            nextCursor = (string?)null,
            returned = items.Length,
            total = items.Length,
            truncated = false,
            dataComplete = true
        };
    }
}
