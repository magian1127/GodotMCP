import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

const verifier = path.join(import.meta.dirname, "verify-doc-localization.mjs");
const temporaryRoot = path.resolve(tmpdir());
const fixtureRoot = mkdtempSync(
  path.join(temporaryRoot, "godot-doc-localization-"),
);
const source = "# Reference\n\n[Guide](guide.md#stable)\n";
const chinese =
  "# 中文参考\n\n[英文原文](README.md)\n\n[指南](guide.zh-CN.md#stable)\n";
const files = {
  "README.md": source,
  "README.zh-CN.md": chinese,
  "guide.md": '# Guide\n\n<a id="stable"></a>\n',
  "guide.zh-CN.md":
    '# 中文指南\n\n[英文原文](guide.md)\n\n<a id="stable"></a>\n',
};
let passed = 0;

function check(name, contents, expectedError) {
  const directory = path.join(fixtureRoot, name);
  mkdirSync(directory);
  for (const [file, text] of Object.entries(contents)) {
    writeFileSync(path.join(directory, file), text);
  }
  const result = spawnSync(process.execPath, [verifier, directory], {
    encoding: "utf8",
    timeout: 10_000,
    windowsHide: true,
  });
  assert.equal(result.error, undefined, `${name}: ${result.error?.message}`);
  const summary = JSON.parse(result.stdout);
  if (expectedError) {
    assert.equal(result.status, 1, `${name}: should fail`);
    assert.ok(summary.errors > 0);
    assert.ok(
      result.stderr.includes(expectedError),
      `${name}: ${result.stderr}`,
    );
  } else {
    assert.equal(result.status, 0, `${name}: ${result.stderr}`);
    assert.equal(summary.errors, 0);
  }
  passed++;
  console.log(`PASS: ${name}`);
}

try {
  check("existing-localized-link", files);
  check("nested-indented-command", {
    "README.md":
      '# 中文命令\n\n1. 执行本地检查：\n\n       node "local/server/check.mjs" --source input\n',
  });
  const missingTarget = { ...files };
  delete missingTarget["guide.zh-CN.md"];
  check("missing-localized-target", missingTarget, "Markdown 链接目标");
  check(
    "wrong-localized-document",
    {
      ...files,
      "README.zh-CN.md": chinese.replace("guide.zh-CN.md", "other.zh-CN.md"),
      "other.md": "# Other guide\n",
      "other.zh-CN.md": "# 另一份指南\n\n[英文原文](other.md)\n",
    },
    "Markdown 链接目标",
  );
  check(
    "changed-anchor",
    {
      ...files,
      "README.zh-CN.md": chinese.replace("#stable", "#different"),
    },
    "Markdown 链接目标",
  );
  check(
    "external-link-is-not-localized",
    {
      "README.md": source.replace(
        "guide.md#stable",
        "https://example.invalid/guide.md",
      ),
      "README.zh-CN.md": chinese.replace(
        "guide.zh-CN.md#stable",
        "https://example.invalid/guide.zh-CN.md",
      ),
    },
    "Markdown 链接目标",
  );
  check(
    "missing-language-link",
    {
      ...files,
      "README.zh-CN.md": chinese.replace("[英文原文](README.md)", ""),
    },
    "缺少指向英文原文",
  );
  check(
    "code-fence-drift",
    {
      ...files,
      "README.md": source + '\n```json\n{"command":"node"}\n```\n',
      "README.zh-CN.md": chinese + '\n```json\n{"command":"different"}\n```\n',
    },
    "代码围栏内容",
  );
  console.log(
    `All ${passed} documentation validator regression checks passed.`,
  );
} finally {
  const resolved = path.resolve(fixtureRoot);
  if (
    !resolved.startsWith(temporaryRoot + path.sep) ||
    !path.basename(resolved).startsWith("godot-doc-localization-")
  ) {
    throw new Error(
      "Refusing to remove a fixture outside the temporary test directory",
    );
  }
  rmSync(resolved, { recursive: true, force: true });
}
