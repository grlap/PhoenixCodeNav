#!/usr/bin/env node

import { createHash } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import { dirname, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const root = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(root, "..");
const html = readFileSync(resolve(root, "index.html"), "utf8");
const roslynBaseline = JSON.parse(readFileSync(resolve(repoRoot, "tests/integration/roslyn-mcp-baseline.json"), "utf8"));
const failures = [];
let checks = 0;

function check(condition, message) {
  checks += 1;
  if (!condition) failures.push(message);
}

function matches(pattern, value = html) {
  return [...value.matchAll(pattern)];
}

function attribute(tag, name) {
  return new RegExp(`\\b${name}="([^"]*)"`, "i").exec(tag)?.[1] ?? "";
}

function metaContent(attributeName, attributeValue) {
  const tag = matches(/<meta\b[^>]*>/gi).map((match) => match[0])
    .find((candidate) => attribute(candidate, attributeName).toLowerCase() === attributeValue.toLowerCase());
  return tag ? attribute(tag, "content") : "";
}

function isAbsoluteHttps(value) {
  try {
    return new URL(value).protocol === "https:";
  } catch {
    return false;
  }
}

function htmlCode(id) {
  const escaped = id.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const match = new RegExp(`<pre\\s+id="${escaped}"><code>([\\s\\S]*?)<\\/code><\\/pre>`, "i").exec(html);
  if (!match) return "";
  return match[1]
    .replaceAll("&lt;", "<")
    .replaceAll("&gt;", ">")
    .replaceAll("&amp;", "&");
}

const args = process.argv.slice(2);
const launchMode = args.includes("--launch");
check(args.every((argument) => argument === "--launch"), "Only the optional --launch argument is supported.");

const ids = matches(/\bid="([^"]+)"/g).map((match) => match[1]);
const idSet = new Set(ids);
check(ids.length === idSet.size, "Every id must be unique.");

for (const match of matches(/<a\b[^>]*\bhref="#([^"]+)"[^>]*>/gi)) {
  check(idSet.has(match[1]), `Fragment link #${match[1]} must resolve to an element id.`);
}

for (const match of matches(/\b(?:aria-controls|aria-labelledby)="([^"]+)"/gi)) {
  for (const id of match[1].trim().split(/\s+/)) {
    check(idSet.has(id), `ARIA reference ${id} must resolve to an element id.`);
  }
}

for (const match of matches(/\bdata-copy="([^"]+)"/gi)) {
  check(idSet.has(match[1]), `Copy source ${match[1]} must resolve to an element id.`);
}

for (const match of matches(/<a\b[^>]*\btarget="_blank"[^>]*>/gi)) {
  check(/\brel="[^"]*\bnoopener\b[^"]*"/i.test(match[0]), "Every target=_blank link must use rel=noopener.");
}

