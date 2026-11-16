// APK download catalogue: scans one configured directory, exposes only strict
// whitelisted release APKs by name, and hashes them lazily with a cache.
//
// Safety rules (the download page is reachable from the LAN):
//   - only regular files directly inside the configured directory
//   - file names must match ^[A-Za-z0-9._-]+\.apk$ (no separators, no "..")
//   - symlinks are skipped while scanning, and a resolved request must still
//     realpath inside the directory (defence in depth against link escape)
//   - nothing else is ever listed or served
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'

export const DOWNLOAD_PAGE_PATH = '/__relay/download'
export const APK_FILE_PATTERN = /^[A-Za-z0-9._-]+\.apk$/
const RELEASE_NAME_PATTERN = /^dshremote-(?<version>.+)-(?<code>\d+)-arm64-release\.apk$/

const shaCache = new Map()

/** Resolve the APK directory: config > env > repo dist/apk (dev or bundle layout) > ~/.dsh/remote/apk. */
export function resolveDownloadDir (configuredDir, pkgRoot) {
  const candidates = []
  if (configuredDir) candidates.push(path.resolve(String(configuredDir)))
  if (process.env.DSH_REMOTE_APK_DIR) candidates.push(path.resolve(process.env.DSH_REMOTE_APK_DIR))
  if (pkgRoot) {
    // Dev install runs from <repo>/relay (../dist/apk); a bundle installed from
    // <repo>/dist/dsh-remote-relay-<ver> needs ../../dist/apk. Walk a few
    // ancestors so both layouts resolve to the same directory.
    let dir = path.resolve(pkgRoot)
    for (let depth = 0; depth < 4; depth++) {
      dir = path.dirname(dir)
      candidates.push(path.join(dir, 'dist', 'apk'))
    }
  }
  const dshHome = process.env.DSH_HOME ? String(process.env.DSH_HOME) : path.join(os.homedir(), '.dsh')
  candidates.push(path.join(dshHome, 'remote', 'apk'))
  for (const candidate of candidates) {
    try {
      if (fs.statSync(candidate).isDirectory()) return candidate
    } catch {
      // try the next candidate
    }
  }
  return candidates[0]
}

/** Parse `dshremote-<version>-<code>-arm64-release.apk`; undefined for other names. */
export function parseApkName (name) {
  const match = RELEASE_NAME_PATTERN.exec(name)
  if (!match) return undefined
  return {
    version: match.groups.version,
    versionCode: Number(match.groups.code)
  }
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

/**
 * Scan the configured directory (synchronous, cheap stats only) and return
 * entries newest-first. sha256 is filled in by `hashEntries`.
 */
export function scanDownloads (dir) {
  let names
  try {
    names = fs.readdirSync(dir, { withFileTypes: true })
  } catch {
    return []
  }
  const entries = []
  for (const dirent of names) {
    if (!dirent.isFile()) continue // skips symlinks, directories, sockets...
    if (!APK_FILE_PATTERN.test(dirent.name)) continue
    const file = path.join(dir, dirent.name)
    let stat
    try {
      stat = fs.statSync(file)
    } catch {
      continue
    }
    if (!stat.isFile()) continue
    const parsed = parseApkName(dirent.name)
    entries.push({
      name: dirent.name,
      file,
      sizeBytes: stat.size,
      updatedAt: new Date(stat.mtimeMs).toISOString(),
      updatedAtMs: stat.mtimeMs,
      version: parsed?.version ?? dirent.name,
      versionCode: parsed?.versionCode ?? null
    })
  }
  entries.sort((a, b) => {
    if (a.versionCode !== null && b.versionCode !== null && a.versionCode !== b.versionCode) {
      return b.versionCode - a.versionCode
    }
    const byVersion = compareVersions(a.version, b.version)
    if (byVersion !== 0) return byVersion
    return b.updatedAtMs - a.updatedAtMs
  })
  return entries
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
      // keep the entry but surface that the hash could not be computed
    }
    out.push({ ...entry, sha256 })
  }
  return out
}

/** Public (token-free) description used by the page/health/pairing. */
export function summarize (dir, entries) {
  const latest = entries[0] ?? null
  return {
    dir,
    url: DOWNLOAD_PAGE_PATH,
    count: entries.length,
    latest: latest
      ? {
          file: latest.name,
          version: latest.version,
          versionCode: latest.versionCode,
          sizeBytes: latest.sizeBytes,
          sha256: latest.sha256,
          updatedAt: latest.updatedAt,
          path: `${DOWNLOAD_PAGE_PATH}/${latest.name}`
        }
      : null
  }
}

/**
 * Resolve one requested APK name to a safe file path.
 * @returns {{ file: string, entry: object } | null}
 */
export function resolveDownload (dir, requestedName) {
  const name = String(requestedName ?? '')
  if (!APK_FILE_PATTERN.test(name) || name.includes('..')) return null
  const entries = scanDownloads(dir)
  const entry = entries.find((candidate) => candidate.name === name)
  if (!entry) return null
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

/** Async public summary: scans now, hashes only the latest file (cheap per call). */
export async function describeLatest (dir) {
  const entries = scanDownloads(dir)
  if (entries.length === 0) return summarize(dir, [])
  const [latest] = await hashEntries([entries[0]])
  const summary = summarize(dir, [latest])
  summary.count = entries.length
  return summary
}

/** Sync summary for pairing.json (hashes only the latest file). */
export function latestSummarySync (dir) {
  const entries = scanDownloads(dir)
  const latest = entries[0]
  const summary = summarize(dir, latest ? [{ ...latest, sha256: 'pending' }] : [])
  summary.count = entries.length
  if (latest) {
    try {
      summary.latest.sha256 = hashSync(latest.file)
    } catch {
      summary.latest.sha256 = 'unavailable'
    }
  }
  return summary
}

function hashSync (file) {
  const hash = crypto.createHash('sha256')
  const buffer = Buffer.allocUnsafe(1024 * 1024)
  const fd = fs.openSync(file, 'r')
  try {
    for (;;) {
      const read = fs.readSync(fd, buffer, 0, buffer.length, null)
      if (read <= 0) break
      hash.update(buffer.subarray(0, read))
    }
  } finally {
    fs.closeSync(fd)
  }
  return hash.digest('hex')
}
// m5 download feature: LAN APK page/streaming/white-list (auto-scans dist/apk)
