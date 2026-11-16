#!/usr/bin/env node
// Dump the Host-side Typert Remote contract (endpoints, params, JSON Schemas,
// forwarded events) from the DSH asar into one JSON document.
//
// Evidence source: each `@deepseek-ai/*` package ships a generated
// `lib/typert.host.js` whose tail is
//   const $remote = createTypertRemoteInfo({ package, methods: {...}, events: [...] })
// that object literal is pure data (zod factories are only referenced, not called),
// so it can be parsed with a tiny JS-object-literal reader instead of imported.
//
// Usage:
//   node scripts/dump-remote-contract.mjs [--asar <path>] [--out <file>]
//                                         [--namespace <substr>] [--summary]
import fs from 'node:fs';

const DEFAULT_ASAR = 'D:\\Program Files\\DeepSeek Harness\\resources\\app.asar';

function argOf(argv, name, dflt) {
  const i = argv.indexOf(name);
  return i >= 0 && argv[i + 1] && !argv[i + 1].startsWith('--') ? argv[i + 1] : dflt;
}

// ---------- asar reader (same layout as scripts/asar-extract.mjs) ----------
function openAsar(file) {
  const fd = fs.openSync(file, 'r');
  const head = Buffer.alloc(64);
  fs.readSync(fd, head, 0, 64, 0);
  const headerSize = head.readUInt32LE(4);
  let jsonStart = -1;
  for (let i = 8; i < 64; i += 4) if (head[i] === 0x7b) { jsonStart = i; break; }
  if (jsonStart < 0) throw new Error('asar: JSON header not found');
  const dataOffset = 8 + headerSize;
  const jsonSize = dataOffset - jsonStart;
  const headerJson = Buffer.alloc(jsonSize);
  fs.readSync(fd, headerJson, 0, jsonSize, jsonStart);
  return { fd, header: JSON.parse(headerJson.toString('utf8')), dataOffset };
}
function walk(node, prefix, out) {
  for (const [name, val] of Object.entries(node.files || {})) {
    const p = prefix ? `${prefix}/${name}` : name;
    if (val.files) walk(val, p, out);
    else out.push({ path: p, size: val.size ?? 0, offset: val.offset, unpacked: !!val.unpacked });
  }
  return out;
}
function readEntry(a, entry) {
  const buf = Buffer.alloc(entry.size);
  fs.readSync(a.fd, buf, 0, entry.size, a.dataOffset + Number(entry.offset));
  return buf.toString('utf8');
}

// ---------- tiny JS object-literal reader ----------
// Tokens: strings, numbers, identifiers (bare refs like `true` / `_x$schema`), punctuation.
function tokenize(src) {
  const out = [];
  let i = 0;
  const n = src.length;
  while (i < n) {
    const c = src[i];
    if (c === '/' && src[i + 1] === '/') { while (i < n && src[i] !== '\n') i++; continue; }
    if (c === '/' && src[i + 1] === '*') { i += 2; while (i < n && !(src[i] === '*' && src[i + 1] === '/')) i++; i += 2; continue; }
    if (c === ' ' || c === '\t' || c === '\n' || c === '\r') { i++; continue; }
    if (c === '"' || c === "'" || c === '`') {
      const quote = c; let s = ''; i++;
      while (i < n) {
        const ch = src[i];
        if (ch === '\\') { const e = src[i + 1]; s += e === 'n' ? '\n' : e === 't' ? '\t' : e === 'r' ? '\r' : e === 'u' ? String.fromCharCode(parseInt(src.slice(i + 2, i + 6), 16)) : e; i += e === 'u' ? 6 : 2; continue; }
        if (ch === quote) { i++; break; }
        if (ch === '$' && src[i + 1] === '{') { // template literal with interpolation: give up on this string
          s += '${'; i += 2; continue;
        }
        s += ch; i++;
      }
      out.push({ t: 'str', v: s });
      continue;
    }
    if (/[0-9]/.test(c)) { let j = i; while (j < n && /[0-9.eE+-]/.test(src[j])) j++; out.push({ t: 'num', v: src.slice(i, j) }); i = j; continue; }
    if (/[A-Za-z_$]/.test(c)) { let j = i; while (j < n && /[A-Za-z0-9_$]/.test(src[j])) j++; out.push({ t: 'id', v: src.slice(i, j) }); i = j; continue; }
    out.push({ t: 'p', v: c }); i++;
  }
  return out;
}
function parseValue(tokens, st) {
  const tok = tokens[st.i];
  if (!tok) throw new Error('unexpected end');
  if (tok.t === 'str') { st.i++; return tok.v; }
  if (tok.t === 'num') { st.i++; return Number(tok.v); }
  if (tok.t === 'id') { st.i++; return { __ref: tok.v }; }
  if (tok.t === 'p' && tok.v === '{') {
    st.i++;
    const obj = {};
    for (;;) {
      const key = tokens[st.i];
      if (!key) throw new Error('unterminated object');
      if (key.t === 'p' && key.v === '}') { st.i++; return obj; }
      let k;
      if (key.t === 'p' && key.v === '[') { // spread/array-keyed entries do not occur here; skip defensively
        st.i++; let depth = 1;
        while (st.i < tokens.length && depth > 0) { const t = tokens[st.i++]; if (t.t === 'p' && (t.v === '[' || t.v === '{')) depth++; else if (t.t === 'p' && (t.v === ']' || t.v === '}')) depth--; }
        if (tokens[st.i] && tokens[st.i].v === ',') st.i++;
        continue;
      }
      k = String(key.v); st.i++;
      if (tokens[st.i] && tokens[st.i].t === 'p' && tokens[st.i].v === ':') st.i++;
      obj[k] = parseValue(tokens, st);
      if (tokens[st.i] && tokens[st.i].t === 'p' && tokens[st.i].v === ',') st.i++;
    }
  }
  if (tok.t === 'p' && tok.v === '[') {
    st.i++;
    const arr = [];
    for (;;) {
      const t = tokens[st.i];
      if (!t) throw new Error('unterminated array');
      if (t.t === 'p' && t.v === ']') { st.i++; return arr; }
      arr.push(parseValue(tokens, st));
      if (tokens[st.i] && tokens[st.i].t === 'p' && tokens[st.i].v === ',') st.i++;
    }
  }
  st.i++;
  return null;
}

