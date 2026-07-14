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
  "workspace-select",
  "motion-toggle",
  "operation-dialog"
]) {
  requireText(index, `id="${id}"`, `Missing required UI anchor #${id}`);
}

requireText(css, "prefers-reduced-motion: reduce", "Reduced-motion rules are required");
requireText(css, ".motion-paused", "Visible motion pause behavior is required");
requireText(script, "aria-pressed", "Motion control must expose its state");
requireText(script, "removeAttribute(\"aria-valuenow\")", "Indeterminate progress must not expose a fabricated percentage");
requireText(model, "total unknown", "Unknown build totals must remain visibly unknown");
requireText(program, "IPAddress.Loopback", "Portal must bind to loopback explicitly");
requireText(program, "FixedTimeEquals", "Bearer token comparison must be constant-time");
requireText(program, "ContentSecurityPolicy", "Portal must send a CSP");
requireText(program, "HasAllowedOrigin", "Portal must reject cross-origin requests");
requireText(program, '"/api/{**path}"', "Unknown diagnostic routes must fail closed");

reject(index, /<(?:script|style)[^>]*>\s*(?!<\/)/i, "Inline script/style blocks are not allowed");
reject(index, /\son[a-z]+\s*=/i, "Inline DOM event handlers are not allowed");
reject(index, /(?:src|href)=["']https?:\/\//i, "Runtime assets must remain local");
reject(project, /ProjectReference/i, "Portal project must not reference Phoenix implementation projects");
reject(program, /CodeNav\.(?:Core|Mcp)/, "Portal server must not depend on Phoenix implementation namespaces");
reject(script, /innerHTML|outerHTML|insertAdjacentHTML/, "Telemetry-derived UI must use safe DOM construction");

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

if (unknownBuild.determinate || unknownBuild.progress != null || unknownBuild.percent != null)
  failures.push("Unknown build totals must produce indeterminate progress");
if (!unknownBuild.progressLabel.toLowerCase().includes("unknown"))
  failures.push("Unknown build progress must be labeled visibly unknown");
if (!unknownBuild.filesLabel.toLowerCase().includes("total unknown"))
  failures.push("Unknown file totals must be labeled visibly unknown");
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
