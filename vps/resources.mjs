// Resource catalogue for the public gateway (v0.4.0: category IDs, not kinds).
//
// The gateway used to categorise downloads by *file type* (`apk`, `relay`,
// `docs`, `image`, `misc`), which put every app's APK in one list. Resources are
// now organised by a **category ID** that names an app / release line:
//
//   resources/<id>/<file>      e.g. resources/dsh-remote/dshremote-1.6.0-32-arm64-release.apk
//                                    resources/smartbill/smartbill-1.0.2-3-universal-debugsign.apk
//
// Each upload carries `keepVersions` (see resources-http.mjs): when set, the
// category keeps at most MAX_RETAINED_VERSIONS newest files; otherwise only the
// just-uploaded file survives. Everything a request can reach is still validated
// here (strict file-name pattern, global extension whitelist, realpath
// containment), so the HTTP layer never trusts a URL segment.
//
// Backwards compatibility (downloads only): the legacy APK URL
// `/__gateway/download/<file>.apk` and the legacy kind routes
// (`/__gateway/resources/apk|image|misc/<name>`) search every category for the
// requested name. Uploads through a legacy kind path are rejected by the HTTP
// layer (400 `id-required`). `downloads.mjs` stays a thin compatibility layer.
//
// Zero third-party dependencies; Node >= 22.
import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'

/** Public download page (unchanged URL, now grouped by category ID). */
export const DOWNLOAD_PAGE_PATH = '/__gateway/download'
/** Canonical download URL prefix: /__gateway/resources/<id>/<name>. */
export const RESOURCE_BASE_PATH = '/__gateway/resources'
/** JSON index endpoint (alias `/__gateway/resources/index`). */
export const RESOURCE_INDEX_PATH = '/__gateway/resources'
export const RESOURCE_INDEX_ALIAS = '/__gateway/resources/index'
/** Legacy APK name pattern, kept for callers that imported it. */
export const APK_FILE_PATTERN = /^[A-Za-z0-9._-]+\.apk$/
/** Generic resource file-name pattern (no leading dot, no separators). */
export const FILE_NAME_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._-]*$/
export const MAX_FILE_NAME_LENGTH = 160
/** Category id: lowercase slug used as URL segment and directory name. */
export const CATEGORY_ID_PATTERN = /^[a-z0-9][a-z0-9-]{0,39}$/
/** How many files a category keeps when an upload asks to keep old versions. */
export const MAX_RETAINED_VERSIONS = 3

/**
 * Categories (apps / release lines) in page order. Adding an app is a one-line
 * change here plus a redeploy (same workflow as adding a kind used to be).
 * `flavor: 'apk'` gives the category the APK version column, versionCode-based
 * ordering and the "下载最新版 APK" button; `flavor: 'file'` sorts by mtime.
 */
export const RESOURCE_CATEGORIES = [
  {
    id: 'dsh-remote',
    title: 'DSH Remote 客户端',
    flavor: 'apk',
    description: 'DSH Remote 安卓客户端 Release 签名 APK（arm64-v8a）。安装前请核对 sha256；只安装自己或维护者构建的包。'
  },
  {
    id: 'smartbill',
    title: 'SmartBill（我的账单）客户端',
    flavor: 'apk',
    description: 'SmartBill 我的账单安卓客户端 APK（universal，debugsign 测试签名）。安装前请核对 sha256。'
  },
  {
    id: 'relay',
    title: '桌面 Relay 插件包',
    flavor: 'file',
    description: '桌面端 dsh-remote-relay 插件发布包（zip / tgz / tar），解压后按 INSTALL.md 安装。'
  },
  {
    id: 'docs',
    title: '文档资料',
    flavor: 'file',
    description: '使用说明、部署文档、变更记录等（pdf / md / txt / zip）。'
  }
]

const CATEGORY_BY_ID = new Map(RESOURCE_CATEGORIES.map((category) => [category.id, category]))

