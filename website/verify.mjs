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

function structuredDataDiagnostic(source) {
  let data;
  try {
    data = JSON.parse(source);
  } catch (error) {
    return `Structured data must be valid JSON: ${error.message}`;
  }
  if (typeof data !== "object" || data === null || Array.isArray(data)) {
    return "Structured data must be a JSON object.";
  }
  if (data.operatingSystem !== "Windows") {
    return "Structured data must match the Windows setup path documented by this page.";
  }
  return "";
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
  const diagnostic = structuredDataDiagnostic(structuredData[0][1]);
  check(diagnostic === "", diagnostic || "Structured data must be valid and match the documented shape.");
}
check(structuredDataDiagnostic("{").startsWith("Structured data must be valid JSON:"),
  "The JSON-LD verifier must classify syntax errors as invalid JSON.");
check(structuredDataDiagnostic("null") === "Structured data must be a JSON object.",
  "The JSON-LD verifier must classify valid non-object JSON as a shape error.");
check(structuredDataDiagnostic('{"operatingSystem":"Linux"}') ===
  "Structured data must match the Windows setup path documented by this page.",
  "The JSON-LD verifier must keep the Windows operating-system contract.");
check(structuredDataDiagnostic('{"operatingSystem":"Windows"}') === "",
  "The JSON-LD verifier must accept the documented Windows object.");

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
    check(/^\d+\.\d+\.\d+\+(?:[0-9a-f]{12}|unknown)$/.test(example.meta.build),
      "The representative build stamp must use Phoenix's standard twelve-character lowercase hexadecimal commit suffix or unknown.");
    check(example.meta.indexMode === "daemon",
      "The representative indexMode must show the shared-daemon runtime, the ordinary topology since the transparent daemon default.");
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

check(/one shared Phoenix daemon per workspace/i.test(html),
  "The trust boundary must state that attached agents share one Phoenix daemon per workspace.");
check(/joins one shared runtime[\s\S]*one index writer, one watcher, one warm semantic estate/i.test(html),
  "The operational signals must describe the shared-daemon runtime that agents join transparently.");
check(/share one Phoenix daemon per workspace[\s\S]*typed, actionable cause/i.test(html),
  "The FAQ must explain multi-agent daemon sharing and the typed startup-failure surface.");
check(!/\bread-only follower\b|\bwriter\/follower\b/i.test(html),
  "The site must not describe the retired writer/follower topology as the current runtime.");

const proofCards = matches(/<article\b[^>]*class="[^"]*\bproof-card\b[^"]*"[^>]*>[\s\S]*?<\/article>/gi)
  .map((match) => match[0]);
const ordinaryProofCards = proofCards.filter((card) => !/\bproof-card--hero\b/i.test(card));
const portalCard = ordinaryProofCards.find((card) =>
  /<span\b[^>]*class="proof-card__label"[^>]*>\s*Operations portal\s*<\/span>/i.test(card)) ?? "";
const sharedDaemonCard = ordinaryProofCards.find((card) =>
  /<span\b[^>]*class="proof-card__label"[^>]*>\s*Shared daemon\s*<\/span>/i.test(card)) ?? "";
const portalCardTag = /^<article\b[^>]*>/i.exec(portalCard)?.[0] ?? "";
const sharedDaemonCardTag = /^<article\b[^>]*>/i.exec(sharedDaemonCard)?.[0] ?? "";
check(ordinaryProofCards.length === 6, "The proof dashboard must contain six ordinary proof cards.");
check(Boolean(portalCard), "The proof dashboard must contain the Operations portal card.");
check(ordinaryProofCards.at(-1) === portalCard, "Operations portal must remain the last ordinary proof card.");
check(Boolean(sharedDaemonCard), "The proof dashboard must contain the Shared daemon card.");
check(/\bproof-card--shared\b/i.test(sharedDaemonCardTag),
  "The Shared daemon card must use its responsive stylesheet class.");
check(!/\bstyle=/i.test(sharedDaemonCardTag),
  "The Shared daemon card must not bypass responsive grid rules with an inline style.");
check(/\bproof-card--wide\b/i.test(portalCardTag), "The Operations portal card must declare its wide-card layout intent.");
check(/style="[^"]*grid-column:\s*1\s*\/\s*-1\s*;?[^"]*"/i.test(portalCardTag), "The Operations portal card must span the full grid at desktop and tablet widths.");
check(/early-access/i.test(portalCard), "The Operations portal card must disclose its early-access status.");
check(/up to eight configured workspaces/i.test(portalCard),
  "The Operations portal card must disclose its configured-workspace bound.");
