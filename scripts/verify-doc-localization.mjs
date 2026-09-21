import { promises as fs } from "node:fs";
import path from "node:path";

const workspaceRoot = path.resolve(import.meta.dirname, "..");
const requestedRoots = process.argv
  .slice(2)
  .filter((argument) => !argument.startsWith("--"));
const summaryOnly = process.argv.includes("--summary-only");
const scanRoots =
  requestedRoots.length > 0
    ? requestedRoots.map((requestedRoot) => path.resolve(requestedRoot))
    : [workspaceRoot];
const ignoredDirectoryNames = new Set([
  ".cache",
  ".git",
  ".godot",
  ".godot-mcp-unified-backups",
  // .superpowers 是 AI 会话的过程产物（SDD 进度/评审包），与 .cache 同类，
  // 各仓库 .gitignore 均已排除，不参与文档本地化校验。
  ".superpowers",
  "dist",
  "node_modules",
]);
const ignoredRelativePrefixes = [
  "references/github/",
  "references/local/",
  "references/test-artifacts/",
];

function relative(filePath) {
  return path.relative(workspaceRoot, filePath).replaceAll("\\", "/");
}

function isIgnored(filePath) {
  const rel = relative(filePath);
  return ignoredRelativePrefixes.some((prefix) => rel.startsWith(prefix));
}

async function collectMarkdownFiles(directory, files = []) {
  for (const entry of await fs.readdir(directory, { withFileTypes: true })) {
    if (entry.isDirectory() && ignoredDirectoryNames.has(entry.name)) continue;
    const fullPath = path.join(directory, entry.name);
    if (isIgnored(fullPath)) continue;
    if (entry.isDirectory()) {
      await collectMarkdownFiles(fullPath, files);
    } else if (
      entry.isFile() &&
      (entry.name.endsWith(".md") ||
        entry.name === "LICENSE" ||
        entry.name === "llms.txt")
    ) {
      files.push(fullPath);
    }
  }
  return files;
}

function chineseCharacterCount(text) {
  return (text.match(/[\u3400-\u9fff]/g) || []).length;
}

const proseWords = new Set([
  "a",
  "about",
  "and",
  "are",
  "as",
  "at",
  "by",
  "class",
  "client",
  "defined",
  "description",
  "example",
  "for",
  "format",
  "from",
  "function",
  "if",
  "in",
  "interface",
  "is",
  "method",
  "methods",
  "name",
  "of",
  "on",
  "options",
  "or",
  "original",
  "parameter",
  "parameters",
  "property",
  "read",
  "register",
  "required",
  "returns",
  "server",
  "source",
  "that",
  "the",
  "this",
  "to",
  "tool",
  "type",
  "use",
  "when",
  "with",
]);

