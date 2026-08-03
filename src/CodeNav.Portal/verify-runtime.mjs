import { access, appendFile, mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { spawn } from "node:child_process";
import { request } from "node:http";

const root = dirname(fileURLToPath(import.meta.url));
const dll = process.env.PHOENIX_PORTAL_DLL
  ?? join(root, "bin", "Release", "net10.0", "PhoenixCodeNav.Portal.dll");
const workspace = await mkdtemp(join(tmpdir(), "phoenix-portal-runtime-"));
const telemetryDirectory = join(workspace, ".codenav", "telemetry");
const telemetryStart = new Date().toISOString().replace(/\D/g, "").slice(0, 14);
const telemetryFile = join(
  telemetryDirectory,
  `phoenix-${process.pid}-${telemetryStart}-1.jsonl`
);

await access(dll);
await mkdir(telemetryDirectory, { recursive: true });
await writeFile(
  telemetryFile,
  serverInfo()
    + buildProgress("runtime-build", "running", "indexing_files", 9, 12)
    + semanticOperation("runtime-a", "references", 41),
  "utf8"
);

let child;
let output = "";
let errorOutput = "";
startOwner();

try {
  const session = await waitForSession();
  if (session.status !== "started")
    throw new Error(`Initial launcher unexpectedly reported ${session.status}.`);
  const reused = await runReuseHelper();
  if (reused.status !== "reused"
      || reused.url !== session.url
      || reused.pid !== session.pid)
    throw new Error("A second launcher did not reuse the live workspace portal exactly.");
  const unauthenticatedPaths = [
    "/api/v1/bootstrap",
    "/API/v1/bootstrap",
    "/aPi/v1/bootstrap"
  ];

  for (const path of unauthenticatedPaths)
    await expectStatus(`${session.origin}${path}`, {}, 401);

  await expectStatus(`${session.origin}/`, {}, 200);
  await expectStatus(`${session.origin}/assets/portal.js`, {}, 200);

  const headers = { Authorization: `Bearer ${session.token}` };
  await expectStatus(`${session.origin}/api/v1/bootstrap`, { headers }, 200);
  const health = await getJson(`${session.origin}/healthz`, {});
  const bootstrap = await waitForJson(
    `${session.origin}/api/v1/bootstrap`,
    headers,
    (body) => body.dataSource === "live"
      && body.indexes?.[0]?.currentBuild?.buildId === "runtime-build"
  );
  if (health.portalVersion !== bootstrap.portal?.version)
    throw new Error("Portal version differs between health and bootstrap surfaces.");
  const liveInstance = bootstrap.instances?.[0];
  if (liveInstance?.version !== "0.12.26"
      || liveInstance?.schemaVersion !== "18"
      || !liveInstance?.featureIds?.includes("operations-portal-live-build-status"))
    throw new Error("serverInfo did not reach the live status/capability model.");
  if (bootstrap.indexes?.[0]?.currentBuild?.buildId !== "runtime-build"
      || bootstrap.indexes?.[0]?.currentBuild?.symbolsWritten !== 321)
    throw new Error("buildProgress did not reach the live build model.");

  const first = await waitForJson(
    `${session.origin}/api/v1/operations`,
    headers,
    (body) => body.items?.some((item) => item.correlationId === "runtime-a")
  );
  const initial = first.items.find((item) => item.correlationId === "runtime-a");
  if (initial.tool !== "references" || initial.durationMs !== 41)
    throw new Error("Initial semantic operation was not normalized faithfully.");
  if (initial.timings?.topologyMs !== 3 || "topoMs" in initial.timings)
    throw new Error("Live timing keys do not match the portal API contract.");
  if (initial.coldState !== "unknown" || initial.confidence !== "exact" || initial.partial)
    throw new Error("Absent cold state or semantic confidence was fabricated.");

  await appendFile(
    telemetryFile,
    semanticOperation("runtime-b", "implementations", 73)
      + buildProgress("runtime-build", "completed", "finalizing", 12, 12),
    "utf8"
  );
  await waitForJson(
    `${session.origin}/api/v1/operations`,
    headers,
    (body) => body.items?.some((item) => item.correlationId === "runtime-b")
  );
  await waitForJson(
    `${session.origin}/api/v1/bootstrap`,
    headers,
    (body) => body.dataSource === "live"
      && body.indexes?.[0]?.currentBuild == null
  );
  const filtered = await getJson(
    `${session.origin}/api/v1/operations?outcome=completed&limit=1`,
    headers
  );
  if (filtered.total !== 2 || filtered.returned !== 1 || !filtered.nextCursor)
    throw new Error("Operation filtering or first cursor page is not honest.");
  const secondPage = await getJson(
    `${session.origin}/api/v1/operations?outcome=completed&limit=1&cursor=${encodeURIComponent(filtered.nextCursor)}`,
    headers
  );
  if (secondPage.returned !== 1
      || secondPage.items[0].operationId === filtered.items[0].operationId)
    throw new Error("Operation cursor did not advance stably.");
  const mismatchedCursor = await getJsonResponse(
    `${session.origin}/api/v1/operations?outcome=failed&limit=1&cursor=${encodeURIComponent(filtered.nextCursor)}`,
    headers
  );
  if (mismatchedCursor.status !== 400
      || mismatchedCursor.body?.error?.code !== "cursor_expired")
    throw new Error("A cursor reused with different filters was not rejected as expired.");
  await expectStatus(
    `${session.origin}/api/v1/operations?limit=501`,
    { headers },
    400
  );

  await appendFile(
    telemetryFile,
    `${JSON.stringify({ e: "telemetry_dropped", count: 3 })}\n`
      + "{malformed-json}\n"
      + `${JSON.stringify({ e: "telemetry_truncated", capBytes: 16 * 1024 * 1024 })}\n`,
    "utf8"
  );
  const partial = await waitForJson(
    `${session.origin}/api/v1/bootstrap`,
    headers,
    (body) => body.dataSource === "live"
      && body.dataComplete === false
      && body.telemetry?.truncatedFiles === 1
      && body.telemetry?.droppedRecords === 3
      && body.telemetry?.invalidRecords === 1
  );
  if (partial.telemetry.source !== "workspace_jsonl")
    throw new Error("Portal did not disclose its live telemetry source.");

  await stopChild();
  const priorSession = session;
  startOwner();
  const restarted = await waitForSession();
  if (restarted.status !== "started"
      || restarted.pid === priorSession.pid
      || restarted.url === priorSession.url)
    throw new Error("A stale launcher descriptor was not replaced by a fresh portal session.");

  console.log(
    "Portal runtime verification passed: launcher start/reuse/restart, auth, live tailing, paging, normalization, and truncation honesty."
  );
} finally {
  await stopChild();
  await rm(workspace, { recursive: true, force: true });
}

function semanticOperation(correlationId, tool, durationMs) {
  return `${JSON.stringify({
    e: "semanticOp",
    ts: new Date().toISOString(),
    corr: correlationId,
    tool,
    accessMode: "writer",
    result: "exact",
    clusterLoadMs: durationMs,
    queryMs: 0,
    ownerLoad: {
      gateWaitMs: 1,
      fingerprintMs: 2,
      topoMs: 3,
      projectLoadMs: durationMs - 6,
      requested: 4,
      loaded: 4,
      reloaded: 0,
      failed: 0
    }
  })}\n`;
}

function serverInfo() {
  return `${JSON.stringify({
    e: "serverInfo",
    ts: new Date().toISOString(),
    version: "0.12.26",
    buildStamp: "0.12.26+runtime",
    schemaVersion: "18",
    featureIds: [
      "operations-portal-jsonl-readonly",
      "operations-portal-live-build-status"
    ],
    featureCount: 2,
    platform: process.platform,
    accessMode: "writer",
    processId: process.pid
  })}\n`;
}

function buildProgress(buildId, state, phase, filesDone, filesTotal) {
  return `${JSON.stringify({
    e: "buildProgress",
    ts: new Date().toISOString(),
    buildId,
    state,
    reason: "startup_missing",
    accessMode: "writer",
    phase,
    phaseElapsedMs: 250,
    elapsedMs: 1000,
    filesDone,
    filesTotal,
    filesSkipped: 1,
    projectsFailed: 0,
    symbolsWritten: 321,
    bytesRead: 4096,
    filesPerSecond: 9.5,
    estimatedRemainingMs: 300
  })}\n`;
}

async function waitForSession() {
  const deadline = Date.now() + 30_000;
  while (Date.now() < deadline) {
    const newline = output.indexOf("\n");
    if (newline >= 0) {
      const handshake = JSON.parse(output.slice(0, newline));
      const url = new URL(handshake.url);
      if (handshake.protocolVersion !== 1
          || handshake.status !== "started"
          || handshake.readOnly !== true
          || handshake.workspaceCount !== 1
          || !url.hash.startsWith("#token="))
        throw new Error(`Invalid launcher handshake: ${output.slice(0, newline)}`);
      return {
        ...handshake,
        origin: url.origin,
        token: url.hash.slice("#token=".length)
      };
    }

    if (child.exitCode != null)
      throw new Error(`Portal exited before startup (${child.exitCode}).\n${errorOutput}`);

    await delay(25);
  }

  throw new Error(`Timed out waiting for the portal session URL.\n${output}\n${errorOutput}`);
}

async function runReuseHelper() {
  const helper = spawn(
    "dotnet",
    [dll, "--launcher", "--workspace-root", workspace],
    {
      cwd: dirname(dll),
      stdio: ["ignore", "pipe", "pipe"],
      windowsHide: true
    }
  );
  let helperOutput = "";
  let helperError = "";
  helper.stdout.setEncoding("utf8");
  helper.stderr.setEncoding("utf8");
  helper.stdout.on("data", (chunk) => { helperOutput += chunk; });
  helper.stderr.on("data", (chunk) => { helperError += chunk; });
  const exitCode = await Promise.race([
    new Promise((resolve) => helper.once("close", resolve)),
    delay(10_000).then(() => "timeout")
  ]);
  if (exitCode === "timeout") {
    helper.kill("SIGKILL");
    throw new Error(`Reuse helper timed out.\n${helperOutput}\n${helperError}`);
  }
  if (exitCode !== 0)
    throw new Error(`Reuse helper exited ${exitCode}.\n${helperOutput}\n${helperError}`);
  const handshake = JSON.parse(helperOutput.trim());
  if (handshake.protocolVersion !== 1 || handshake.readOnly !== true)
    throw new Error(`Invalid reuse handshake: ${helperOutput}`);
  return handshake;
}

function startOwner() {
  output = "";
  errorOutput = "";
  child = spawn(
    "dotnet",
    [dll, "--launcher", "--workspace-root", workspace],
    {
      cwd: dirname(dll),
      stdio: ["ignore", "pipe", "pipe"],
      windowsHide: true
    }
  );
  child.stdout.setEncoding("utf8");
  child.stderr.setEncoding("utf8");
  child.stdout.on("data", (chunk) => { output += chunk; });
  child.stderr.on("data", (chunk) => { errorOutput += chunk; });
}

async function waitForJson(url, headers, predicate) {
  const deadline = Date.now() + 10_000;
  let last = null;
  while (Date.now() < deadline) {
    last = await getJson(url, headers);
    if (predicate(last))
      return last;
    await delay(75);
  }
  throw new Error(`Timed out waiting for live portal data: ${JSON.stringify(last)}`);
}

async function getJson(url, headers) {
  return new Promise((resolve, reject) => {
    const pending = request(url, { headers, agent: false }, (response) => {
      let body = "";
      response.setEncoding("utf8");
      response.on("data", (chunk) => { body += chunk; });
      response.once("end", () => {
        if (response.statusCode !== 200) {
          reject(new Error(`${new URL(url).pathname} returned ${response.statusCode}`));
          return;
        }
        try {
          resolve(JSON.parse(body));
        } catch (error) {
          reject(error);
        }
      });
    });
    pending.once("error", reject);
    pending.end();
  });
}

async function getJsonResponse(url, headers) {
  return new Promise((resolve, reject) => {
    const pending = request(url, { headers, agent: false }, (response) => {
      let body = "";
      response.setEncoding("utf8");
      response.on("data", (chunk) => { body += chunk; });
      response.once("end", () => {
        try {
          resolve({
            status: response.statusCode,
            body: body ? JSON.parse(body) : null
          });
        } catch (error) {
          reject(error);
        }
      });
    });
    pending.once("error", reject);
    pending.end();
  });
}

async function expectStatus(url, options, expected) {
  const status = await new Promise((resolve, reject) => {
    const pending = request(url, { ...options, agent: false }, (response) => {
      response.resume();
      response.once("end", () => resolve(response.statusCode));
    });
    pending.once("error", reject);
    pending.end();
  });

  if (status !== expected)
    throw new Error(`${new URL(url).pathname} returned ${status}; expected ${expected}`);
}

async function stopChild() {
  if (child.exitCode != null)
    return;

  await new Promise((resolve) => {
    let settled = false;
    const finish = () => {
      if (settled)
        return;
      settled = true;
      clearTimeout(forceTimer);
      resolve();
    };
    const forceTimer = setTimeout(() => {
      if (child.exitCode == null)
        child.kill("SIGKILL");
      else
        finish();
    }, 2_000);

    child.once("close", finish);
    if (!child.kill() && child.exitCode != null)
      finish();
  });
}

function delay(milliseconds) {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}