check(!/every local instance/i.test(portalCard),
  "The Operations portal card must not claim unbounded local-instance discovery.");
check(/href="https:\/\/github\.com\/grlap\/PhoenixCodeNav\/blob\/main\/src\/CodeNav\.Portal\/README\.md"/i.test(portalCard),
  "The Operations portal card must link to its setup guide.");
check(proofCards.filter((card) => /\bproof-card--wide\b/i.test(/^<article\b[^>]*>/i.exec(card)?.[0] ?? "")).length === 1,
  "Only the Operations portal card may use the wide-card grid rule.");

const setupSection = /<section\b[^>]*\bid="setup"[\s\S]*?<\/section>/i.exec(html)?.[0] ?? "";
check(/complete self-contained[\s\S]*win-x64[\s\S]*FSharp\.Core\.dll[\s\S]*portal\//i.test(setupSection),
  "The setup must tell users to keep the complete win-x64 output including FSharp.Core.dll and portal/.");
check(/Copy the complete output[\s\S]*artifacts\/win-x64\/[\s\S]*portal\//i.test(setupSection),
  "The setup summary must direct users to copy the complete publish directory.");

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
check(/\.proof-dashboard\s+\.proof-card\.proof-card--shared\s*\{[^}]*grid-column:\s*1\s*\/\s*-1/i.test(stylesheet),
  "The Shared daemon card must span the proof dashboard without bypassing responsive CSS.");
check(/\.setup-step__copy\s*\{[^}]*min-width:\s*0/i.test(stylesheet) &&
  /\.setup-step__copy\s+code\s*\{[^}]*overflow-wrap:\s*anywhere/i.test(stylesheet),
"The larger setup copy must not let long inline identifiers widen the mobile layout.");
check(/@media\s*\(min-width:\s*1021px\)[\s\S]*?\.nav__links a,\s*\.nav__github\s*\{[^}]*font-size:\s*17px/i.test(stylesheet) &&
  /@media\s*\(min-width:\s*1021px\)[\s\S]*?\.hero__lead\s*\{[^}]*font-size:\s*clamp\(24px,\s*1\.8vw,\s*29px\)/i.test(stylesheet) &&
  /@media\s*\(min-width:\s*1021px\)[\s\S]*?\.hero__plain-language p\s*\{[^}]*font-size:\s*19px/i.test(stylesheet) &&
  /@media\s*\(min-width:\s*1021px\)[\s\S]*?\.trust-rail__inner b\s*\{[^}]*font-size:\s*18px/i.test(stylesheet) &&
  /@media\s*\(min-width:\s*1021px\)[\s\S]*?\.question-card p,[\s\S]*?\.faq details p\s*\{[^}]*font-size:\s*20px/i.test(stylesheet),
"The desktop breakpoint must retain the larger navigation, hero, feature-rail, and supporting-content type scale.");
const readableFontSize = (value) => {
  const normalized = value.trim();
  const supported = /^\d+(?:\.\d+)?px$/i.test(normalized) ||
    /^clamp\(\s*\d+(?:\.\d+)?px\s*,\s*\d+(?:\.\d+)?(?:px|vw)\s*,\s*\d+(?:\.\d+)?px\s*\)$/i.test(normalized);
  const pixelSizes = matches(/\d+(?:\.\d+)?px/gi, normalized).map((match) => Number.parseFloat(match[0]));
  return supported && pixelSizes.length > 0 && pixelSizes.every((size) => size >= 12);
};
const isCssWhitespace = (character) => character === " " || character === "\t" ||
  character === "\n" || character === "\r" || character === "\f";
const isCssHexDigit = (character) => character !== undefined && /^[0-9a-f]$/i.test(character);
const isCssNameCharacter = (character) => character !== undefined &&
  (/[a-z0-9_-]/i.test(character) || character.codePointAt(0) >= 0x80);

function skipCssComment(value, start) {
  const end = value.indexOf("*/", start + 2);
  return end < 0 ? { index: value.length, complete: false } : { index: end + 2, complete: true };
}

function skipCssString(value, start) {
  const quote = value[start];
  let index = start + 1;
  while (index < value.length) {
    const character = value[index];
    if (character === quote) return { index: index + 1, complete: true };
    if (character === "\n" || character === "\r" || character === "\f") {
      return { index, complete: false };
    }
    if (character === "\\") {
      index += 1;
      if (index >= value.length) return { index, complete: false };
      if (value[index] === "\r" && value[index + 1] === "\n") index += 2;
      else index += 1;
      continue;
    }
    index += 1;
  }
  return { index, complete: false };
}

