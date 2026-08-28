#!/usr/bin/env node

import { createHash } from "node:crypto";
import { existsSync, readFileSync, realpathSync, statSync } from "node:fs";
import { dirname, isAbsolute, posix, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const root = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(root, "..");
const html = readFileSync(resolve(root, "index.html"), "utf8");
const readme = readFileSync(resolve(root, "README.md"), "utf8");
const gitAttributesPath = resolve(repoRoot, ".gitattributes");
const gitAttributesExists = existsSync(gitAttributesPath);
const gitAttributes = gitAttributesExists ? readFileSync(gitAttributesPath, "utf8") : "";
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

function decodeIndexVarint(index, offset) {
  if (offset >= index.length) throw new Error("Git index path varint is truncated.");
  let byte = index[offset++];
  let value = byte & 0x7f;
  while ((byte & 0x80) !== 0) {
    if (offset >= index.length || value > 0x00ff_ffff)
      throw new Error("Git index path varint is malformed.");
    value += 1;
    byte = index[offset++];
    value = (value << 7) + (byte & 0x7f);
  }
  return { value, offset };
}

function gitIndexChecksum(index, objectIdBytes) {
  if (objectIdBytes !== 20 && objectIdBytes !== 32)
    throw new Error(`Git index object id length ${objectIdBytes} is unsupported.`);
  if (!Buffer.isBuffer(index) || index.length < 12 + objectIdBytes)
    throw new Error("Git index is too short to contain its checksum.");
  const algorithm = objectIdBytes === 32 ? "sha256" : "sha1";
  return createHash(algorithm).update(index.subarray(0, -objectIdBytes)).digest();
}

function parseGitIndex(index, objectIdBytes) {
  if (!Buffer.isBuffer(index) || index.length < 12 ||
      index.subarray(0, 4).toString("ascii") !== "DIRC") {
    throw new Error("Git index signature is missing.");
  }
  const checksum = gitIndexChecksum(index, objectIdBytes);
  const storedChecksum = index.subarray(-objectIdBytes);
  if (!checksum.equals(storedChecksum)) throw new Error("Git index checksum is invalid.");
  const version = index.readUInt32BE(4);
  if (version < 2 || version > 4) throw new Error(`Git index v${version} is unsupported.`);
  const entryCount = index.readUInt32BE(8);
  if (entryCount > Math.floor(index.length / (42 + objectIdBytes)))
    throw new Error("Git index entry count exceeds the file bound.");

  const entries = [];
  let offset = 12;
  let previousPath = Buffer.alloc(0);
  for (let entry = 0; entry < entryCount; entry += 1) {
    const entryStart = offset;
    const fixedBytes = 40 + objectIdBytes + 2;
    if (offset + fixedBytes > index.length) throw new Error("Git index entry is truncated.");
    offset += 40 + objectIdBytes;
    const flags = index.readUInt16BE(offset);
    offset += 2;
    if ((flags & 0x4000) !== 0) {
      if (offset + 2 > index.length) throw new Error("Extended Git index flags are truncated.");
      offset += 2;
    }

    let pathBytes;
    if (version === 4) {
      const decoded = decodeIndexVarint(index, offset);
      offset = decoded.offset;
      if (decoded.value > previousPath.length)
        throw new Error("Git index path compression exceeds the previous path.");
      const nul = index.indexOf(0, offset);
      if (nul < 0) throw new Error("Git index path is unterminated.");
      pathBytes = Buffer.concat([
        previousPath.subarray(0, previousPath.length - decoded.value),
        index.subarray(offset, nul),
      ]);
      offset = nul + 1;
      previousPath = pathBytes;
    } else {
      const nul = index.indexOf(0, offset);
      if (nul < 0) throw new Error("Git index path is unterminated.");
      pathBytes = index.subarray(offset, nul);
      offset = nul + 1;
      const consumed = offset - entryStart;
      offset += (8 - (consumed % 8)) % 8;
    }
    if (offset > index.length) throw new Error("Git index path entry is malformed.");
    entries.push(pathBytes.toString("utf8"));
  }

  const checksumOffset = index.length - objectIdBytes;
  if (offset > checksumOffset) throw new Error("Git index entries overlap the checksum.");
  const extensions = new Map();
  while (offset < checksumOffset) {
    if (offset + 8 > checksumOffset) throw new Error("Git index extension header is truncated.");
    const signature = index.subarray(offset, offset + 4).toString("ascii");
    const size = index.readUInt32BE(offset + 4);
    offset += 8;
    if (size > checksumOffset - offset) throw new Error(`Git index ${signature} extension is truncated.`);
    if (extensions.has(signature)) throw new Error(`Git index ${signature} extension is duplicated.`);
    extensions.set(signature, index.subarray(offset, offset + size));
    offset += size;
  }
  for (const signature of extensions.keys()) {
    if (/^[a-z]/.test(signature) && signature !== "link")
      throw new Error(`Required Git index extension ${signature} is unsupported.`);
  }
  if (entries.some((path) => path.length === 0) && !extensions.has("link"))
    throw new Error("Ordinary Git index entries must have non-empty paths.");
  return { entries, extensions, checksum };
}

function parseEwahBitmap(data, offset, maximumBits) {
  if (offset + 8 > data.length) throw new Error("Split-index EWAH header is truncated.");
  const bitSize = data.readUInt32BE(offset);
  const wordCount = data.readUInt32BE(offset + 4);
  offset += 8;
  if (bitSize > maximumBits) throw new Error("Split-index EWAH bitmap exceeds the shared index.");
  const wordsBytes = wordCount * 8;
  if (!Number.isSafeInteger(wordsBytes) || offset + wordsBytes + 4 > data.length)
    throw new Error("Split-index EWAH words are truncated.");

  const bits = new Set();
  let wordIndex = 0;
  let outputBit = 0;
  while (wordIndex < wordCount) {
    const rlw = data.readBigUInt64BE(offset + wordIndex * 8);
    wordIndex += 1;
    const runBit = (rlw & 1n) !== 0n;
    const runLength = Number((rlw >> 1n) & 0xffff_ffffn);
    const literalCount = Number(rlw >> 33n);
    if (literalCount > wordCount - wordIndex)
      throw new Error("Split-index EWAH literal count exceeds its word array.");
    const runBits = runLength * 64;
    if (runLength > Math.ceil(Math.max(0, bitSize - outputBit) / 64))
      throw new Error("Split-index EWAH run exceeds its bit size.");
    if (runBit) {
      const end = Math.min(bitSize, outputBit + runBits);
      for (let bit = outputBit; bit < end; bit += 1) bits.add(bit);
    }
    outputBit += runBits;

    if (literalCount > Math.ceil(Math.max(0, bitSize - outputBit) / 64))
      throw new Error("Split-index EWAH literals exceed its bit size.");
    for (let literal = 0; literal < literalCount; literal += 1) {
      const word = data.readBigUInt64BE(offset + wordIndex * 8);
      wordIndex += 1;
      const availableBits = Math.min(64, Math.max(0, bitSize - outputBit));
      for (let bit = 0; bit < availableBits; bit += 1) {
        if ((word & (1n << BigInt(bit))) !== 0n) bits.add(outputBit + bit);
      }
      outputBit += 64;
    }
  }
  if (outputBit < bitSize) throw new Error("Split-index EWAH bitmap is shorter than its bit size.");
  offset += wordsBytes;
  const currentRlw = data.readUInt32BE(offset);
  if ((wordCount === 0 && currentRlw !== 0) || (wordCount > 0 && currentRlw >= wordCount))
    throw new Error("Split-index EWAH current-RLW position is invalid.");
  return { bits, offset: offset + 4 };
}

function splitIndexHash(link, objectIdBytes) {
  if (link.length < objectIdBytes) throw new Error("Split-index link hash is truncated.");
  return link.subarray(0, objectIdBytes);
}

function mergeSplitIndex(overlay, shared, link, objectIdBytes) {
  if (shared.extensions.has("link")) throw new Error("Nested split Git indexes are unsupported.");
  const hash = splitIndexHash(link, objectIdBytes);
  if (hash.some((byte) => byte !== 0) &&
      (!Buffer.isBuffer(shared.checksum) || !hash.equals(shared.checksum))) {
    throw new Error("Split Git index base checksum does not match its link hash.");
  }
  let offset = objectIdBytes;
  const deleted = parseEwahBitmap(link, offset, shared.entries.length);
  offset = deleted.offset;
  const replaced = parseEwahBitmap(link, offset, shared.entries.length);
  offset = replaced.offset;
  if (offset !== link.length) throw new Error("Split-index link extension has trailing data.");
  for (const position of deleted.bits) {
    if (replaced.bits.has(position))
      throw new Error("Split-index entry cannot be both deleted and replaced.");
  }
  if (replaced.bits.size > overlay.entries.length)
    throw new Error("Split-index replacement bitmap exceeds overlay entries.");

  const paths = new Set();
  let replacement = 0;
  for (let position = 0; position < shared.entries.length; position += 1) {
    let path = shared.entries[position];
    if (replaced.bits.has(position)) {
      const overlayPath = overlay.entries[replacement++];
      if (overlayPath) path = overlayPath;
    }
    if (!deleted.bits.has(position)) paths.add(path);
  }
  for (; replacement < overlay.entries.length; replacement += 1) {
    const path = overlay.entries[replacement];
    if (!path) throw new Error("Split-index added entry has an empty path.");
    paths.add(path);
  }
  return { hash, paths };
}

function resolveGitIndex() {
  const marker = resolve(repoRoot, ".git");
  if (!existsSync(marker)) throw new Error("Repository .git metadata is missing.");
  const markerStat = statSync(marker);
  let gitDirectory;
  if (markerStat.isDirectory()) {
    gitDirectory = realpathSync(marker);
  } else if (markerStat.isFile() && markerStat.size <= 4 * 1024) {
    const match = /^gitdir:\s*(.+)\s*$/i.exec(readFileSync(marker, "utf8"));
    if (!match) throw new Error("Repository .git file is malformed.");
    gitDirectory = realpathSync(isAbsolute(match[1])
      ? match[1]
      : resolve(dirname(marker), match[1]));
  } else {
    throw new Error("Repository .git metadata is not a directory or bounded gitdir file.");
  }

  let commonDirectory = gitDirectory;
  const commonMarker = resolve(gitDirectory, "commondir");
  if (existsSync(commonMarker) && statSync(commonMarker).isFile() &&
      statSync(commonMarker).size <= 4 * 1024) {
    const value = readFileSync(commonMarker, "utf8").trim();
    if (!value) throw new Error("Git commondir file is empty.");
    commonDirectory = realpathSync(isAbsolute(value) ? value : resolve(gitDirectory, value));
  }
  return { indexPath: resolve(gitDirectory, "index"), commonDirectory };
}

function readTrackedPaths() {
  try {
    const { indexPath, commonDirectory } = resolveGitIndex();
    const indexStat = statSync(indexPath);
    if (!indexStat.isFile() || indexStat.size > 256 * 1024 * 1024)
      throw new Error("Git index is missing or exceeds the 256 MiB verifier bound.");
    let objectIdBytes = 20;
    const configPath = resolve(commonDirectory, "config");
    if (existsSync(configPath)) {
      const configStat = statSync(configPath);
      if (!configStat.isFile() || configStat.size > 1024 * 1024)
        throw new Error("Git config is not a bounded regular file.");
      if (/^\s*objectFormat\s*=\s*sha256\s*$/im.test(readFileSync(configPath, "utf8")))
        objectIdBytes = 32;
    }
    const index = parseGitIndex(readFileSync(indexPath), objectIdBytes);
    const link = index.extensions.get("link");
    if (!link) return { paths: new Set(index.entries), failure: "" };

    const hash = splitIndexHash(link, objectIdBytes);
    let shared = { entries: [], extensions: new Map() };
    if (hash.some((byte) => byte !== 0)) {
      const sharedPath = resolve(dirname(indexPath), `sharedindex.${hash.toString("hex")}`);
      const sharedStat = statSync(sharedPath);
      if (!sharedStat.isFile() || sharedStat.size > 256 * 1024 * 1024)
        throw new Error("Split Git index base is missing or exceeds the 256 MiB verifier bound.");
      shared = parseGitIndex(readFileSync(sharedPath), objectIdBytes);
    }
    return { paths: mergeSplitIndex(index, shared, link, objectIdBytes).paths, failure: "" };
  } catch (error) {
    const diagnostic = error instanceof Error ? error.message : String(error);
    return { paths: new Set(), failure: `Git index tracking check failed: ${diagnostic}` };
  }
}

const trackedPaths = readTrackedPaths();

function trackedFileStatus(relativePath) {
  if (trackedPaths.failure) return { tracked: false, failure: trackedPaths.failure };
  return {
    tracked: trackedPaths.paths.has(relativePath.replaceAll("\\", "/")),
    failure: "",
  };
}

function checkTracked(relativePath, message) {
  const status = trackedFileStatus(relativePath);
  if (status.failure) {
    check(false, status.failure);
    return;
  }
  check(status.tracked, message);
}

function pathStaysInside(parent, candidate, pathApi = { relative, isAbsolute, sep }) {
  const fromParent = pathApi.relative(parent, candidate);
  return fromParent === "" || (!pathApi.isAbsolute(fromParent) && fromParent !== ".." &&
    !fromParent.startsWith(`..${pathApi.sep}`));
}

function documentedAssetReferences(source) {
  return [...source.matchAll(/`((?:styles\.css)|(?:script\.js))`/gi)]
    .map((match) => match[1])
    .sort();
}

function sameAssetInventory(left, right) {
  const sortedLeft = [...left].sort();
  const sortedRight = [...right].sort();
  return sortedLeft.length === sortedRight.length &&
    sortedLeft.every((asset, index) => asset === sortedRight[index]);
}

function reducedMotionBlocks(source) {
  const blocks = [];
  for (const media of source.matchAll(/@media\s*\(prefers-reduced-motion:\s*reduce\)\s*\{/ig)) {
    const openingBrace = media.index + media[0].lastIndexOf("{");
    let depth = 0;
    let closingBrace = -1;
    for (let index = openingBrace; index < source.length; index += 1) {
      if (source[index] === "{") depth += 1;
      if (source[index] === "}") depth -= 1;
      if (depth === 0) {
        closingBrace = index;
        break;
      }
    }
    if (closingBrace < 0) return null;
    blocks.push(source.slice(openingBrace + 1, closingBrace));
  }
  return blocks;
}

function selectorTargetsId(selector, id) {
  let quote = "";
  let bracketDepth = 0;
  let escaped = false;
  for (let index = 0; index < selector.length; index += 1) {
    const character = selector[index];
    if (escaped) {
      escaped = false;
      continue;
    }
    if (character === "\\") {
      escaped = true;
      continue;
    }
    if (quote) {
      if (character === quote) quote = "";
      continue;
    }
    if (character === "\"" || character === "'") {
      quote = character;
      continue;
    }
    if (character === "[") {
      bracketDepth += 1;
      continue;
    }
    if (character === "]" && bracketDepth > 0) {
      bracketDepth -= 1;
      continue;
    }
    if (bracketDepth !== 0 || character !== "#") continue;
    if (!selector.startsWith(id, index + 1)) continue;
    const following = selector[index + id.length + 1] ?? "";
    if (!/[A-Za-z0-9_-]/.test(following)) return true;
  }
  return false;
}

function reducedMotionKeepsAtlasVisible(source) {
  const blocks = reducedMotionBlocks(source);
  if (!blocks || blocks.length === 0) return false;
  // Fail closed on any rule that can hide the canvas. A global rule participates in the
  // reduced-motion cascade too, and a later reduced-motion block can override an earlier one.
  return !matches(/([^{}]+)\{([^{}]*)\}/g, source).some((rule) => {
    const selectors = rule[1].split(",").map((selector) => selector.trim());
    if (!selectors.some((selector) => selectorTargetsId(selector, "atlas-canvas"))) return false;
    return /(?:^|;)\s*(?:display\s*:\s*none|visibility\s*:\s*hidden|opacity\s*:\s*0(?:\.0*)?)\s*(?:!important\s*)?(?:;|$)/i
      .test(rule[2]);
  });
}

function metaContents(attributeName, attributeValue) {
  return matches(/<meta\b[^>]*>/gi).map((match) => match[0])
    .filter((candidate) => attribute(candidate, attributeName).toLowerCase() === attributeValue.toLowerCase())
    .map((tag) => attribute(tag, "content"));
}

function metaContent(attributeName, attributeValue) {
  return metaContents(attributeName, attributeValue)[0] ?? "";
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

function hasExactRobotsPolicy(robotsTokens, expectedTokens) {
  return robotsTokens.length === expectedTokens.length &&
    new Set(robotsTokens).size === robotsTokens.length &&
    expectedTokens.every((token) => robotsTokens.includes(token));
}

function selectsLaunchMode(argumentsList, robotsTokens) {
  if (argumentsList.includes("--launch")) return true;
  return argumentsList.includes("--auto") &&
    hasExactRobotsPolicy(robotsTokens, ["index", "follow"]);
}

function robotsTokensForContents(contents) {
  if (contents.length !== 1) return [];
  return contents[0].toLowerCase().split(",").map((token) => token.trim()).filter(Boolean);
}

const args = process.argv.slice(2);
const robotsContents = metaContents("name", "robots");
const modeRobots = robotsTokensForContents(robotsContents);
const launchMode = selectsLaunchMode(args, modeRobots);
check(args.length <= 1 && args.every((argument) => argument === "--launch" || argument === "--auto"),
  "Only one optional --launch or --auto argument is supported.");
check(robotsContents.length === 1, "The page must contain exactly one robots meta tag.");
check(robotsTokensForContents(["index,follow", "noindex,nofollow"]).length === 0,
  "Robots parsing must reject multiple robots meta tags instead of selecting the first.");
check(selectsLaunchMode(["--auto"], ["index", "follow"]),
  "Automatic verification mode must select launch checks for index,follow.");
check(!selectsLaunchMode(["--auto"], ["noindex", "nofollow"]),
  "Automatic verification mode must select prelaunch checks for noindex,nofollow.");
check(!selectsLaunchMode(["--auto"], ["index", "follow", "nofollow"]),
  "Automatic verification mode must reject a launch policy containing nofollow.");
check(!selectsLaunchMode(["--auto"], ["index", "follow", "none"]),
  "Automatic verification mode must reject a launch policy containing none.");
check(!selectsLaunchMode(["--auto"], ["index", "follow", "follow"]),
  "Automatic verification mode must reject duplicate launch-policy tokens.");

function finishGitIndexFixture(content, objectIdBytes = 20) {
  const algorithm = objectIdBytes === 32 ? "sha256" : "sha1";
  return Buffer.concat([content, createHash(algorithm).update(content).digest()]);
}

function makeV2IndexFixture(paths, extensions = [], objectIdBytes = 20) {
  const header = Buffer.alloc(12);
  header.write("DIRC", 0, "ascii");
  header.writeUInt32BE(2, 4);
  header.writeUInt32BE(paths.length, 8);
  const entries = paths.map((path) => {
    const pathBytes = Buffer.from(path, "utf8");
    const fixed = Buffer.alloc(40 + objectIdBytes + 2);
    fixed.writeUInt16BE(Math.min(pathBytes.length, 0x0fff), 40 + objectIdBytes);
    const unpadded = Buffer.concat([fixed, pathBytes, Buffer.alloc(1)]);
    return Buffer.concat([unpadded, Buffer.alloc((8 - (unpadded.length % 8)) % 8)]);
  });
  const encodedExtensions = extensions.map(({ signature, data }) => {
    const extensionHeader = Buffer.alloc(8);
    extensionHeader.write(signature, 0, "ascii");
    extensionHeader.writeUInt32BE(data.length, 4);
    return Buffer.concat([extensionHeader, data]);
  });
  return finishGitIndexFixture(Buffer.concat([header, ...entries, ...encodedExtensions]),
    objectIdBytes);
}

function makeV4IndexFixture() {
  const header = Buffer.alloc(12);
  header.write("DIRC", 0, "ascii");
  header.writeUInt32BE(4, 4);
  header.writeUInt32BE(2, 8);
  const fixedA = Buffer.alloc(62);
  const fixedB = Buffer.alloc(62);
  const first = Buffer.from("website/a.js", "utf8");
  const secondSuffix = Buffer.from("b.js", "utf8");
  fixedA.writeUInt16BE(first.length, 60);
  fixedB.writeUInt16BE("website/b.js".length, 60);
  return finishGitIndexFixture(Buffer.concat([
    header,
    fixedA, Buffer.from([0]), first, Buffer.alloc(1),
    fixedB, Buffer.from([4]), secondSuffix, Buffer.alloc(1),
  ]));
}

function makeEwahFixture(bitSize, setBits) {
  const literalCount = Math.ceil(bitSize / 64);
  const words = [BigInt(literalCount) << 33n];
  for (let wordIndex = 0; wordIndex < literalCount; wordIndex += 1) {
    let word = 0n;
    for (const bit of setBits) {
      if (Math.floor(bit / 64) === wordIndex) word |= 1n << BigInt(bit % 64);
    }
    words.push(word);
  }
  const result = Buffer.alloc(8 + words.length * 8 + 4);
  result.writeUInt32BE(bitSize, 0);
  result.writeUInt32BE(words.length, 4);
  words.forEach((word, index) => result.writeBigUInt64BE(word, 8 + index * 8));
  result.writeUInt32BE(0, 8 + words.length * 8);
  return result;
}

function makeSplitLinkFixture(hash, sharedCount, deleted, replaced) {
  return Buffer.concat([
    hash,
    makeEwahFixture(sharedCount, deleted),
    makeEwahFixture(sharedCount, replaced),
  ]);
}

check(parseGitIndex(makeV2IndexFixture(["README.md", "website/script.js"]), 20).entries
  .join("\0") === ["README.md", "website/script.js"].join("\0"),
"The Git index parser must read ordinary v2 staged paths.");
check(parseGitIndex(makeV2IndexFixture(["website/script.js"], [], 32), 32).entries[0] ===
  "website/script.js",
"The Git index parser must validate and read SHA-256 index checksums.");
check(parseGitIndex(makeV4IndexFixture(), 20).entries.join("\0") ===
  ["website/a.js", "website/b.js"].join("\0"),
"The Git index parser must reconstruct v4 prefix-compressed paths.");
const splitShared = parseGitIndex(makeV2IndexFixture([
  "README.md", "website/old.js", "website/styles.css",
]), 20);
const splitHash = splitShared.checksum;
const splitLink = makeSplitLinkFixture(splitHash, splitShared.entries.length,
  [2], [1]);
const splitOverlay = parseGitIndex(makeV2IndexFixture(["", "website/new.js"], [
  { signature: "link", data: splitLink },
]), 20);
check([...mergeSplitIndex(splitOverlay, splitShared, splitLink, 20).paths].sort().join("\0") ===
  ["README.md", "website/new.js", "website/old.js"].join("\0"),
"The Git index parser must merge split-index replacement, deletion, and addition entries.");
const tamperedIndex = makeV2IndexFixture(["website/script.js"]);
tamperedIndex[20] ^= 1;
let tamperedIndexRejected = false;
try { parseGitIndex(tamperedIndex, 20); }
catch { tamperedIndexRejected = true; }
check(tamperedIndexRejected, "The Git index parser must reject a tampered index checksum.");
let mismatchedSharedIndexRejected = false;
try {
  const wrongLink = makeSplitLinkFixture(Buffer.alloc(20, 0x2a),
    splitShared.entries.length, [], []);
  mergeSplitIndex(splitOverlay, splitShared, wrongLink, 20);
} catch { mismatchedSharedIndexRejected = true; }
check(mismatchedSharedIndexRejected,
  "The split-index parser must reject a shared index whose digest differs from the link hash.");
let malformedIndexRejected = false;
try { parseGitIndex(Buffer.from("not-an-index"), 20); }
catch { malformedIndexRejected = true; }
check(malformedIndexRejected, "The Git index parser must fail closed on malformed input.");
check(!trackedFileStatus("website/definitely-untracked-probe.js").tracked,
  "The Git index tracking check must reject an absent path.");
check(pathStaysInside("/repo/website", "/repo/website/asset.js", posix) &&
  !pathStaysInside("/repo/website", "/repo/WEBSITE/asset.js", posix) &&
  !pathStaysInside("/repo/website", "/repo/website-other/asset.js", posix),
"Local-asset containment must reject POSIX case-variant and prefix sibling directories.");

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
const assets = localReferences.map((reference) => {
  const cleanReference = reference.split(/[?#]/, 1)[0];
  const path = resolve(root, cleanReference);
  const contained = pathStaysInside(root, path);
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

const referencedAssets = [...stylesheets, ...scripts]
  .map((asset) => asset.cleanReference)
  .sort();
const documentedAssets = documentedAssetReferences(readme);
check(sameAssetInventory(documentedAssets, referencedAssets),
"The website README must list the exact CSS and JavaScript assets referenced by index.html.");
check(sameAssetInventory(
  documentedAssetReferences("`styles.css` `script.js`"),
  ["styles.css", "script.js"]),
"The README asset verifier must accept the stable asset inventory.");
check(!sameAssetInventory(
  documentedAssetReferences("`styles.css`"),
  ["styles.css", "script.js"]),
"The README asset verifier must reject an incomplete asset inventory.");
check(!sameAssetInventory(
  documentedAssetReferences("`script.js` `script.js`"),
  ["styles.css", "script.js"]),
"The README asset verifier must reject an equal-length mismatched asset inventory.");
check(/GitHub Pages caches each stable URL independently, so warm clients can briefly mix HTML and asset generations/i.test(readme),
"The website README must disclose the residual mixed-generation cache window for stable asset names.");

check(gitAttributesExists, "The repository must keep .gitattributes for website line-ending normalization.");
checkTracked(".gitattributes", "The repository .gitattributes file must be tracked in Git.");
check(/^website\/\*\.css text eol=lf$/m.test(gitAttributes),
  ".gitattributes must normalize top-level website CSS to LF.");
check(/^website\/\*\.js text eol=lf$/m.test(gitAttributes),
  ".gitattributes must normalize top-level website JavaScript to LF.");
check(/^website\/\*\*\/\*\.css text eol=lf$/m.test(gitAttributes),
  ".gitattributes must normalize nested website CSS to LF.");
check(/^website\/\*\*\/\*\.js text eol=lf$/m.test(gitAttributes),
  ".gitattributes must normalize nested website JavaScript to LF.");

for (const asset of [...stylesheets, ...scripts]) {
  checkTracked(`website/${asset.cleanReference}`,
    `Referenced asset ${asset.reference} must be tracked in Git.`);
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
check(/<p\b[^>]*class="[^"]*\bhero__eyebrow\b[^"]*"[^>]*>\s*Local code navigation for AI agents\s*<\/p>/i.test(html),
  "The hero eyebrow must remain text-only without the decorative cyan dot.");
check(/<p\b[^>]*class="[^"]*\bhero__lead\b[^"]*"[^>]*>\s*PhoenixCodeNav helps Claude Code, Codex, and other MCP clients navigate large C# workspaces locally, efficiently, and with every answer labeled by confidence\.\s*<\/p>/i.test(html),
  "The hero lead must use the direct 'workspaces locally' phrasing without an intervening dash.");
check(/It is the connection that lets your coding agent use local tools\. Phoenix runs behind the agent\. It is not another editor or chat app\./i.test(html),
  "The plain-language MCP explanation must use sentence breaks instead of an em dash.");

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
check(/evaluated NuGet package closure[\s\S]*project-to-project F# references remain unsupported/i.test(html),
  "The F# answer must pair the package-closure capability with the undisclosed-free project-reference boundary.");

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

const installGuideUrl = "https://github.com/grlap/PhoenixCodeNav#install-work-machine";
const installGuideLinks = matches(/<a\b[^>]*>/gi)
  .map((match) => match[0])
  .filter((tag) => attribute(tag, "href") === installGuideUrl);
check(!/<section\b[^>]*\bid="setup"/i.test(html) && !/From source to first answer/i.test(html),
  "The website must leave installation guidance to the README instead of embedding a setup walkthrough.");
check(!/href="#setup"/i.test(html),
  "The website must not retain links to the removed setup section.");
check(installGuideLinks.length === 3 && installGuideLinks.every((tag) =>
  attribute(tag, "target") === "_blank" && attribute(tag, "rel").split(/\s+/).includes("noopener")),
"The hero, final call to action, and footer must link safely to the README installation guide.");
check(/<div class="section-label"[^>]*>\s*<span>06<\/span><span>The trust boundary<\/span>/i.test(html) &&
  /<div class="section-label section-label--dark"[^>]*>\s*<span>07<\/span><span>Use the right tool<\/span>/i.test(html),
"Section numbering must remain sequential after removing the setup walkthrough.");

const robots = modeRobots;
if (launchMode) {
  check(hasExactRobotsPolicy(robots, ["index", "follow"]),
    "Launch mode requires the exact index,follow robots directive without duplicate or conflicting tokens.");
  const canonicalTag = matches(/<link\b[^>]*>/gi).map((match) => match[0]).find((tag) => attribute(tag, "rel").toLowerCase() === "canonical");
  check(isAbsoluteHttps(canonicalTag ? attribute(canonicalTag, "href") : ""), "Launch mode requires an absolute HTTPS canonical URL.");
  check(isAbsoluteHttps(metaContent("property", "og:url")), "Launch mode requires an absolute HTTPS og:url.");
  check(isAbsoluteHttps(metaContent("property", "og:image")), "Launch mode requires an absolute HTTPS og:image.");
  check(existsSync(resolve(root, "sitemap.xml")), "Launch mode requires website/sitemap.xml.");
  const termsFiles = ["LICENSE", "LICENSE.md", "LICENSE.txt", "COPYING", "COPYING.md", "TERMS.md", "EULA.md"];
  check(termsFiles.some((name) => existsSync(resolve(repoRoot, name))), "Launch mode requires a root license or explicit use-terms file.");
} else {
  check(hasExactRobotsPolicy(robots, ["noindex", "nofollow"]),
    "Prelaunch mode requires the exact noindex,nofollow guard without duplicate or conflicting tokens.");
}

const stylesheet = stylesheets.length === 1 && stylesheets[0].exists ? readFileSync(stylesheets[0].path, "utf8") : "";
const script = scripts.length === 1 && scripts[0].exists ? readFileSync(scripts[0].path, "utf8") : "";
check(/\.proof-dashboard\s+\.proof-card\.proof-card--shared\s*\{[^}]*grid-column:\s*1\s*\/\s*-1/i.test(stylesheet),
  "The Shared daemon card must span the proof dashboard without bypassing responsive CSS.");
check(/\.setup-step__copy\s*\{[^}]*min-width:\s*0/i.test(stylesheet) &&
  /\.setup-step__copy\s+code\s*\{[^}]*overflow-wrap:\s*anywhere/i.test(stylesheet),
"The larger anatomy copy must not let long inline identifiers widen the mobile layout.");
check(/\.setup__head\s*\{[^}]*margin:\s*clamp\(65px,\s*9vw,\s*130px\)\s+0\s+clamp\(75px,\s*10vw,\s*145px\)/i.test(stylesheet),
  "The anatomy heading must retain breathing room below its section label.");
check(/\.hero__line\s*\{[^}]*overflow:\s*hidden[^}]*padding-bottom:\s*0\.2em[^}]*margin-bottom:\s*-0\.12em/i.test(stylesheet) &&
  /\.hero__line--accent\s*>\s*span\s*\{[^}]*padding-bottom:\s*0\.18em[^}]*margin-bottom:\s*-0\.18em/i.test(stylesheet),
  "The animated hero lines and gradient span must paint full descenders without changing line rhythm.");
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
check(stylesheet.includes(".no-js .atlas__controls"), "CSS must hide dead atlas controls without JavaScript.");
check(!/function setupTabs\b/.test(script) && !/\.(?:config-tabs|config-panel|first-question)/.test(stylesheet),
  "The removed installation walkthrough must not leave tab behavior or presentation behind.");
check(!/data-atlas-step|\bstepButtons\b/.test(script),
  "The removed atlas step controls must not leave dead JavaScript wiring behind.");
check(/let copied = false;[\s\S]*?copied = document\.execCommand\("copy"\);[\s\S]*?finally\s*{\s*textarea\.remove\(\);\s*}[\s\S]*?if \(!copied\) throw new Error/.test(script),
  "The legacy clipboard fallback must report a false copy result and always remove its textarea.");
check(stylesheet.includes(".motion-paused *"), "CSS must pause continuous animation in the global motion-paused state.");
check(reducedMotionKeepsAtlasVisible("@media (prefers-reduced-motion: reduce) { #atlas-canvas { display: block; } }"),
  "The reduced-motion atlas guard must accept a visible still canvas.");
check(!reducedMotionKeepsAtlasVisible("@media (prefers-reduced-motion: reduce) { #atlas-canvas { display: none !important; } }"),
  "The reduced-motion atlas guard must reject a hidden still canvas.");
check(!reducedMotionKeepsAtlasVisible("@media (prefers-reduced-motion: reduce) { #atlas-canvas { display: block; } } #atlas-canvas { display: none; }"),
  "The reduced-motion atlas guard must reject a globally hidden canvas.");
check(!reducedMotionKeepsAtlasVisible("@media (prefers-reduced-motion: reduce) { #atlas-canvas { display: block; } } html #atlas-canvas { display: none; }"),
  "The reduced-motion atlas guard must reject a compound selector that hides the canvas.");
check(reducedMotionKeepsAtlasVisible("@media (prefers-reduced-motion: reduce) { #atlas-canvas { display: block; } } [href='#atlas-canvas'] { display: none; }"),
  "The reduced-motion atlas guard must not mistake an attribute value for the canvas selector.");
check(!reducedMotionKeepsAtlasVisible("@media (prefers-reduced-motion: reduce) { #atlas-canvas { display: block; } } @media (prefers-reduced-motion: reduce) { #atlas-canvas { visibility: hidden; } }"),
  "The reduced-motion atlas guard must inspect every matching media block.");
check(reducedMotionKeepsAtlasVisible(stylesheet) &&
  /let state = reducedMotion\.matches \? 3 : 0/.test(script) &&
  /function canAnimate\(\)\s*\{[\s\S]*?!reducedMotion\.matches/.test(script),
"Reduced-motion visitors must see the final atlas state without continuous animation.");
check(/function updatePauseButton\(\)[\s\S]*?const motionPaused = reducedMotion\.matches \|\| userPaused[\s\S]*?setGlobalMotionPaused\(motionPaused\)/.test(script),
  "The atlas pause state must combine live media preference with independent user intent.");
check(/pauseButton\.addEventListener\("click"[\s\S]*?updatePauseButton\(\)/.test(script), "The pause button click handler must update the global pause state.");
check(/function applyReducedMotionPreference\(\)[\s\S]*?stopFrameLoop\(\)[\s\S]*?clearTimeout\(timer\)[\s\S]*?if \(reducedMotion\.matches\)[\s\S]*?setState\(3\)[\s\S]*?draw\(performance\.now\(\), true\)[\s\S]*?startFrameLoop\(\)[\s\S]*?scheduleStep\(\)/.test(script),
  "Live reduced-motion changes must stop work, draw state 3, and restore animation only through canAnimate.");
check(/reducedMotion\.addEventListener\("change", applyReducedMotionPreference\)/.test(script),
  "The atlas must react when prefers-reduced-motion changes after page load.");
check(/function init\(\)[\s\S]*?document\.documentElement\.classList\.replace\("no-js", "js"\);[\s\S]*?window\.__phoenixReady = true/.test(script), "Successful initialization must reveal JavaScript controls before setting the readiness marker.");
check(!/<script>[^<]*classList\.replace\("no-js", "js"\)/i.test(html), "Inline markup must not disable the fallback before the external script loads.");

if (failures.length) {
  console.error(`Website ${launchMode ? "launch" : "prelaunch"} verification failed (${failures.length}/${checks} checks):`);
  failures.forEach((failure) => console.error(`- ${failure}`));
  process.exit(1);
}

console.log(`Website ${launchMode ? "launch" : "prelaunch"} verification passed (${checks} checks).`);