function untranslatedProseLines(text) {
  const results = [];
  const lines = text.split(/\r?\n/);
  let fenceCharacter = "";
  let fenceLength = 0;

  for (let index = 0; index < lines.length; index += 1) {
    const originalLine = lines[index];
    const marker = /^\s*(`{3,}|~{3,})/.exec(originalLine)?.[1] || "";
    if (marker) {
      if (!fenceCharacter) {
        fenceCharacter = marker[0];
        fenceLength = marker.length;
      } else if (marker[0] === fenceCharacter && marker.length >= fenceLength) {
        fenceCharacter = "";
        fenceLength = 0;
      }
      continue;
    }
    if (fenceCharacter || /^\s{4,}\S/.test(originalLine)) continue;
    if (/^\s*Copyright\b/i.test(originalLine)) continue;

    let visible = originalLine;
    visible = visible.replace(/`+[^`\n]*`+/g, " ");
    visible = visible.replace(/!??\[([^\]]*)\]\([^)\n]+\)/g, "$1");
    visible = visible.replace(/https?:\/\/\S+/g, " ");
    visible = visible.replace(/<[^>\n]+>/g, " ");
    if (chineseCharacterCount(visible) > 0) continue;

    const words = (visible.match(/[A-Za-z][A-Za-z'-]*/g) || []).map((word) =>
      word.toLowerCase(),
    );
    const letterCount = words.reduce((total, word) => total + word.length, 0);
    const hasProseWord = words.some((word) => proseWords.has(word));
    if (words.length >= 3 && letterCount >= 18 && hasProseWord) {
      results.push({ line: index + 1, text: originalLine.trim() });
    }
  }
  return results;
}

function fencedBlocks(text) {
  const lines = text.match(/.*(?:\r?\n|$)/g)?.filter(Boolean) || [];
  const blocks = [];
  let block = "";
  let markerCharacter = "";
  let markerLength = 0;

  for (const line of lines) {
    const marker = /^\s*(`{3,}|~{3,})/.exec(line)?.[1] || "";
    if (!markerCharacter && marker) {
      markerCharacter = marker[0];
      markerLength = marker.length;
      block = line;
      continue;
    }
    if (!markerCharacter) continue;
    block += line;
    if (
      marker &&
      marker[0] === markerCharacter &&
      marker.length >= markerLength
    ) {
      blocks.push(block.replaceAll("\r\n", "\n"));
      block = "";
      markerCharacter = "";
      markerLength = 0;
    }
  }
  return blocks;
}

function fenceLanguage(block) {
  return (
    /^\s*(?:`{3,}|~{3,})\s*([^\s`]*)/.exec(block)?.[1]?.toLowerCase() || ""
  );
}

function fenceContent(block) {
  const lines = block.split("\n");
  return lines.slice(1, -1).join("\n");
}

function isProseFence(block) {
  return new Set(["md", "markdown", "mermaid", "text", "txt"]).has(
    fenceLanguage(block),
  );
}

function linkDestinations(text) {
  return [...text.matchAll(/\]\(([^)\n]+)\)/g)].map((match) => match[1]);
}

function rawUrls(text) {
  return [...text.matchAll(/https?:\/\/[^\s)>]+/g)]
    .map((match) => match[0])
    .sort();
}

function arraysEqual(left, right) {
  return JSON.stringify(left) === JSON.stringify(right);
}

function removeLanguageLink(destinations, sourceFileName) {
  const result = [...destinations];
  const index = result.findIndex(
    (destination) =>
      destination === sourceFileName || destination === "./" + sourceFileName,
  );
  if (index >= 0) result.splice(index, 1);
  return { found: index >= 0, destinations: result };
}

async function equivalentLinkDestinations(
  sourceLinks,
  chineseLinks,
  chinesePath,
) {
  if (sourceLinks.length !== chineseLinks.length) return false;
  const matches = await Promise.all(
    sourceLinks.map(async (sourceLink, index) => {
      const chineseLink = chineseLinks[index];
      if (sourceLink === chineseLink) return true;
      if (/^(?:[a-z][a-z0-9+.-]*:|\/\/)/i.test(chineseLink)) return false;
      const localized = /^([^?#]+)\.zh-CN\.md([?#].*)?$/.exec(chineseLink);
      if (
        !localized ||
        `${localized[1]}.md${localized[2] || ""}` !== sourceLink
      )
        return false;
      // 只接受同一文档实际存在的中文副本；路径、锚点和查询参数必须对应。
      const directory = path.dirname(chinesePath);
      return (
        (await exists(path.resolve(directory, `${localized[1]}.zh-CN.md`))) &&
        (await exists(path.resolve(directory, `${localized[1]}.md`)))
      );
    }),
  );
  return matches.every(Boolean);
}

function chineseSidecarPath(sourcePath) {
  if (sourcePath.endsWith(".md"))
    return sourcePath.replace(/\.md$/i, ".zh-CN.md");
  if (sourcePath.endsWith("llms.txt"))
    return sourcePath.replace(/\.txt$/i, ".zh-CN.txt");
  if (path.basename(sourcePath) === "LICENSE") return sourcePath + ".zh-CN.md";
  throw new Error(`不支持的文档类型：${relative(sourcePath)}`);
}

async function verifySidecar(sourcePath, chinesePath, errors) {
  const [source, chinese] = await Promise.all([
    fs.readFile(sourcePath, "utf8"),
    fs.readFile(chinesePath, "utf8"),
  ]);
  if (chineseCharacterCount(chinese) < 2) {
    errors.push(`${relative(chinesePath)} 没有足够的中文内容`);
  }
  const untranslatedLines = untranslatedProseLines(chinese);
  if (untranslatedLines.length > 0) {
    const examples = untranslatedLines
      .slice(0, 3)
      .map((entry) => `${entry.line}:${entry.text}`)
      .join(" | ");
    errors.push(
      `${relative(chinesePath)} 仍有 ${untranslatedLines.length} 行疑似未翻译英文正文（${examples}）`,
    );
  }
  const sourceFenceBlocks = fencedBlocks(source);
  const chineseFenceBlocks = fencedBlocks(chinese);
  const untranslatedFenceExamples = [];
  for (let index = 0; index < sourceFenceBlocks.length; index += 1) {
    if (!isProseFence(sourceFenceBlocks[index]) || !chineseFenceBlocks[index])
      continue;
    const lines = untranslatedProseLines(
      fenceContent(chineseFenceBlocks[index]),
    );
    if (lines.length > 0) {
      untranslatedFenceExamples.push(
        `围栏 ${index + 1} 仍有 ${lines.length} 行（${lines
          .slice(0, 2)
          .map((entry) => entry.text)
          .join(" | ")}）`,
      );
    }
  }
  if (untranslatedFenceExamples.length > 0) {
    errors.push(
      `${relative(chinesePath)} 的文本/Markdown 围栏仍有英文正文（${untranslatedFenceExamples
        .slice(0, 3)
        .join("；")}）`,
    );
  }

  const languageLink = removeLanguageLink(
    linkDestinations(chinese),
    path.basename(sourcePath),
  );
  if (!languageLink.found) {
    errors.push(`${relative(chinesePath)} 缺少指向英文原文的语言链接`);
  }
  if (
    !(await equivalentLinkDestinations(
      linkDestinations(source),
      languageLink.destinations,
      chinesePath,
    ))
  ) {
    errors.push(
      `${relative(chinesePath)} 的 Markdown 链接目标与英文原文不一致`,
    );
  }
  if (!arraysEqual(rawUrls(source), rawUrls(chinese))) {
    errors.push(`${relative(chinesePath)} 的裸 URL 与英文原文不一致`);
  }
  const comparableSourceBlocks = sourceFenceBlocks.map((block) =>
    isProseFence(block) ? block.split("\n", 1)[0] : block,
  );
  const comparableChineseBlocks = chineseFenceBlocks.map((block) =>
    isProseFence(block) ? block.split("\n", 1)[0] : block,
  );
  if (!arraysEqual(comparableSourceBlocks, comparableChineseBlocks)) {
    const sourceBlocks = comparableSourceBlocks;
    const chineseBlocks = comparableChineseBlocks;
    const changedBlocks = [];
    const maximumBlocks = Math.max(sourceBlocks.length, chineseBlocks.length);
    for (let index = 0; index < maximumBlocks; index += 1) {
      if (sourceBlocks[index] !== chineseBlocks[index]) {
        changedBlocks.push(
          `${index + 1}:${(sourceBlocks[index] || "<missing>").split("\n", 1)[0]}`,
        );
      }
    }
    errors.push(
      `${relative(chinesePath)} 的代码围栏内容与英文原文不一致（${changedBlocks
        .slice(0, 5)
        .join(" | ")}）`,
    );
  }
}

async function exists(filePath) {
  try {
    await fs.access(filePath);
    return true;
  } catch {
    return false;
  }
}

async function main() {
  const discoveredFiles = [];
  for (const scanRoot of scanRoots) {
    const scanStat = await fs.stat(scanRoot);
    if (scanStat.isDirectory()) {
      await collectMarkdownFiles(scanRoot, discoveredFiles);
    } else {
      discoveredFiles.push(scanRoot);
    }
  }
  const files = [...new Set(discoveredFiles)].sort();
  const errors = [];
  const sidecarPairs = [];
  const chineseCanonicalFiles = [];
  const englishArchiveFiles = [];

  for (const filePath of files) {
    if (filePath.endsWith(".en.md")) {
      englishArchiveFiles.push(filePath);
      const canonicalPath = filePath.replace(/\.en\.md$/i, ".md");
      if (!(await exists(canonicalPath))) {
        errors.push(
          `${relative(filePath)} 缺少中文 canonical 文档 ${relative(canonicalPath)}`,
        );
      } else {
        const [archiveContents, canonicalContents] = await Promise.all([
          fs.readFile(filePath, "utf8"),
          fs.readFile(canonicalPath, "utf8"),
        ]);
        if (chineseCharacterCount(canonicalContents) < 2) {
          errors.push(`${relative(canonicalPath)} 不是中文 canonical 文档`);
        }
        if (
          !linkDestinations(archiveContents).includes(
            path.basename(canonicalPath),
          )
        ) {
          errors.push(`${relative(filePath)} 缺少指向中文主文档的语言链接`);
        }
        if (
          !linkDestinations(canonicalContents).includes(path.basename(filePath))
        ) {
          errors.push(`${relative(canonicalPath)} 缺少指向英文归档的语言链接`);
        }
      }
      continue;
    }
    if (filePath.endsWith(".zh-CN.md") || filePath.endsWith(".zh-CN.txt"))
      continue;

    const contents = await fs.readFile(filePath, "utf8");
    if (chineseCharacterCount(contents) >= 2) {
      chineseCanonicalFiles.push(filePath);
      const untranslatedLines = untranslatedProseLines(contents);
      if (untranslatedLines.length > 0) {
        const examples = untranslatedLines
          .slice(0, 3)
          .map((entry) => `${entry.line}:${entry.text}`)
          .join(" | ");
        errors.push(
          `${relative(filePath)} 仍有 ${untranslatedLines.length} 行疑似未翻译英文正文（${examples}）`,
        );
      }
      continue;
    }

    const sidecarPath = chineseSidecarPath(filePath);
    if (!(await exists(sidecarPath))) {
      errors.push(`${relative(filePath)} 缺少中文版 ${relative(sidecarPath)}`);
      continue;
    }
    sidecarPairs.push([filePath, sidecarPath]);
  }

  for (const [sourcePath, chinesePath] of sidecarPairs) {
    await verifySidecar(sourcePath, chinesePath, errors);
  }

  const failedArtifacts = files.filter((filePath) =>
    filePath.endsWith(".failed.md"),
  );
  for (const filePath of failedArtifacts)
    errors.push(`${relative(filePath)} 是失败产物`);

  const result = {
    scannedMarkdownFiles: files.length,
    chineseCanonicalFiles: chineseCanonicalFiles.length,
    englishArchives: englishArchiveFiles.length,
    englishChineseSidecarPairs: sidecarPairs.length,
    errors: errors.length,
  };
  process.stdout.write(JSON.stringify(result, null, 2) + "\n");
  if (errors.length > 0) {
    if (!summaryOnly) {
      for (const error of errors) process.stderr.write(`- ${error}\n`);
    }
    process.exitCode = 1;
  }
}

await main();
