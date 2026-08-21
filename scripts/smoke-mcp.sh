#!/usr/bin/env bash
# Smoke-test the CodeNav MCP server over stdio JSON-RPC against a workspace.
# Usage: scripts/smoke-mcp.sh <workspace-root>
set -u
WS="${1:-C:/temp/acme-2k}"
OUT=/tmp/codenav-smoke-out.jsonl
ERR=/tmp/codenav-smoke-err.log

cd "$(dirname "$0")/.."

send() { printf '%s\n' "$1"; }

{
  send '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"0"}}}'
  sleep 2
  send '{"jsonrpc":"2.0","method":"notifications/initialized"}'
  send '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
  sleep 1
  send '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"repo_overview","arguments":{}}}'
  sleep 1
  send '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"search_symbol","arguments":{"query":"BalanceResolver","limit":3}}}'
  sleep 1
  send '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"outline","arguments":{"path":"src/Platform/Common/Acme.Platform.Common/Guard.cs"}}}'
  sleep 1
  send '{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"references","arguments":{"name":"AcmeException","maxFiles":200,"samplesPerGroup":1}}}'
  sleep 1
  send '{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"project_graph","arguments":{"project":"Acme.Platform.Common","direction":"upstream","depth":1}}}'
  sleep 1
  send '{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"source_context","arguments":{"path":"src/Platform/Common/Acme.Platform.Common/Guard.cs","spans":"1-12"}}}'
  sleep 2
} | dotnet run --project src/CodeNav.Mcp -c Release --no-build -- --workspace-root "$WS" > "$OUT" 2> "$ERR"

echo "=== responses ($(wc -l < "$OUT") lines) ==="
python - "$OUT" <<'EOF'
import json, sys
for line in open(sys.argv[1], encoding='utf-8'):
    line = line.strip()
    if not line:
        continue
    try:
        msg = json.loads(line)
    except Exception:
        print('UNPARSEABLE:', line[:200]); continue
    rid = msg.get('id')
    if rid is None:
        continue
    if 'error' in msg:
        print(f'[{rid}] JSONRPC ERROR: {msg["error"]}')
        continue
    result = msg.get('result', {})
    if rid == 1:
        si = result.get('serverInfo', {})
        print(f'[1] initialize: {si.get("name")} {si.get("version")} proto={result.get("protocolVersion")}')
    elif rid == 2:
        tools = [t['name'] for t in result.get('tools', [])]
        print(f'[2] tools/list: {len(tools)} tools: {", ".join(tools)}')
    else:
        content = result.get('content', [])
        text = content[0]['text'] if content else ''
        try:
            payload = json.loads(text)
            keys = list(payload.keys())
            print(f'[{rid}] keys={keys}')
            snippet = json.dumps(payload)[:600]
            print(f'     {snippet}')
        except Exception:
            print(f'[{rid}] raw: {text[:300]}')
EOF
echo "=== stderr tail ==="
tail -5 "$ERR"
