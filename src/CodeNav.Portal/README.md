# Phoenix Operations Portal preview

This is the independent Project B presentation shell. It has no project reference to Phoenix Core,
MCP, SQLite, indexing, search, or semantic implementation code.

Run it directly while the telemetry producer is developed in parallel:

```powershell
dotnet run --project src/CodeNav.Portal/CodeNav.Portal.csproj -c Release
```

Open the one-time loopback URL printed to the console. The URL fragment contains the in-memory
portal session token; the browser removes it after bootstrap and keeps it in `sessionStorage`.

The current data is a bounded presentation fixture behind the portal API adapter. Live IPC and
retention can replace that adapter later without changing the dashboard structure.

Frontend verification is dependency-free:

```powershell
node src/CodeNav.Portal/verify.mjs
```

After a Release build, verify the live loopback authentication boundary:

```powershell
node src/CodeNav.Portal/verify-runtime.mjs
```