function readCssEscape(value, start) {
  let index = start + 1;
  if (index >= value.length || value[index] === "\n" || value[index] === "\r" || value[index] === "\f") {
    return { index, decoded: "", complete: false };
  }
  if (!isCssHexDigit(value[index])) {
    const decoded = value[index];
    return { index: index + 1, decoded, complete: true };
  }
  let hex = "";
  while (index < value.length && hex.length < 6 && isCssHexDigit(value[index])) {
    hex += value[index];
    index += 1;
  }
  if (isCssWhitespace(value[index])) {
    if (value[index] === "\r" && value[index + 1] === "\n") index += 2;
    else index += 1;
  }
  const codePoint = Number.parseInt(hex, 16);
  const decoded = codePoint === 0 || codePoint > 0x10ffff || (codePoint >= 0xd800 && codePoint <= 0xdfff)
    ? "\ufffd"
    : String.fromCodePoint(codePoint);
  return { index, decoded, complete: true };
}

function readCssIdentifier(value, start) {
  let index = start;
  let decoded = "";
  while (index < value.length) {
    if (isCssNameCharacter(value[index])) {
      decoded += value[index];
      index += 1;
      continue;
    }
    if (value[index] !== "\\") break;
    const escaped = readCssEscape(value, index);
    if (!escaped.complete) return { index: escaped.index, decoded, complete: false };
    decoded += escaped.decoded;
    index = escaped.index;
  }
  return { index, decoded, complete: decoded.length > 0 };
}

function skipCssTrivia(value, start) {
  let index = start;
  while (index < value.length) {
    if (isCssWhitespace(value[index])) {
      index += 1;
      continue;
    }
    if (value[index] === "/" && value[index + 1] === "*") {
      const comment = skipCssComment(value, index);
      if (!comment.complete) return comment;
      index = comment.index;
      continue;
    }
    break;
  }
  return { index, complete: true };
}

function skipUnquotedCssUrl(value, start) {
  let index = start;
  while (index < value.length) {
    const character = value[index];
    if (character === ")") return { index: index + 1, complete: true };
    if (isCssWhitespace(character)) {
      while (isCssWhitespace(value[index])) index += 1;
      return value[index] === ")"
        ? { index: index + 1, complete: true }
        : { index, complete: false };
    }
    if (character === "\\") {
      const escaped = readCssEscape(value, index);
      if (!escaped.complete) return { index: escaped.index, complete: false };
      index = escaped.index;
      continue;
    }
    if (character === "\"" || character === "'" || character === "(" ||
        character === "\n" || character === "\r" || character === "\f") {
      return { index, complete: false };
    }
    index += 1;
  }
  return { index, complete: false };
}

function scanCssItem(value, start) {
  let index = start;
  let parentheses = 0;
  let brackets = 0;
  while (index < value.length) {
    const character = value[index];
    if (character === "/" && value[index + 1] === "*") {
      const comment = skipCssComment(value, index);
      if (!comment.complete) return { index: comment.index, terminator: "", complete: false };
      index = comment.index;
      continue;
    }
    if (character === "\"" || character === "'") {
      const string = skipCssString(value, index);
      if (!string.complete) return { index: string.index, terminator: "", complete: false };
      index = string.index;
      continue;
    }
    if (isCssNameCharacter(character) || character === "\\") {
      const identifier = readCssIdentifier(value, index);
      if (!identifier.complete) return { index: identifier.index, terminator: "", complete: false };
      if (identifier.decoded.toLowerCase() === "url" && value[identifier.index] === "(") {
        let contentStart = identifier.index + 1;
        while (isCssWhitespace(value[contentStart])) contentStart += 1;
        if (value[contentStart] !== "\"" && value[contentStart] !== "'") {
          const url = skipUnquotedCssUrl(value, contentStart);
          if (!url.complete) return { index: url.index, terminator: "", complete: false };
          index = url.index;
          continue;
        }
      }
      index = identifier.index;
      continue;
    }
    if (character === "(") parentheses += 1;
    else if (character === ")") {
      if (parentheses === 0) return { index, terminator: "", complete: false };
      parentheses -= 1;
    } else if (character === "[") brackets += 1;
    else if (character === "]") {
      if (brackets === 0) return { index, terminator: "", complete: false };
      brackets -= 1;
    } else if (parentheses === 0 && brackets === 0 && (character === ";" || character === "{" || character === "}")) {
      return { index, terminator: character, complete: true };
    }
    index += 1;
  }
  return { index, terminator: "", complete: parentheses === 0 && brackets === 0 };
}

