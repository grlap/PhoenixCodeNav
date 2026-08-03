# Phoenix Operations Portal

This is the independent, loopback-only Phoenix operations website. It has no project reference to
Phoenix Core or MCP implementation code.

Run it from the workspace you want to inspect:

```powershell
dotnet run --project src/CodeNav.Portal/CodeNav.Portal.csproj -c Release
```

Open the one-time loopback URL printed to the console. The URL fragment contains the private
portal session token; the browser removes it after bootstrap and keeps it in `sessionStorage`.
Manual mode otherwise keeps the token in memory. Launcher mode also stores the token-bearing URL
in the owner-private runtime descriptor for reuse, then removes that descriptor on graceful
shutdown or stale-session recovery.

In a published Phoenix installation, ask the attached agent to open the Operations Portal. It
calls the MCP tool `open_operations_portal`, which starts or reuses the packaged `portal/`
companion for that workspace and returns the authenticated URL for the agent to show in the
conversation. The tool does not open a browser. Launcher mode emits exactly one private JSON
handshake to its redirected parent and suppresses normal logging, so portal output cannot corrupt
the MCP stdio transport. The launcher protocol is an implementation detail; use the MCP tool
instead of invoking `--launcher` manually.

The portal tails the existing bounded, privacy-safe
`.codenav/telemetry/phoenix-*.jsonl` files through anchored no-follow regular-file handles and
observes only an equally anchored `.codenav/index.db` file's presence and size. It never opens
SQLite, scans full tables for counts, follows workspace reparse points, or writes to the
workspace. Linux directory/no-follow opens use the running architecture's ABI, and bounded Unix
directory records stop at their first NUL instead of admitting padding as a filename. Live files
provide semantic operations, full-build progress, and per-process
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

After a Release build, verify launcher start/reuse/stale recovery, authentication, live tailing,
and truncation honesty:

```powershell
node src/CodeNav.Portal/verify-runtime.mjs
```
