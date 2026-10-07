import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { lint } from 'markdownlint/promise';

const excluded = new Set(['.git', '_reference', 'node_modules', 'temp', 'artifacts', 'bin', 'obj', '.worktrees']);
const files = [];
async function walk(dir) {
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    if (excluded.has(entry.name)) continue;
    const file = path.join(dir, entry.name);
    if (entry.isDirectory()) await walk(file);
    else if (entry.name.endsWith('.md')) files.push(file);
  }
}
await walk('.');
const config = { default: true, MD013: false, MD024: { siblings_only: true }, MD033: false };
const result = await lint({ files: files.filter(file => !file.endsWith('pull_request_template.md')), config });
for (const [file, errors] of Object.entries(result)) {
  for (const error of errors) {
    console.error(`${file}:${error.lineNumber} ${error.ruleNames[0]} ${error.ruleDescription} ${error.errorDetail ?? ''}`);
    process.exitCode = 1;
  }
}
// リポジトリ内へのリンクはファイルの実在も検査する。
for (const file of files) {
  const text = await readFile(file, 'utf8');
  for (const match of text.matchAll(/\]\(([^)]+)\)/g)) {
    const target = match[1].split('#')[0];
    if (!target || /^(https?:|mailto:|\/)/.test(target)) continue;
    try { await readFile(path.resolve(path.dirname(file), target)); }
    catch { console.error(`Broken local link in ${file}: ${target}`); process.exitCode = 1; }
  }
}