function inspectFontSizeDeclarations(value) {
  let index = 0;
  let depth = 0;
  let atItemStart = false;
  let lexicallyComplete = true;
  let discovered = 0;
  const values = [];

  while (index < value.length) {
    const trivia = skipCssTrivia(value, index);
    if (!trivia.complete) {
      lexicallyComplete = false;
      break;
    }
    index = trivia.index;
    if (index >= value.length) break;

    if (depth > 0 && value[index] === "}") {
      depth -= 1;
      index += 1;
      atItemStart = depth > 0;
      continue;
    }
    if (depth > 0 && value[index] === ";") {
      index += 1;
      atItemStart = true;
      continue;
    }

    let property = null;
    let valueStart = index;
    if (depth > 0 && atItemStart && (isCssNameCharacter(value[index]) || value[index] === "\\")) {
      const identifier = readCssIdentifier(value, index);
      if (!identifier.complete) {
        lexicallyComplete = false;
        break;
      }
      const afterIdentifier = skipCssTrivia(value, identifier.index);
      if (!afterIdentifier.complete) {
        lexicallyComplete = false;
        break;
      }
      if (value[afterIdentifier.index] === ":") {
        property = identifier.decoded.toLowerCase();
        const afterColon = skipCssTrivia(value, afterIdentifier.index + 1);
        if (!afterColon.complete) {
          if (property === "font-size") discovered += 1;
          lexicallyComplete = false;
          break;
        }
        valueStart = afterColon.index;
      }
    }

    const item = scanCssItem(value, valueStart);
    if (!item.complete || !item.terminator) {
      if (property === "font-size") discovered += 1;
      lexicallyComplete = false;
      break;
    }
    if (item.terminator === "{") {
      depth += 1;
      index = item.index + 1;
      atItemStart = true;
      continue;
    }
    if (property === "font-size") {
      discovered += 1;
      values.push(value.slice(valueStart, item.index).trim());
    }
    if (item.terminator === ";") {
      index = item.index + 1;
      atItemStart = depth > 0;
      continue;
    }
    if (depth === 0) {
      lexicallyComplete = false;
      break;
    }
    depth -= 1;
    index = item.index + 1;
    atItemStart = depth > 0;
  }

  return { lexicallyComplete: lexicallyComplete && depth === 0, discovered, values };
}

const fontSizeInspection = inspectFontSizeDeclarations(stylesheet);
const fontSizeDeclarations = fontSizeInspection.values;
check(fontSizeInspection.lexicallyComplete,
  "The stylesheet must not contain an unterminated comment or string.");
check(fontSizeInspection.discovered === fontSizeDeclarations.length,
  "Every discovered font-size declaration must be parsed and validated.");
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
check([
  ".good{font-size:12px;}.bad{font-size : 8px;}",
  ".good{font-size:12px;}.bad{font-size:8px}",
  String.raw`.good{font-size:12px}.bad{font-\73ize:8px}`,
  ".good{font-size:12px}.outer{.inner{color:red}font-size:8px}",
].every((fixture) => {
  const inspection = inspectFontSizeDeclarations(fixture);
  return inspection.lexicallyComplete && inspection.discovered === 2 &&
    inspection.values.length === inspection.discovered &&
    !inspection.values.every(readableFontSize);
}), "The font-size declaration parser must reject whitespace, closing-brace, escaped-name, and nested-rule bypasses.");
check([
  "/*font-size:8px*/.a{font-size:12px;}",
  '.a{content:";}/*";font-size:12px;}',
  ".a{background:url(a/*b);font-size:12px;}",
].every((fixture) => {
  const inspection = inspectFontSizeDeclarations(fixture);
  return inspection.lexicallyComplete && inspection.discovered === 1 &&
    inspection.values.length === 1 && readableFontSize(inspection.values[0]);
}), "The font-size declaration parser must ignore comments, strings, and unquoted URL token contents.");
check([
  ".a{/*",
  '.a{content:"unterminated',
  '.a{content:"raw newline\nclosed later";font-size:12px;}',
].every((fixture) => !inspectFontSizeDeclarations(fixture).lexicallyComplete),
"The font-size declaration parser must fail closed on unterminated comments and strings.");
const malformedFontSizeFixture = inspectFontSizeDeclarations(`{font-size:${" ".repeat(32768)}`);
check(!malformedFontSizeFixture.lexicallyComplete && malformedFontSizeFixture.discovered === 1 &&
  malformedFontSizeFixture.values.length === 0,
"The font-size declaration parser must scan large malformed values deterministically and fail closed.");
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