/** Compound extensions that must win over the last-dot extension. */
const COMPOUND_EXTENSIONS = ['.tar.gz', '.tar.bz2', '.tar.xz', '.tar.zst']

const CONTENT_TYPES = {
  '.apk': 'application/vnd.android.package-archive',
  '.zip': 'application/zip',
  '.tgz': 'application/gzip',
  '.tar.gz': 'application/gzip',
  '.tar.bz2': 'application/x-bzip2',
  '.tar.xz': 'application/x-xz',
  '.tar.zst': 'application/zstd',
  '.tar': 'application/x-tar',
  '.gz': 'application/gzip',
  '.7z': 'application/x-7z-compressed',
  '.pdf': 'application/pdf',
  '.md': 'text/markdown; charset=utf-8',
  '.txt': 'text/plain; charset=utf-8',
  '.log': 'text/plain; charset=utf-8',
  '.csv': 'text/csv; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.webp': 'image/webp',
  '.gif': 'image/gif',
  '.svg': 'image/svg+xml',
  '.bin': 'application/octet-stream'
}

/** Every extension a resource file may use (union of the historical kinds). */
export const ALLOWED_EXTENSIONS = Object.freeze([...new Set(Object.keys(CONTENT_TYPES))])

/** Extensions that may be served with `content-disposition: inline`. */
const INLINE_SAFE_EXTENSIONS = new Set([
  '.png', '.jpg', '.jpeg', '.webp', '.gif', '.pdf', '.md', '.txt', '.log', '.csv'
])

/**
 * Historical kinds, kept only for compatible downloads. `relay` and `docs` are
 * also category ids; `apk`, `image` and `misc` are download-only aliases that
 * search every category for a matching file name.
 */
export const LEGACY_KINDS = [
  { id: 'apk', extensions: ['.apk'] },
  { id: 'relay', extensions: ['.zip', '.tgz', '.tar.gz', '.tar.bz2', '.tar.xz', '.tar.zst', '.tar'] },
  { id: 'docs', extensions: ['.pdf', '.md', '.txt', '.zip'] },
  { id: 'image', extensions: ['.png', '.jpg', '.jpeg', '.webp', '.gif', '.svg'] },
  { id: 'misc', extensions: ['.json', '.csv', '.txt', '.log', '.zip', '.gz', '.7z', '.bin'] }
]

const LEGACY_KIND_BY_ID = new Map(LEGACY_KINDS.map((kind) => [kind.id, kind]))

/** sha256 cache keyed by `<file>|<mtimeMs>|<sizeBytes>`. */
const shaCache = new Map()

/** @returns {object|undefined} the registered category for an ID. */
export function categoryById (categoryId) {
  return CATEGORY_BY_ID.get(String(categoryId ?? ''))
}

/** @returns {object|undefined} the legacy kind definition (downloads only). */
export function legacyKindById (kindId) {
  return LEGACY_KIND_BY_ID.get(String(kindId ?? ''))
}

/** True when `value` is a syntactically valid category id (registry hit optional). */
export function isCategoryId (value) {
  return CATEGORY_ID_PATTERN.test(String(value ?? ''))
}

/** Lower-case extension of a file name; compound extensions win. */
export function fileNameExtension (name) {
  const lower = String(name ?? '').toLowerCase()
  for (const ext of COMPOUND_EXTENSIONS) {
    if (lower.endsWith(ext)) return ext
  }
  const dot = lower.lastIndexOf('.')
  return dot > 0 ? lower.slice(dot) : ''
}

/** Content type for a file name (never from user input, only from the whitelist). */
export function contentTypeFor (name) {
  return CONTENT_TYPES[fileNameExtension(name)] ?? 'application/octet-stream'
}

/** Whether a file may be shown inline in a browser (SVG and archives never). */
export function isInlineSafe (name) {
  return INLINE_SAFE_EXTENSIONS.has(fileNameExtension(name))
}