const localReferences = matches(/\b(?:href|src)="([^"]+)"/gi)
  .map((match) => match[1])
  .filter((reference) => !/^(?:[a-z]+:|#)/i.test(reference));
const rootPrefix = `${root.toLowerCase()}${sep}`;
const assets = localReferences.map((reference) => {
  const cleanReference = reference.split(/[?#]/, 1)[0];
  const path = resolve(root, cleanReference);
  const contained = path.toLowerCase().startsWith(rootPrefix);
  return { reference, cleanReference, path, contained, exists: contained && existsSync(path) };
});

for (const asset of assets) {
  check(asset.contained, `Local asset ${asset.reference} must stay inside website/.`);
  check(asset.exists, `Local asset ${asset.reference} must exist.`);
}

const stylesheets = assets.filter((asset) => asset.cleanReference.endsWith(".css"));
const scripts = assets.filter((asset) => asset.cleanReference.endsWith(".js"));
check(stylesheets.length === 1, "The page must reference exactly one local stylesheet.");
check(scripts.length === 1, "The page must reference exactly one local JavaScript file.");

for (const asset of [...stylesheets, ...scripts]) {
  const hashMatch = /\.([0-9a-f]{10})\.(?:css|js)$/i.exec(asset.cleanReference);
  check(Boolean(hashMatch), `Cacheable asset ${asset.reference} must contain a ten-character content hash.`);
  if (!hashMatch || !asset.exists) continue;
  const digest = createHash("sha256").update(readFileSync(asset.path)).digest("hex").slice(0, 10);
  check(digest === hashMatch[1].toLowerCase(), `Content hash in ${asset.reference} must match the file contents.`);
}

const structuredData = matches(/<script\b[^>]*type="application\/ld\+json"[^>]*>([\s\S]*?)<\/script>/gi);
check(structuredData.length === 1, "The page must contain exactly one JSON-LD block.");
if (structuredData.length === 1) {
  try {
    JSON.parse(structuredData[0][1]);
    check(true, "Structured data must be valid JSON.");
  } catch (error) {
    check(false, `Structured data must be valid JSON: ${error.message}`);
  }
}

check(/<html\b[^>]*\bclass="[^"]*\bno-js\b[^"]*"[^>]*\blang="en"/i.test(html), "The document must ship with the no-JavaScript fallback class and language.");
check(!/<button\b(?![^>]*\btype="button")[^>]*>/i.test(html), "Every button must declare type=button.");
check(!/<article\b[^>]*\btabindex=/i.test(html), "Noninteractive article cards must not be keyboard tab stops.");

const understanding = roslynBaseline.understandingTarget;
const understandingSource = htmlCode("anatomy-raw");
const understandingRequest = htmlCode("anatomy-enriched");
const understandingChain = htmlCode("anatomy-edges");
const definitionExampleText = htmlCode("anatomy-envelope");
const understandingConclusion = htmlCode("anatomy-workspace");
const understandingPathSlash = understanding.path.lastIndexOf("/") + 1;
const understandingPathPrefix = understanding.path.slice(0, understandingPathSlash);
const understandingFileName = understanding.path.slice(understandingPathSlash);

check(understandingSource.includes(understanding.propertySignature), "The code-understanding walkthrough must show the receiver's real declared type.");
check(understandingSource.includes(understanding.followOnText), "The code-understanding walkthrough must show the real follow-on Compilation use.");
check(understandingRequest.includes(`path: "${understandingPathPrefix}"`) &&
  understandingRequest.includes(`"${understandingFileName}"`),
"The definition request must target the pinned Roslyn understanding fixture.");
check(understandingRequest.includes(`name: "${understanding.calleeName}"`) &&
  understandingRequest.includes(`line: ${understanding.calleeLine}`) &&
  understandingRequest.includes(`column: ${understanding.calleeColumn}`),
"The definition request must preserve the integration-tested symbol position.");
check(/mode:\s*"semantic"/.test(understandingRequest) && /includeBody:\s*true/.test(understandingRequest),
  "The code-understanding request must require semantic resolution with live declaration source.");
check(understandingChain.includes("Task<Compilation>") &&
  understandingChain.includes("newCompilation : Compilation"),
"The reasoning chain must connect the bound task return to the awaited variable type.");
check(Boolean(definitionExampleText), "The code-understanding walkthrough must publish a definition response example.");
if (definitionExampleText) {
  try {
    const example = JSON.parse(definitionExampleText);
    check(example.name === understanding.calleeName, "The definition example must name the integration-tested callee.");
    check(typeof example.symbol === "object" && example.symbol !== null &&
      example.symbol.kind === "method" &&
      example.symbol.containingType === "RegularCompilationTracker",
    "The definition example must expose the compiler-bound method identity.");
    check(Array.isArray(example.declarations) &&
      example.declarations.some((declaration) =>
        declaration.path === understanding.calleeDefinitionPath &&
        Number.isInteger(declaration.startLine) &&
        Number.isInteger(declaration.endLine)),
    "The definition example must point at the pinned Roslyn declaration.");
    check(example.body?.path === understanding.calleeDefinitionPath &&
      example.body.source.includes(understanding.calleeSignature) &&
      example.body.freshness === "live",
    "The definition example must carry the real live Task<Compilation> declaration source.");
    check(typeof example.meta === "object" && example.meta !== null, "The definition example must contain meta.");
    check(!Object.hasOwn(example, "implementations") &&
      !Object.hasOwn(example, "totalReferences") &&
      !Object.hasOwn(example, "groups"),
    "The definition example must not borrow fields from other MCP operations.");
    check(example.meta.confidence === "exact" && example.meta.navigationLayer === "semantic", "The definition example must label compiler evidence as exact semantic navigation.");
    check(/^[0-9a-f]{32}$/.test(example.meta.indexVersion), "The representative indexVersion must preserve the emitted opaque GUID format.");
  } catch (error) {
    check(false, `The definition response example must be valid JSON after HTML entity decoding: ${error.message}`);
  }
}

const anatomySection = /<section\b[^>]*\bid="anatomy"[\s\S]*?<\/section>/i.exec(html)?.[0] ?? "";
check(/compile ownership comes from the index[\s\S]*bounded live source read supplies the declared receiver type/i.test(anatomySection),
  "The walkthrough must distinguish indexed compile ownership from live receiver-type evidence.");
check(!/same bytes|same immutable solution snapshot|live body\s*·\s*pinned snapshot/i.test(anatomySection),
  "The walkthrough must not collapse separately captured text and semantic evidence into one snapshot.");
check(/does not claim[\s\S]*full expression inference/i.test(anatomySection),
  "The walkthrough must distinguish its proof chain from unsupported full expression inference.");
check(/semantic_unavailable/i.test(anatomySection),
  "The walkthrough must disclose the honest semantic-unavailable boundary.");
check(/indexed ownership[\s\S]*text\s*·\s*live receiver context[\s\S]*exact\s*·\s*semantic call binding[\s\S]*separately read\s*·\s*live declaration body/i.test(understandingConclusion),
  "The agent conclusion must label every evidence hop separately.");

const proofCards = matches(/<article\b[^>]*class="[^"]*\bproof-card\b[^"]*"[^>]*>[\s\S]*?<\/article>/gi)
  .map((match) => match[0]);
const ordinaryProofCards = proofCards.filter((card) => !/\bproof-card--hero\b/i.test(card));
const portalCard = ordinaryProofCards.find((card) =>
  /<span\b[^>]*class="proof-card__label"[^>]*>\s*Operations portal\s*<\/span>/i.test(card)) ?? "";
const portalCardTag = /^<article\b[^>]*>/i.exec(portalCard)?.[0] ?? "";
check(ordinaryProofCards.length === 5, "The proof dashboard must contain five ordinary proof cards.");
check(Boolean(portalCard), "The proof dashboard must contain the Operations portal card.");
check(ordinaryProofCards.at(-1) === portalCard, "Operations portal must remain the fifth ordinary proof card.");
check(/\bproof-card--wide\b/i.test(portalCardTag), "The Operations portal card must declare its wide-card layout intent.");
check(/style="[^"]*grid-column:\s*1\s*\/\s*-1\s*;?[^"]*"/i.test(portalCardTag), "The Operations portal card must span the full grid at desktop and tablet widths.");
check(proofCards.filter((card) => /\bproof-card--wide\b/i.test(/^<article\b[^>]*>/i.exec(card)?.[0] ?? "")).length === 1,
  "Only the Operations portal card may use the wide-card grid rule.");

const robots = metaContent("name", "robots").toLowerCase().split(",").map((token) => token.trim()).filter(Boolean);
if (launchMode) {
  check(robots.includes("index") && robots.includes("follow") && !robots.includes("noindex"), "Launch mode requires an index,follow robots directive.");
  const canonicalTag = matches(/<link\b[^>]*>/gi).map((match) => match[0]).find((tag) => attribute(tag, "rel").toLowerCase() === "canonical");
  check(isAbsoluteHttps(canonicalTag ? attribute(canonicalTag, "href") : ""), "Launch mode requires an absolute HTTPS canonical URL.");
  check(isAbsoluteHttps(metaContent("property", "og:url")), "Launch mode requires an absolute HTTPS og:url.");
  check(isAbsoluteHttps(metaContent("property", "og:image")), "Launch mode requires an absolute HTTPS og:image.");
  check(existsSync(resolve(root, "sitemap.xml")), "Launch mode requires website/sitemap.xml.");
  const termsFiles = ["LICENSE", "LICENSE.md", "LICENSE.txt", "COPYING", "COPYING.md", "TERMS.md", "EULA.md"];
  check(termsFiles.some((name) => existsSync(resolve(repoRoot, name))), "Launch mode requires a root license or explicit use-terms file.");
} else {
  check(robots.includes("noindex") && robots.includes("nofollow"), "Prelaunch mode requires the noindex,nofollow guard.");
}

const stylesheet = stylesheets.length === 1 && stylesheets[0].exists ? readFileSync(stylesheets[0].path, "utf8") : "";
const script = scripts.length === 1 && scripts[0].exists ? readFileSync(scripts[0].path, "utf8") : "";
const readableFontSize = (value) => {
  const normalized = value.trim();
  const supported = /^\d+(?:\.\d+)?px$/i.test(normalized) ||
    /^clamp\(\s*\d+(?:\.\d+)?px\s*,\s*\d+(?:\.\d+)?(?:px|vw)\s*,\s*\d+(?:\.\d+)?px\s*\)$/i.test(normalized);
  const pixelSizes = matches(/\d+(?:\.\d+)?px/gi, normalized).map((match) => Number.parseFloat(match[0]));
  return supported && pixelSizes.length > 0 && pixelSizes.every((size) => size >= 12);
};
const fontSizeDeclarations = matches(/font-size:\s*([^;}{]+)\s*;/gi, stylesheet)
  .map((match) => match[1]);
check(fontSizeDeclarations.length > 0 && fontSizeDeclarations.every(readableFontSize),
  "Readable site text must use a supported font-size with no pixel bound below 12px.");
check([
  "11px",
  "calc(8px + 1vw)",
  "clamp(12px, calc(8px + 1vw), 14px)",
  "clamp(12px, 1vw, 11px)",
  "0.75rem",
].every((value) => !readableFontSize(value)),
  "The font-size guard must fail closed for sub-12px and unsupported computed values.");
check(stylesheet.includes(".no-js .atlas__controls") && stylesheet.includes(".no-js .config-tabs__list"), "CSS must hide dead atlas and tab controls without JavaScript.");
check(stylesheet.includes(".motion-paused *"), "CSS must pause continuous animation in the global motion-paused state.");
check(/function updatePauseButton\(\)[\s\S]*?setGlobalMotionPaused\(userPaused/.test(script), "The atlas pause state must invoke the global motion controller.");
check(/pauseButton\.addEventListener\("click"[\s\S]*?updatePauseButton\(\)/.test(script), "The pause button click handler must update the global pause state.");
check(/function init\(\)[\s\S]*?document\.documentElement\.classList\.replace\("no-js", "js"\);[\s\S]*?window\.__phoenixReady = true/.test(script), "Successful initialization must reveal JavaScript controls before setting the readiness marker.");
check(!/<script>[^<]*classList\.replace\("no-js", "js"\)/i.test(html), "Inline markup must not disable the fallback before the external script loads.");

if (failures.length) {
  console.error(`Website ${launchMode ? "launch" : "prelaunch"} verification failed (${failures.length}/${checks} checks):`);
  failures.forEach((failure) => console.error(`- ${failure}`));
  process.exit(1);
}

console.log(`Website ${launchMode ? "launch" : "prelaunch"} verification passed (${checks} checks).`);
