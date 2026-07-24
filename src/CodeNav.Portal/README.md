# Phoenix Operations Portal

This is the independent, loopback-only Phoenix operations website. It has no project reference to
Phoenix Core or MCP implementation code.

Run it from the workspace you want to inspect:

```powershell
dotnet run --project src/CodeNav.Portal/CodeNav.Portal.csproj -c Release
```

Open the one-time loopback URL printed to the console. The URL fragment contains the in-memory
portal session token; the browser removes it after bootstrap and keeps it in `sessionStorage`.

The portal tails the existing bounded, privacy-safe
`.codenav/telemetry/phoenix-*.jsonl` files through anchored no-follow regular-file handles and
observes only an equally anchored `.codenav/index.db` file's presence and size. It never opens
SQLite, scans full tables for counts, follows workspace reparse points, or writes to the
workspace. Live files provide semantic operations, full-build progress, and per-process
version/schema/platform/feature identity. If neither source exists, the bounded fixture remains
visible for UI preview. An observed index without telemetry stays explicitly partial; oversized
telemetry strings and unsafe numeric/timestamp ranges are rejected per record without stopping the
tailer.

To inspect more than one workspace, set `PHOENIX_PORTAL_WORKSPACES` to a platform path-separated
list before launching the portal.

Frontend verification is dependency-free:

```powershell
node src/CodeNav.Portal/verify.mjs
```

After a Release build, verify authentication, live tailing, and truncation honesty:

```powershell
node src/CodeNav.Portal/verify-runtime.mjs
```