/**
 * Strict name validation for one resource file: character set, no traversal,
 * length and a whitelisted extension. This is the only gate before any
 * filesystem access.
 */
export function isSafeResourceName (name) {
  const text = String(name ?? '')
  if (text.length === 0 || text.length > MAX_FILE_NAME_LENGTH) return false
  if (!FILE_NAME_PATTERN.test(text)) return false
  if (text.includes('..')) return false
  if (text === '.' || text === '..') return false
  return ALLOWED_EXTENSIONS.includes(fileNameExtension(text))
}

/**
 * Legacy helper: same as isSafeResourceName plus the historical per-kind
 * extension whitelist. Accepts a legacy kind id (string) or a category object.
 */
export function isSafeFileName (name, kind) {
  if (!isSafeResourceName(name)) return false
  const legacy = typeof kind === 'string' ? legacyKindById(kind) : null
  if (legacy) return legacy.extensions.includes(fileNameExtension(name))
  const category = typeof kind === 'string' ? categoryById(kind) : kind
  if (category && Array.isArray(category.extensions)) {
    return category.extensions.includes(fileNameExtension(name))
  }
  return category !== undefined && category !== null
}

/**
 * Directory for one category: `<RESOURCE_DIR>/<id>`. The legacy APK_DIR is only
 * used as a download fallback (see resolveLegacyResource), never for new writes.
 */
export function categoryDir (categoryId, config = {}) {
  const category = categoryById(categoryId)
  if (!category || !config.resourceDir) return null
  return path.join(config.resourceDir, category.id)
}

/** Canonical relative download URL for a resource. */
export function resourceFileUrl (categoryId, name) {
  return `${RESOURCE_BASE_PATH}/${encodeURIComponent(String(categoryId))}/${encodeURIComponent(String(name))}`
}

/** Legacy APK URL (still supported, resolves across categories). */
export function legacyApkUrl (name) {
  return `${DOWNLOAD_PAGE_PATH}/${encodeURIComponent(String(name))}`
}

/** Parse `dshremote-<version>-<code>-arm64-release.apk`; undefined otherwise. */
export function parseApkName (name) {
  const match = /^dshremote-(?<version>.+)-(?<code>\d+)-arm64-release\.apk$/.exec(String(name ?? ''))
  if (!match) return undefined
  return { version: match.groups.version, versionCode: Number(match.groups.code) }
}

function compareVersions (a, b) {
  const left = String(a ?? '').split('.').map((part) => Number.parseInt(part, 10))
  const right = String(b ?? '').split('.').map((part) => Number.parseInt(part, 10))
  const length = Math.max(left.length, right.length)
  for (let i = 0; i < length; i++) {
    const l = Number.isFinite(left[i]) ? left[i] : 0
    const r = Number.isFinite(right[i]) ? right[i] : 0
    if (l !== r) return r - l
  }
  return 0
}

/** APK ordering: versionCode desc -> version desc -> mtime desc (historical). */
function compareApkEntries (a, b) {
  if (a.versionCode !== null && b.versionCode !== null && a.versionCode !== b.versionCode) {
    return b.versionCode - a.versionCode
  }
  const byVersion = compareVersions(a.version, b.version)
  if (byVersion !== 0) return byVersion
  return b.updatedAtMs - a.updatedAtMs
}

/** Newest-first ordering inside a category. */
function sortEntries (category, entries) {
  if (category.flavor === 'apk') {
    entries.sort(compareApkEntries)
    return entries
  }
  entries.sort((a, b) => {
    if (b.updatedAtMs !== a.updatedAtMs) return b.updatedAtMs - a.updatedAtMs
    return a.name.localeCompare(b.name)
  })
  return entries
}

