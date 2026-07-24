import { readFile, access } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { createBuildViewModel } from "./wwwroot/assets/portal-model.js";

const root = dirname(fileURLToPath(import.meta.url));
const web = join(root, "wwwroot");
const index = await readFile(join(web, "index.html"), "utf8");
const css = await readFile(join(web, "assets", "portal.css"), "utf8");
const script = await readFile(join(web, "assets", "portal.js"), "utf8");
const model = await readFile(join(web, "assets", "portal-model.js"), "utf8");
const project = await readFile(join(root, "CodeNav.Portal.csproj"), "utf8");
const program = await readFile(join(root, "Program.cs"), "utf8");

const failures = [];
const requireText = (content, text, description) => {
  if (!content.includes(text)) failures.push(description);
};
const reject = (content, pattern, description) => {
  if (pattern.test(content)) failures.push(description);
};

for (const id of [
  "main",
  "overview",
  "activity",
  "instances",
  "status",
  "server-status-list",
  "capability-list",
  "workspace-select",
  "motion-toggle",
  "operation-dialog",
  "data-mode"
]) {
  requireText(index, `id="${id}"`, `Missing required UI anchor #${id}`);
}

requireText(css, "prefers-reduced-motion: reduce", "Reduced-motion rules are required");
requireText(css, ".motion-paused", "Visible motion pause behavior is required");
requireText(script, "aria-pressed", "Motion control must expose its state");
requireText(script, "removeAttribute(\"aria-valuenow\")", "Indeterminate progress must not expose a fabricated percentage");
requireText(script, "window.setInterval", "Live portal snapshots must refresh without a page reload");
requireText(script, "formatToken(semanticState)", "Semantic summary must preserve unknown and warming states");
requireText(script, 'build?.live === true', "Build presentation must honor producer liveness");
requireText(script, '"STALLED"', "Dead builds must render as stalled rather than live");
requireText(script, 'index?.state === "ready"', "Ready presentation must require explicit index evidence");
requireText(model, "total unknown", "Unknown build totals must remain visibly unknown");
requireText(program, "IPAddress.Loopback", "Portal must bind to loopback explicitly");
requireText(program, "FixedTimeEquals", "Bearer token comparison must be constant-time");
requireText(program, "ContentSecurityPolicy", "Portal must send a CSP");
requireText(program, "HasAllowedOrigin", "Portal must reject cross-origin requests");
requireText(program, '"/api/{**path}"', "Unknown diagnostic routes must fail closed");
requireText(program, "PortalDataSource", "Portal APIs must use the independent live data source");
const dataSource = await readFile(join(root, "PortalDataSource.cs"), "utf8");
const dataApi = await readFile(join(root, "PortalDataSource.Api.cs"), "utf8");
const pathGuard = await readFile(join(root, "PortalPathGuard.cs"), "utf8");
requireText(dataSource, "OpenRegularFile", "Live telemetry reads must use anchored regular-file handles");
requireText(dataSource, "telemetry_truncated", "Telemetry truncation must remain visible");
requireText(dataSource, "buildProgress", "Live full-build progress must be consumed");
requireText(dataSource, "serverInfo", "Live server identity must be consumed");
requireText(dataSource, "MaxReadBytesPerWorkspaceRefresh", "Live ingestion needs an aggregate refresh budget");
requireText(dataApi, "MaxPageResponseBytes", "Portal API pages need a response-byte budget");
requireText(dataApi, "TryDecodeCursor", "Operation/event pages need validated stable cursors");
requireText(pathGuard, "UnixOpenAt", "Unix workspace reads must be rooted at an open directory");
requireText(pathGuard, "WindowsFileFlagOpenReparsePoint", "Windows workspace reads must open reparse points without following them");
requireText(pathGuard, "PortalFileIdentity", "Live telemetry cursors must track stable opened-file identity");
requireText(pathGuard, "TryEnumerateDirectoryNames", "Telemetry discovery must enumerate from its anchored directory handle");

reject(index, /<(?:script|style)[^>]*>\s*(?!<\/)/i, "Inline script/style blocks are not allowed");
reject(index, /\son[a-z]+\s*=/i, "Inline DOM event handlers are not allowed");
reject(index, /(?:src|href)=["']https?:\/\//i, "Runtime assets must remain local");
reject(project, /ProjectReference/i, "Portal project must not reference Phoenix implementation projects");
reject(program, /CodeNav\.(?:Core|Mcp)/, "Portal server must not depend on Phoenix implementation namespaces");
reject(script, /innerHTML|outerHTML|insertAdjacentHTML/, "Telemetry-derived UI must use safe DOM construction");
reject(script, /warmCount === instances\.length \? "Warm"/, "Unknown semantic state must not be rendered as cold or warm");

for (const asset of ["assets/portal.css", "assets/portal.js", "assets/portal-model.js", "assets/favicon.svg"]) {
  try {
    await access(join(web, asset));
  } catch {
    failures.push(`Missing local asset ${asset}`);
  }
}

const unknownBuild = createBuildViewModel({
  elapsedMs: 1200,
  filesProcessed: 480,
  filesTotal: null,
  progress: null,
  throughputPerSecond: null,
  etaSeconds: null,
  filesSkipped: null
});
const unavailableIndex = createBuildViewModel({
  progress: null,
  filesProcessed: null,
  filesTotal: null,
  throughputPerSecond: null,
  elapsedMs: null,
  etaSeconds: null,
  filesSkipped: null
});

if (unknownBuild.determinate || unknownBuild.progress != null || unknownBuild.percent != null)
  failures.push("Unknown build totals must produce indeterminate progress");
if (!unknownBuild.progressLabel.toLowerCase().includes("unknown"))
  failures.push("Unknown build progress must be labeled visibly unknown");
if (!unknownBuild.filesLabel.toLowerCase().includes("total unknown"))
  failures.push("Unknown file totals must be labeled visibly unknown");
if (unavailableIndex.determinate
    || unavailableIndex.progress != null
    || unavailableIndex.progressLabel.toLowerCase().includes("100"))
  failures.push("An unavailable index must not fabricate a completed build");
for (const [name, value] of [
  ["throughput", unknownBuild.rateLabel],
  ["ETA", unknownBuild.etaLabel],
  ["skipped files", unknownBuild.skippedLabel]
]) {
  if (value === "0" || value === "0s" || value === "NaN" || value.includes?.("NaN"))
    failures.push(`Unknown ${name} must not be fabricated as zero or NaN`);
}

if (failures.length) {
  console.error("Portal verification failed:");
  for (const failure of failures) console.error(`- ${failure}`);
  process.exitCode = 1;
} else {
  console.log("Portal verification passed: local assets, scope boundary, security shell, motion, and accessibility anchors are present.");
}
