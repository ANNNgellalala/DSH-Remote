#!/usr/bin/env node
// Minimal read-only Electron asar reader (no deps).
// Usage:
//   node scripts/asar-extract.mjs list   [--asar <path>] [--grep <substr>] [--limit N]
//   node scripts/asar-extract.mjs tree   [--asar <path>] [--grep <substr>]
//   node scripts/asar-extract.mjs read   <inner/path> [--asar <path>] [--out <file>]
//   node scripts/asar-extract.mjs stat   <inner/path> [--asar <path>]
//   node scripts/asar-extract.mjs pkgs   [--asar <path>] [--filter <substr>]
//
// asar: 4B payload-size(=4) + 4B header-size + JSON header + data area (8+headerSize, 4-byte aligned)
import fs from 'node:fs';
import path from 'node:path';

const DEFAULT_ASAR = 'D:\\Program Files\\DeepSeek Harness\\resources\\app.asar';

function argOf(argv, name, dflt) {
  const i = argv.indexOf(name);
  return i >= 0 && argv[i + 1] ? argv[i + 1] : dflt;
}

function openAsar(file) {
  const fd = fs.openSync(file, 'r');
  const head = Buffer.alloc(64);
  fs.readSync(fd, head, 0, 64, 0);
  const payloadSize = head.readUInt32LE(0); // size of the pickled header-size field (=4)
  const headerSize = head.readUInt32LE(4); // length of the pickle containing the JSON header
  // locate the JSON object start on a 4-byte boundary within the first 64 bytes
  let jsonStart = -1;
  for (let i = 8; i < 64; i += 4) if (head[i] === 0x7b /* { */) { jsonStart = i; break; }
  if (jsonStart < 0) throw new Error('asar: JSON header not found');
  // data area begins right after the pickled header block: 8 + headerSize
  const dataOffset = 8 + headerSize;
  const jsonSize = dataOffset - jsonStart;
  const headerJson = Buffer.alloc(jsonSize);
  fs.readSync(fd, headerJson, 0, jsonSize, jsonStart);
  const header = JSON.parse(headerJson.toString('utf8'));
  return { fd, header, headerSize, jsonSize, jsonStart, dataOffset, payloadSize };
}

function walk(node, prefix, out) {
  for (const [name, val] of Object.entries(node.files || {})) {
    const p = prefix ? `${prefix}/${name}` : name;
    if (val.files) walk(val, p, out);
    else out.push({ path: p, size: val.size ?? 0, offset: val.offset, unpacked: !!val.unpacked, link: val.link });
  }
  return out;
}

function readFile(a, entry) {
  if (entry.unpacked) throw new Error('unpacked: read from app.asar.unpacked/' + entry.path);
  if (entry.link) throw new Error('symlink: ' + entry.link);
  const buf = Buffer.alloc(entry.size);
  fs.readSync(a.fd, buf, 0, entry.size, a.dataOffset + Number(entry.offset));
  return buf;
}

const argv = process.argv.slice(2);
const cmd = argv[0] && !argv[0].startsWith('--') ? argv[0] : 'list';
const asarPath = argOf(argv, '--asar', DEFAULT_ASAR);
const a = openAsar(asarPath);

if (cmd === 'stat') {
  const files = walk(a.header, '', []);
  console.log(JSON.stringify({ headerSize: a.headerSize, jsonSize: a.jsonSize, dataOffset: a.dataOffset, fileCount: files.length }, null, 2));
  process.exit(0);
}

const files = walk(a.header, '', []);

if (cmd === 'list' || cmd === 'tree') {
  const g = argOf(argv, '--grep', '');
  const limit = Number(argOf(argv, '--limit', '0')) || 0;
  let rows = files
    .filter((f) => !g || f.path.includes(g))
    .sort((x, y) => x.path.localeCompare(y.path));
  if (limit) rows = rows.slice(0, limit);
  for (const f of rows) console.log((f.unpacked ? 'U ' : '  ') + String(f.size).padStart(10) + '  ' + f.path);
  console.log(`-- ${rows.length}/${files.length} files`);
  process.exit(0);
}

if (cmd === 'pkgs') {
  const filter = argOf(argv, '--filter', '');
  const seen = new Map();
  for (const f of files) {
    const m = f.path.match(/^([^/]+\/)*?node_modules\/((?:@[^/]+\/)?[^/]+)\/package\.json$/);
    if (!m) continue;
    const name = m[2];
    if (filter && !name.includes(filter)) continue;
    if (!seen.has(name)) seen.set(name, f.path);
  }
  for (const [n, p] of [...seen].sort()) console.log(n + '\t' + p);
  console.log(`-- ${seen.size} packages`);
  process.exit(0);
}

if (cmd === 'read') {
  const inner = argv[1];
  if (!inner) { console.error('need inner path'); process.exit(2); }
  const entry = files.find((f) => f.path === inner || f.path === inner.replace(/\\/g, '/'));
  if (!entry) { console.error('not found: ' + inner); process.exit(3); }
  const buf = readFile(a, entry);
  const out = argOf(argv, '--out', '');
  if (out) { fs.writeFileSync(out, buf); console.error(`wrote ${buf.length} bytes -> ${out}`); }
  else process.stdout.write(buf);
  process.exit(0);
}

console.error('unknown command: ' + cmd);
process.exit(2);