function entryFor (category, dir, name, stat) {
  const extension = fileNameExtension(name)
  const entry = {
    id: category.id,
    category: category.id,
    name,
    file: path.join(dir, name),
    extension,
    contentType: contentTypeFor(name),
    sizeBytes: stat.size,
    updatedAt: new Date(stat.mtimeMs).toISOString(),
    updatedAtMs: stat.mtimeMs,
    url: resourceFileUrl(category.id, name)
  }
  if (category.flavor === 'apk') {
    const parsed = parseApkName(name)
    entry.version = parsed?.version ?? name
    entry.versionCode = parsed?.versionCode ?? null
    entry.legacyUrl = legacyApkUrl(name)
  }
  return entry
}

/** Scan one category directory (cheap stats only; sha256 comes from hashEntries). */
export function scanCategory (categoryId, dir) {
  const category = categoryById(categoryId)
  if (!category || !dir) return []
  let dirents
  try {
    dirents = fs.readdirSync(dir, { withFileTypes: true })
  } catch {
    return []
  }
  const entries = []
  for (const dirent of dirents) {
    if (!dirent.isFile()) continue // skips symlinks, directories, sockets...
    if (!isSafeResourceName(dirent.name)) continue
    const file = path.join(dir, dirent.name)
    let stat
    try {
      stat = fs.statSync(file)
    } catch {
      continue
    }
    if (!stat.isFile()) continue
    entries.push(entryFor(category, dir, dirent.name, stat))
  }
  return sortEntries(category, entries)
}

/**
 * Scan every registered category and group the result (newest-first inside).
 * @returns {Array<{id,title,description,flavor,dir,count,latest,files}>}
 */
export function scanCategories (config = {}) {
  return RESOURCE_CATEGORIES.map((category) => {
    const dir = categoryDir(category.id, config)
    const files = scanCategory(category.id, dir)
    return {
      id: category.id,
      kind: category.id, // compatibility alias for older scripts/tests
      title: category.title,
      description: category.description,
      flavor: category.flavor,
      dir,
      count: files.length,
      latest: files[0] ?? null,
      files
    }
  })
}

async function sha256Of (entry) {
  const key = `${entry.file}|${entry.updatedAtMs}|${entry.sizeBytes}`
  const cached = shaCache.get(key)
  if (cached) return cached
  const hash = crypto.createHash('sha256')
  await new Promise((resolve, reject) => {
    const stream = fs.createReadStream(entry.file)
    stream.on('data', (chunk) => hash.update(chunk))
    stream.on('error', reject)
    stream.on('end', resolve)
  })
  const digest = hash.digest('hex')
  shaCache.set(key, digest)
  return digest
}

/** Fill sha256 for every entry (cached per path+mtime+size). */
export async function hashEntries (entries) {
  const out = []
  for (const entry of entries) {
    let sha256 = 'unavailable'
    try {
      sha256 = await sha256Of(entry)
    } catch {
      // keep the entry downloadable even if hashing failed
    }
    out.push({ ...entry, sha256 })
  }
  return out
}

/** Hash a full category scan (files + latest get sha256). */
export async function hashCategories (categories) {
  const out = []
  for (const category of categories) {
    const files = await hashEntries(category.files)
    out.push({ ...category, files, latest: files[0] ?? null, count: files.length })
  }
  return out
}

/**
 * Resolve one requested resource to a safe real path, or null when the name is
 * not currently whitelisted by the scan (or escapes the category directory).
 * @returns {{ file: string, entry: object } | null}
 */
export function resolveResource (categoryId, dir, requestedName) {
  const category = categoryById(categoryId)
  if (!category || !dir) return null
  const name = String(requestedName ?? '')
  if (!isSafeResourceName(name)) return null
  const entry = scanCategory(category.id, dir).find((candidate) => candidate.name === name)
  if (!entry) return null
  return containEntry(dir, entry)
}

/** Realpath containment check for a scanned entry (shared by legacy resolution). */
function containEntry (dir, entry) {
  let realDir
  let realFile
  try {
    realDir = fs.realpathSync(dir)
    realFile = fs.realpathSync(entry.file)
  } catch {
    return null
  }
  const prefix = realDir.endsWith(path.sep) ? realDir : `${realDir}${path.sep}`
  if (!realFile.startsWith(prefix)) return null
  return { file: realFile, entry }
}

