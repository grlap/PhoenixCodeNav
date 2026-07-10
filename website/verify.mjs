#!/usr/bin/env node

import { createHash } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import { dirname, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const root = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(root, "..");
const html = readFileSync(resolve(root, "index.html"), "utf8");
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
