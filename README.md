# PhoenixCodeNav

Local code navigation for large **C# and mixed C#/F# monorepos**, available through
**MCP and a CLI**. Built for coding agents working across thousands of legacy and
SDK-style projects.

Find symbols, follow references, inspect dependencies, and gather focused context
without reading entire files. Results include confidence and coverage information
so agents can distinguish compiler-bound answers from indexed candidates.

[Install](#install-work-machine) · [CLI quick start](#use-from-the-command-line-cli) ·
[Connect an MCP client](#choose-how-to-connect) · [Documentation](#documentation)

> Named after **Phoenix A** — built to navigate the heaviest repositories.
> No relation to Apache Phoenix.

## What it does

- **Find code:** ranked file, text, and symbol search; file outlines.
- **Follow code:** definitions, references, implementations, callers, and callees.
- **Understand the repo:** project graphs, dependency paths, impact, and context packs.
- **Stay current:** incremental indexing and Git-aware freshness.
- **Share work:** MCP clients and CLI calls use the same local workspace daemon,
  index, and warm semantic state.

C# uses Roslyn; F# uses FSharp.Compiler.Service. Text search also covers Markdown,
SQL, and F# scripts. Phoenix does not execute MSBuild or restore your workspace's
packages. See [language and model limits](#language-and-model-limits) below.

## Install (work machine)

A self-contained publish does not need a separately installed .NET runtime.
To build from source, install the **.NET 10 SDK** and run this from the Phoenix checkout:

```text
dotnet publish src/CodeNav.Mcp -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/win-x64
```

Copy the **entire publish directory** to your install location, such as
`C:/tools/phoenix/`. Keep `FSharp.Core.dll` and the `portal/` companion beside
`PhoenixCodeNav.Mcp.exe`.

For macOS or Linux, use the matching runtime identifier, such as `osx-arm64` or
`linux-x64`, and run `PhoenixCodeNav.Mcp` without `.exe`. A framework-dependent
alternative requires the .NET 10 runtime:

```text
dotnet publish src/CodeNav.Mcp -c Release --self-contained false -o artifacts/portable
```

Semantic navigation also needs suitable compiler references and any package DLLs
used by the project. C# is net472-first: Phoenix looks for reference assemblies in
Visual Studio/Build Tools, the NuGet cache, or the installed .NET Framework.
`CODENAV_NET472_REFS` can explicitly select a reference-assembly directory.
Missing inputs are reported rather than hidden.
[Deployment details](docs/design.md#deployment)

Add `.codenav/` to the repository's `.gitignore`; it holds the local index and telemetry.

## Use from the command line (CLI)

No MCP client setup or separate CLI installation is needed. With the publish directory
on your `PATH`, run these from the repository you want to navigate:

```powershell
PhoenixCodeNav.Mcp.exe tools
PhoenixCodeNav.Mcp.exe help search_symbol
PhoenixCodeNav.Mcp.exe search_symbol --workspace-root . --query YourTypeName --limit 5
```

Replace `YourTypeName` with a symbol from your code. If Phoenix is not on `PATH`, use
its full path, for example `& 'C:/tools/phoenix/PhoenixCodeNav.Mcp.exe' tools` in PowerShell.

`tools`, `help`, and `schema` are offline discovery commands; they do not start a daemon.
A tool call starts or joins the workspace daemon. The first call may report
`index_building`; inspect `server_capabilities`, wait for the index to be ready,
then follow the returned retry guidance:

```powershell
PhoenixCodeNav.Mcp.exe server_capabilities --workspace-root . --pretty
```

Commands return JSON on stdout and diagnostics on stderr. Use `--pretty` for readable
output, `schema <tool>` for exact arguments, and `--json` or `--args-file` for a
complete JSON request. CLI and MCP calls share the same tool-result contract.

[CLI arguments, exit codes, and recovery](docs/agent-instructions.md#shell-only-agents)

## Choose how to connect

Use the CLI, register Phoenix as an MCP server, or use both against the same workspace.

### Attach to Claude Code

Add a project-scoped `.mcp.json` at the repository root:

```json
{
  "mcpServers": {
    "phoenix": {
      "command": "C:/tools/phoenix/PhoenixCodeNav.Mcp.exe",
      "args": ["--workspace-root", "."]
    }
  }
}
```

### Attach to Codex

Add to `~/.codex/config.toml`:

```toml
[mcp_servers.phoenix]
command = "C:/tools/phoenix/PhoenixCodeNav.Mcp.exe"
args = ["--workspace-root", "C:/path/to/repo"]
```

Use your installed executable path. Add the [agent instructions](docs/agent-instructions.md)
to your repository's `CLAUDE.md` or `AGENTS.md` so agents know when and how to use Phoenix.
A [repository-installable skill template](skills/phoenix/SKILL.md) is also included in
the publish directory; replace its `{{PHOENIX_EXE}}` placeholders before installing it.

For lower-noise indexed searches, optionally set
`CODENAV_DEFAULT_QUERY_SCOPE=first_party` in the server environment.
Per-call `queryScope: "all"` restores all indexed content.

<a id="why-not-just-grep"></a>

## Language and model limits

C# supports semantic definitions, references, implementations, callers, callees, and
type hierarchy. F# supports position-based symbol lookup, definitions, references,
implementations, callers, and callees. F# type hierarchy and semantic navigation
through C# project references are not supported; `.fsx` is text-only.

F# uses the **simple project model by default**, sharing raw-project construction
with C#. It preserves F# source ordering and literal parser options, but ignores imports
and build conditions and uses a direct package-cache heuristic.

For bounded F# property, condition, import, and restored-package evaluation, set
`PHOENIX_FSHARP_PROJECT_MODEL=evaluated` **before starting the workspace daemon**.
Restart an existing daemon for the change to take effect; unset the variable or set
`simple` to return to the default. There is no automatic fallback between models.
Switching to evaluated mode may require a startup sweep to prepare its indexed inputs.

**Exact binding is not proof of a reproduced build.** F# simple-model result counts
remain approximate (`totalIsApproximate: true`), even when individual symbol bindings
are `exact`. Ignored conditions can add results; omitted imports can hide them.
Other errors, omissions, and incomplete scans carry explicit confidence and coverage details.

[Project-model behavior](docs/design.md#f-project-models) ·
[Confidence and coverage](docs/design.md#confidence-model) ·
[Language boundaries for agents](docs/agent-instructions.md#language-boundary)

## Keeping the index fresh

The daemon watches source and project changes, catches up at startup, and tracks Git
branch switches and pulls. Use `repo_overview` to inspect freshness and `refresh_index`
to request a refresh. Separate Git worktrees have separate indexes; Phoenix never
creates or deletes worktrees.

The optional `worktrees` and `index_worktree` tools support Windows and Linux,
not macOS. Ordinary macOS navigation remains available.

To inspect local operations, ask your agent to call `open_operations_portal`.
It returns a link to the packaged, loopback-only, read-only portal.

[Freshness and Git behavior](docs/design.md#freshness--and-how-git-operations-are-handled) ·
[Missing, stale, or partial results](docs/agent-instructions.md#act-on-the-response-contract)

## Documentation

- [Why Phoenix exists](docs/intro.md) — use cases and comparison with text search.
- [Agent instructions](docs/agent-instructions.md) — tool selection, CLI usage, and recovery.
- [Architecture and reference](docs/design.md) — project models, packages, diagnostics,
  indexing, daemon lifecycle, deployment, and response contracts.
- [Agent-experience roadmap](docs/agent-experience-roadmap.md) — planned improvements.

## Development

From the source checkout:

```text
dotnet restore PhoenixCodeNav.sln
dotnet build PhoenixCodeNav.sln -c Release --no-restore
dotnet test PhoenixCodeNav.sln -c Release --no-build --no-restore
pwsh -NoProfile -File ./scripts/test-roslyn-mcp.ps1
node ./website/verify.mjs
```

The external MCP gate requires pinned Roslyn/F# submodules and the reference-assembly
fixtures prepared by the build; it creates fresh indexes and does not repair its inputs.
The full suite needs directory-link support. See [contribution and review requirements](AGENTS.md)
before check-in. Benchmarks and the synthetic workspace generator live in
`src/CodeNav.Bench` and `src/CodeNav.WorkspaceGen`.

## License

[Apache License 2.0](LICENSE). Copyright 2026 Greg Lapinski.
Third-party components retain their own licenses; see [NOTICE](NOTICE),
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt), and [legal/](legal/).