/** Drop cached hashes for a path (after upload/delete/prune). */
export function invalidateHash (filePath) {
  const prefix = `${String(filePath ?? '')}|`
  for (const key of [...shaCache.keys()]) {
    if (key.startsWith(prefix)) shaCache.delete(key)
  }
}

// ---------------------------------------------------------------------------
// Retention (keepVersions upload option)
// ---------------------------------------------------------------------------

/**
 * List whitelisted regular files of a directory ordered by *upload recency*
 * (mtime desc, name asc), which is what `keepVersions` prunes by.
 */
function listByMtime (dir) {
  let dirents
  try {
    dirents = fs.readdirSync(dir, { withFileTypes: true })
  } catch {
    return []
  }
  const files = []
  for (const dirent of dirents) {
    if (!dirent.isFile()) continue
    if (!isSafeResourceName(dirent.name)) continue
    const file = path.join(dir, dirent.name)
    try {
      const stat = fs.statSync(file)
      if (!stat.isFile()) continue
      files.push({ name: dirent.name, file, mtimeMs: stat.mtimeMs })
    } catch {
      continue
    }
  }
  files.sort((a, b) => {
    if (b.mtimeMs !== a.mtimeMs) return b.mtimeMs - a.mtimeMs
    return a.name.localeCompare(b.name)
  })
  return files
}

/**
 * Apply the retention policy to one category directory after an upload.
 *
 * - `keepVersions: true` -> keep the MAX_RETAINED_VERSIONS newest files;
 * - `keepVersions: false` -> keep only the newest file (the upload itself).
 *
 * Deletion is best effort: a file that cannot be removed is reported in
 * `errors` but never fails the upload (the upload itself already succeeded).
 * @returns {{ retained: string[], pruned: string[], errors: Array<{name:string,message:string}> }}
 */
export function applyRetention (categoryId, dir, keepVersions) {
  const category = categoryById(categoryId)
  if (!category || !dir) return { retained: [], pruned: [], errors: [] }
  const limit = keepVersions === true ? MAX_RETAINED_VERSIONS : 1
  const files = listByMtime(dir)
  const retained = files.slice(0, limit)
  const pruned = []
  const errors = []
  for (const stale of files.slice(limit)) {
    try {
      fs.unlinkSync(stale.file)
      invalidateHash(stale.file)
      pruned.push(stale.name)
    } catch (error) {
      errors.push({ name: stale.name, message: String(error?.message ?? error) })
    }
  }
  return { retained: retained.map((file) => file.name), pruned, errors }
}

// ---------------------------------------------------------------------------
// Legacy APK catalogue API (download compatibility only)
// ---------------------------------------------------------------------------

/** Scan a legacy APK directory (e.g. APK_DIR) with the historical APK ordering. */
export function scanApkDir (dir) {
  if (!dir) return []
  let dirents
  try {
    dirents = fs.readdirSync(dir, { withFileTypes: true })
  } catch {
    return []
  }
  const category = { id: 'apk', flavor: 'apk' }
  const entries = []
  for (const dirent of dirents) {
    if (!dirent.isFile()) continue
    if (!isSafeFileName(dirent.name, 'apk')) continue
    const file = path.join(dir, dirent.name)
    let stat
    try {
      stat = fs.statSync(file)
    } catch {
      continue
    }
    if (!stat.isFile()) continue
    entries.push(entryFor(category, dir, dirent.name, stat))
  }
  return sortEntries(category, entries)
}

/** Every APK file in every category, in "latest first" order.
 *
 * The extension whitelist is global, so `.apk` files may exist outside the
 * APK-flavoured categories too; identity/index APK counts include them (matching
 * the documented "all .apk files" semantics) and normalise the version fields.
 */