// ---------- extract the $remote literal ----------
function extractRemoteInfo(src) {
  const marker = 'createTypertRemoteInfo(';
  const at = src.indexOf(marker);
  if (at < 0) return null;
  const tokens = tokenize(src.slice(at + marker.length - 1)); // start at '('
  const st = { i: 0 };
  if (!(tokens[0].t === 'p' && tokens[0].v === '(')) throw new Error('expected (');
  st.i = 1;
  return parseValue(tokens, st);
}

function summarize(info) {
  if (!info || typeof info !== 'object') return null;
  const methods = {};
  for (const [endpoint, m] of Object.entries(info.methods || {})) {
    if (!m || typeof m !== 'object') continue;
    const params = Array.isArray(m.parameters)
      ? m.parameters.map((p) => ({
          name: p.name,
          wire: p.wire ?? null,
          source: p.source ?? null,
          lookup: p.lookup ?? null,
          description: p.description ?? null,
          schema: (p.strictCodec && p.strictCodec.jsonSchema) ?? (p.codec && p.codec.jsonSchema) ?? null,
        }))
      : [];
    methods[endpoint] = {
      service: m.service ?? null,
      mode: m.mode ?? 'unary',
      description: m.description ?? null,
      scope: m.scope ?? null,
      cancelable: m.cancellation !== undefined,
      params,
      result: m.result
        ? { typeSymbol: m.result.typeSymbol ?? null, schema: m.result.jsonSchema ?? null }
        : null,
      sourceLocation: m.sourceLocation ?? null,
    };
  }
  return {
    package: info.package ?? null,
    methods,
    events: Array.isArray(info.events)
      ? info.events.map((e) => ({ name: e.name, mode: e.mode, signature: e.signature, description: e.description ?? null }))
      : [],
  };
}

// ---------- main ----------
const argv = process.argv.slice(2);
const asarPath = argOf(argv, '--asar', DEFAULT_ASAR);
const outPath = argOf(argv, '--out', '');
const only = argOf(argv, '--namespace', '');
const summaryOnly = argv.includes('--summary');

const a = openAsar(asarPath);
const files = walk(a.header, '', []).filter((f) => /\/lib\/typert\.host\.js$/.test(f.path));
if (files.length === 0) throw new Error('no typert.host.js found');

const result = { asar: asarPath, generatedFrom: 'lib/typert.host.js (#createTypertRemoteInfo)', namespaces: {} };
for (const f of files) {
  const pkgDir = f.path.split('/lib/typert.host.js')[0];
  const pkg = pkgDir.split('node_modules/').pop();
  if (only && !pkg.includes(only)) continue;
  let info;
  try { info = summarize(extractRemoteInfo(readEntry(a, f))); }
  catch (error) { result.namespaces[pkg] = { error: String(error && error.message ? error.message : error), file: f.path }; continue; }
  if (info) { info.file = f.path; result.namespaces[pkg] = info; }
}

if (summaryOnly) {
  const rows = [];
  for (const [pkg, info] of Object.entries(result.namespaces)) {
    if (info.error) { console.log(`${pkg}: ERROR ${info.error}`); continue; }
    for (const [endpoint, m] of Object.entries(info.methods)) rows.push(`${pkg}  ${endpoint}  [${m.mode}${m.cancelable ? ',cancelable' : ''}]  ${m.description ?? ''}`);
  }
  rows.sort();
  for (const r of rows) console.log(r);
  console.log(`-- ${rows.length} endpoints in ${Object.keys(result.namespaces).length} namespaces`);
} else if (outPath) {
  fs.writeFileSync(outPath, JSON.stringify(result, null, 1));
  const endpoints = Object.values(result.namespaces).reduce((n, i) => n + Object.keys(i.methods || {}).length, 0);
  console.error(`wrote ${outPath}: ${endpoints} endpoints, ${Object.keys(result.namespaces).length} namespaces, ${fs.statSync(outPath).size} bytes`);
} else {
  process.stdout.write(JSON.stringify(result, null, 1));
}
