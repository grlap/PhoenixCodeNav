import { access } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { spawn } from "node:child_process";
import { request } from "node:http";

const root = dirname(fileURLToPath(import.meta.url));
const dll = process.env.PHOENIX_PORTAL_DLL
  ?? join(root, "bin", "Release", "net10.0", "PhoenixCodeNav.Portal.dll");

await access(dll);

const child = spawn("dotnet", [dll], {
  cwd: dirname(dll),
  stdio: ["ignore", "pipe", "pipe"],
  windowsHide: true
});

let output = "";
let errorOutput = "";
child.stdout.setEncoding("utf8");
child.stderr.setEncoding("utf8");
child.stdout.on("data", (chunk) => { output += chunk; });
child.stderr.on("data", (chunk) => { errorOutput += chunk; });

try {
  const session = await waitForSession();
  const unauthenticatedPaths = [
    "/api/v1/bootstrap",
    "/API/v1/bootstrap",
    "/aPi/v1/bootstrap"
  ];

  for (const path of unauthenticatedPaths)
    await expectStatus(`${session.origin}${path}`, {}, 401);

  await expectStatus(
    `${session.origin}/api/v1/bootstrap`,
    { headers: { Authorization: `Bearer ${session.token}` } },
    200
  );

  console.log("Portal runtime verification passed: API path casing cannot bypass bearer authentication.");
} finally {
  await stopChild();
}

async function waitForSession() {
  const deadline = Date.now() + 15_000;
  while (Date.now() < deadline) {
    const match = output.match(/Open (http:\/\/[^/]+)\/#token=([^\s]+)/);
    if (match)
      return { origin: match[1], token: match[2] };

    if (child.exitCode != null)
      throw new Error(`Portal exited before startup (${child.exitCode}).\n${errorOutput}`);

    await delay(25);
  }

  throw new Error(`Timed out waiting for the portal session URL.\n${output}\n${errorOutput}`);
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
    const forceTimer = setTimeout(() => {
      if (child.exitCode == null)
        child.kill("SIGKILL");
    }, 2_000);

    child.once("close", () => {
      clearTimeout(forceTimer);
      resolve();
    });
    child.kill();
  });
}

function delay(milliseconds) {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}