export function scanCategoryApks (config = {}) {
  const entries = []
  for (const category of scanCategories(config)) {
    for (const entry of category.files) {
      if (fileNameExtension(entry.name) !== '.apk') continue
      const parsed = parseApkName(entry.name)
      entries.push({
        ...entry,
        version: entry.version ?? parsed?.version ?? entry.name,
        versionCode: entry.versionCode ?? parsed?.versionCode ?? null,
        legacyUrl: entry.legacyUrl ?? legacyApkUrl(entry.name)
      })
    }
  }
  return entries.sort(compareApkEntries)
}

/**
 * Resolve a legacy download path (kind-based URL or `/__gateway/download/<apk>`)
 * by file name: category directories first (registry order), then APK_DIR for
 * the `apk` kind.
 * @returns {{ file: string, entry: object } | null}
 */
export function resolveLegacyResource (legacyKindId, config = {}, requestedName) {
  const kind = legacyKindById(legacyKindId)
  if (!kind) return null
  const name = String(requestedName ?? '')
  if (!isSafeFileName(name, kind)) return null
  for (const category of RESOURCE_CATEGORIES) {
    const dir = categoryDir(category.id, config)
    if (!dir) continue
    const entry = scanCategory(category.id, dir).find((candidate) => candidate.name === name)
    if (entry) return containEntry(dir, entry)
  }
  if (kind.id === 'apk' && config.apkDir) {
    const entry = scanApkDir(config.apkDir).find((candidate) => candidate.name === name)
    if (entry) return containEntry(config.apkDir, entry)
  }
  return null
}

/** Legacy: scan a whole APK directory (same names/order as v0.1.0). */
export function scanDownloads (dir) {
  return scanApkDir(dir)
}

/** Legacy: resolve one APK name inside a directory. */
export function resolveDownload (dir, requestedName) {
  const entry = scanApkDir(dir).find((candidate) => candidate.name === requestedName)
  return entry ? containEntry(dir, entry) : null
}

function apkSummary (entries) {
  const latest = entries[0] ?? null
  return {
    page: DOWNLOAD_PAGE_PATH,
    count: entries.length,
    latest: latest
      ? { file: latest.name, version: latest.version, versionCode: latest.versionCode, sizeBytes: latest.sizeBytes }
      : null
  }
}

/**
 * Secret-free APK summary used by identity/health. Accepts either a config
 * object (counts the APK-flavoured categories) or a directory string (the
 * historical signature, e.g. for the legacy APK_DIR).
 */
export function summarizeDownloads (configOrDir = {}) {
  if (typeof configOrDir === 'string') return apkSummary(scanApkDir(configOrDir))
  return apkSummary(scanCategoryApks(configOrDir))
}

/**
 * Secret-free summary for `/__gateway/identity` (scan only, no hashing, no
 * absolute directory paths).
 */
export function summarizeResources (config = {}) {
  const categories = scanCategories(config)
  const total = categories.reduce((sum, category) => sum + category.count, 0)
  const byId = Object.fromEntries(categories.map((category) => [category.id, category.count]))
  const apkEntries = scanCategoryApks(config)
  const apkLatest = apkEntries[0] ?? null
  return {
    page: DOWNLOAD_PAGE_PATH,
    index: RESOURCE_INDEX_PATH,
    total,
    byId,
    categories: categories.map((category) => ({
      id: category.id,
      title: category.title,
      count: category.count,
      latest: category.latest
        ? {
            file: category.latest.name,
            sizeBytes: category.latest.sizeBytes,
            updatedAt: category.latest.updatedAt,
            url: category.latest.url,
            ...(category.flavor === 'apk'
              ? { version: category.latest.version, versionCode: category.latest.versionCode }
              : {})
          }
        : null
    })),
    apk: {
      count: apkEntries.length,
      latest: apkLatest
        ? {
            file: apkLatest.name,
            version: apkLatest.version,
            versionCode: apkLatest.versionCode,
            sizeBytes: apkLatest.sizeBytes
          }
        : null
    }
  }
}
