import fs from "node:fs";
import path from "node:path";

export function parseArgs(argv, supported = []) {
  const out = {};
  const allowed = new Set(["project-root", "output", "replace", ...supported]);
  for (let i = 0; i < argv.length; i += 1) {
    const key = argv[i];
    if (!key.startsWith("--")) throw new Error(`unexpected argument: ${key}`);
    const name = key.slice(2);
    if (!allowed.has(name)) throw new Error(`unsupported option: --${name}`);
    if (name === "replace") {
      out.replace = true;
      continue;
    }
    const value = argv[++i];
    if (value === undefined) throw new Error(`missing value for --${name}`);
    out[name] = value;
  }
  return out;
}

export function boundedNumber(value, fallback, min, max, name) {
  if (value === undefined) return fallback;
  const number = Number(value);
  if (!Number.isFinite(number) || number < min || number > max) {
    throw new Error(`${name} must be between ${min} and ${max}`);
  }
  return number;
}

export function resolveProjectOutput(args, extension) {
  if (!args["project-root"] || !args.output) {
    throw new Error("--project-root and --output are required");
  }
  const root = path.resolve(args["project-root"]);
  if (!fs.existsSync(root) || !fs.statSync(root).isDirectory()) {
    throw new Error(`project root is not a directory: ${root}`);
  }
  if (!fs.existsSync(path.join(root, "project.godot"))) {
    throw new Error(`project root does not contain project.godot: ${root}`);
  }
  const realRoot = fs.realpathSync(root);
  const relative = String(args.output).replace(/^res:\/\//, "").replaceAll("/", path.sep);
  if (path.isAbsolute(relative)) throw new Error("--output must be project-relative or res://");
  const output = path.resolve(root, relative);
  const rel = path.relative(root, output);
  if (rel === "" || rel.startsWith(`..${path.sep}`) || rel === ".." || path.isAbsolute(rel)) {
    throw new Error("output escapes --project-root");
  }
  if (path.extname(output).toLowerCase() !== extension) {
    throw new Error(`output must end with ${extension}`);
  }

  // 当项目内的某个目录本身是符号链接/junction 时,仅凭词法上的包含关系并不足够。
  // 因此解析最深的已存在祖先目录,并要求其真实路径仍位于真实项目根之下。
  let ancestor = path.dirname(output);
  while (!fs.existsSync(ancestor)) {
    const parent = path.dirname(ancestor);
    if (parent === ancestor) throw new Error("could not resolve output parent");
    ancestor = parent;
  }
  const realAncestor = fs.realpathSync(ancestor);
  const realRelative = path.relative(realRoot, realAncestor);
  if (
    realRelative === ".." ||
    realRelative.startsWith(`..${path.sep}`) ||
    path.isAbsolute(realRelative)
  ) {
    throw new Error("output resolves outside --project-root through a symlink or junction");
  }

  try {
    if (fs.lstatSync(output).isSymbolicLink()) {
      throw new Error("output must not be a symbolic link");
    }
  } catch (error) {
    if (error instanceof Error && error.message === "output must not be a symbolic link") throw error;
    if (!(error && typeof error === "object" && "code" in error && error.code === "ENOENT")) throw error;
  }
  if (fs.existsSync(output) && args.replace !== true) {
    throw new Error(`output exists: ${output}; pass --replace to overwrite`);
  }
  return { root, output, resourcePath: `res://${rel.replaceAll(path.sep, "/")}` };
}

export function writeResult(output, resourcePath, details) {
  process.stdout.write(`${JSON.stringify({ success: true, output, resource_path: resourcePath, ...details })}\n`);
}
