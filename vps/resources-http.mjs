// HTTP surface of the gateway resource centre (v0.4.0: category IDs).
//
// Owns these public routes (all reachable from the open internet):
//
//   GET|HEAD /__gateway/download                       resource page, grouped by category ID
//   GET|HEAD /__gateway/download/<file>.apk            legacy APK download (kept)
//   GET|HEAD /__gateway/resources[?id=<id>]            JSON index (secret-free)
//   GET|HEAD /__gateway/resources/index                alias of the JSON index
//   GET|HEAD /__gateway/resources/<id>/<name>          streamed download (Range/ETag)
//   PUT|POST /__gateway/resources/<id>/<name>          upload (Bearer UPLOAD_TOKEN)
//   DELETE   /__gateway/resources/<id>/<name>          delete (Bearer UPLOAD_TOKEN)
//
// Uploads take `?keepVersions=0|1`: 1 keeps the MAX_RETAINED_VERSIONS newest
// files of the category, 0 (default) keeps only the uploaded version. Legacy
// kind paths (`apk`, `image`, `misc`) are download-only aliases; writing through
// them answers 400 `id-required`.
//
// Everything a request can reach is first validated by resources.mjs (strict
// file-name pattern, extension whitelist, realpath containment), so this module
// never has to trust a URL segment. Upload/delete are disabled (404) unless
// UPLOAD_TOKEN is configured; a missing/wrong token is 403.
//
// The module is deliberately free of gateway-global state so `node --test` can
// mount `handleResourcesRequest` on a plain http server with a temp directory.
import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'

import {
  DOWNLOAD_PAGE_PATH,
  RESOURCE_BASE_PATH,
  RESOURCE_CATEGORIES,
  RESOURCE_INDEX_ALIAS,
  RESOURCE_INDEX_PATH,
  applyRetention,
  categoryById,
  categoryDir,
  hashCategories,
  hashEntries,
  invalidateHash,
  isInlineSafe,
  isSafeResourceName,
  legacyKindById,
  resolveLegacyResource,
  resolveResource,
  scanCategories,
  summarizeDownloads
} from './resources.mjs'
import { downloadPageHeaders, renderDownloadPage } from './download-page.mjs'
import { timingSafeEquals } from './proto.mjs'

/** Public https base URL derived from the gateway's public (wss) URL. */
export function publicBaseUrl (config = {}) {
  const raw = String(config.publicUrl ?? '').trim()
  if (raw === '') return ''
  if (/^https?:\/\//i.test(raw)) return raw.replace(/\/+$/, '')
  if (/^wss?:\/\//i.test(raw)) return raw.replace(/^ws/i, 'http').replace(/\/+$/, '')
  return raw.replace(/\/+$/, '')
}

function writeJson (res, status, value, extraHeaders = {}) {
  const body = Buffer.from(`${JSON.stringify(value, null, 2)}\n`, 'utf8')
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'cache-control': 'no-store',
    'x-content-type-options': 'nosniff',
    'content-length': body.length,
    ...extraHeaders
  })
  res.end(body)
}

function writeMethodNotAllowed (res, allow) {
  writeJson(res, 405, {
    ok: false,
    error: 'method-not-allowed',
    message: `method not allowed; use ${allow}`,
    allow
  }, { allow })
}

function writeNotFound (res) {
  writeJson(res, 404, { ok: false, error: 'not-found', message: '资源不存在' })
}

function writeAuthError (res, auth) {
  if (auth.status === 404) {
    writeJson(res, 404, { ok: false, error: auth.error, message: auth.message })
    return
  }
  writeJson(res, auth.status, { ok: false, error: auth.error, message: auth.message }, {
    'www-authenticate': 'Bearer realm="gateway-resources"'
  })
}

/**
 * Upload/delete authentication.
 * - UPLOAD_TOKEN not configured -> 404 (endpoint disabled, documented choice)
 * - missing/wrong bearer token  -> 403
 */
function authorizeWrite (req, config) {
  if (!config.uploadToken) {
    return {
      ok: false,
      status: 404,
      error: 'uploads-disabled',
      message: '上传/删除接口未启用：服务端未配置 UPLOAD_TOKEN'
    }
  }
  const header = String(req.headers.authorization ?? '').trim()
  const match = /^Bearer\s+(.+)$/i.exec(header)
  if (!match) {
    return { ok: false, status: 403, error: 'upload-forbidden', message: '需要 Authorization: Bearer <UPLOAD_TOKEN>' }
  }
  if (!timingSafeEquals(match[1].trim(), config.uploadToken)) {
    return { ok: false, status: 403, error: 'upload-forbidden', message: '上传令牌无效' }
  }
  return { ok: true }
}

/** Parse `?keepVersions=`; absent means "do not keep old versions". */
export function parseKeepVersions (url) {
  if (!url.searchParams.has('keepVersions')) return { ok: true, value: false }
  const raw = String(url.searchParams.get('keepVersions') ?? '').trim().toLowerCase()
  if (['1', 'true', 'yes', 'on'].includes(raw)) return { ok: true, value: true }
  if (['0', 'false', 'no', 'off'].includes(raw)) return { ok: true, value: false }
  return { ok: false }
}

function fileJson (file, base) {
  const item = {
    id: file.id,
    name: file.name,
    sizeBytes: file.sizeBytes,
    sha256: file.sha256 ?? 'unavailable',
    updatedAt: file.updatedAt,
    contentType: file.contentType,
    path: file.url,
    url: base ? `${base}${file.url}` : file.url
  }
  if (file.version !== undefined) item.version = file.version
  if (file.versionCode !== undefined) item.versionCode = file.versionCode
  if (file.legacyUrl !== undefined) item.legacyUrl = file.legacyUrl
  return item
}

function indexDocument (categories, ctx) {
  const base = publicBaseUrl(ctx.config)
  const byId = {}
  for (const category of categories) byId[category.id] = category.files.length
  const total = categories.reduce((sum, category) => sum + category.files.length, 0)
  const apk = summarizeDownloads(ctx.config)
  return {
    ok: true,
    generatedAt: new Date().toISOString(),
    gateway: {
      name: ctx.gateway.name,
      version: ctx.gateway.version,
      url: base,
      fp: ctx.gateway.fp
    },
    page: DOWNLOAD_PAGE_PATH,
    index: RESOURCE_INDEX_PATH,
    downloadBase: RESOURCE_BASE_PATH,
    total,
    counts: { total, byId },
    categories: categories.map((category) => ({
      id: category.id,
      title: category.title,
      description: category.description,
      flavor: category.flavor,
      count: category.files.length,
      latest: category.latest ? fileJson(category.latest, base) : null,
      files: category.files.map((file) => fileJson(file, base))
    })),
    apk: {
      count: apk.count,
      latest: apk.latest
    }
  }
}

// ---------------------------------------------------------------------------
// GET /__gateway/download — the public page, grouped by category
// ---------------------------------------------------------------------------

async function handleDownloadPage (req, res, ctx) {
  if (req.method !== 'GET' && req.method !== 'HEAD') {
    writeMethodNotAllowed(res, 'GET, HEAD')
    return
  }
  const config = ctx.config
  const categories = await hashCategories(scanCategories(config))
  const html = Buffer.from(renderDownloadPage({
    categories,
    dir: config.apkDir,
    resourceDir: config.resourceDir,
    gatewayVersion: ctx.gateway.version,
    gatewayName: ctx.gateway.name,
    fingerprint: ctx.gateway.fp,
    baseUrl: publicBaseUrl(config),
    tunnels: ctx.gateway.tunnels ?? 0,
    generatedAt: new Date().toISOString()
  }), 'utf8')
  res.writeHead(200, { ...downloadPageHeaders('text/html; charset=utf-8'), 'content-length': html.length })
  res.end(req.method === 'HEAD' ? undefined : html)
}

// ---------------------------------------------------------------------------
// GET /__gateway/resources — JSON index
// ---------------------------------------------------------------------------

async function handleIndex (req, res, ctx, url) {
  if (req.method !== 'GET' && req.method !== 'HEAD') {
    writeMethodNotAllowed(res, 'GET, HEAD')
    return
  }
  const idFilter = url.searchParams.get('id')
  let categories = scanCategories(ctx.config)
  if (idFilter !== null && idFilter !== '') {
    const category = categoryById(idFilter)
    if (!category) {
      writeJson(res, 400, {
        ok: false,
        error: 'unknown-id',
        message: `unknown resource category id ${JSON.stringify(idFilter)}`,
        allowedIds: RESOURCE_CATEGORIES.map((item) => item.id)
      })
      return
    }
    categories = categories.filter((item) => item.id === category.id)
  }
  const hashed = await hashCategories(categories)
  const document = indexDocument(hashed, ctx)
  const body = Buffer.from(`${JSON.stringify(document, null, 2)}\n`, 'utf8')
  res.writeHead(200, {
    'content-type': 'application/json; charset=utf-8',
    'cache-control': 'no-store',
    'x-content-type-options': 'nosniff',
    'content-length': body.length
  })
  res.end(req.method === 'HEAD' ? undefined : body)
}

// ---------------------------------------------------------------------------
// File download (canonical + legacy routes)
// ---------------------------------------------------------------------------

function contentDisposition (name, inline) {
  const type = inline ? 'inline' : 'attachment'
  return `${type}; filename="${name}"; filename*=UTF-8''${encodeURIComponent(name)}`
}

function parseRangeHeader (header, size) {
  const text = String(header ?? '').trim()
  if (!/^bytes=/i.test(text)) return null
  const spec = text.slice(6).trim()
  if (spec.includes(',')) return null // multiple ranges: serve the full file instead
  const match = /^(\d*)-(\d*)$/.exec(spec)
  if (!match) return { unsatisfiable: true }
  if (match[1] === '' && match[2] === '') return { unsatisfiable: true }
  if (size <= 0) return { unsatisfiable: true }
  if (match[1] === '') {
    const suffix = Number(match[2])
    if (!Number.isFinite(suffix) || suffix <= 0) return { unsatisfiable: true }
    return suffix >= size ? { start: 0, end: size - 1 } : { start: size - suffix, end: size - 1 }
  }
  const start = Number(match[1])
  let end = match[2] === '' ? size - 1 : Number(match[2])
  if (!Number.isFinite(start) || !Number.isFinite(end) || start > end || start >= size) {
    return { unsatisfiable: true }
  }
  if (end >= size) end = size - 1
  return { start, end }
}

function streamFile (file, res, range) {
  const stream = range
    ? fs.createReadStream(file, { start: range.start, end: range.end })
    : fs.createReadStream(file)
  stream.on('error', () => {
    try { res.destroy() } catch {}
  })
  res.on('close', () => {
    try { stream.destroy() } catch {}
  })
  stream.pipe(res)
}

async function sendResolvedFile (req, res, url, resolved) {
  const [entry] = await hashEntries([resolved.entry])
  const size = entry.sizeBytes
  const sha256 = entry.sha256 ?? 'unavailable'
  const etag = sha256 !== 'unavailable' ? `"${sha256}"` : null
  const inline = url.searchParams.get('inline') === '1' && isInlineSafe(entry.name)
  const headers = {
    'content-type': entry.contentType,
    'content-disposition': contentDisposition(entry.name, inline),
    'content-length': String(size),
    'accept-ranges': 'bytes',
    'x-content-sha256': sha256,
    'cache-control': 'no-store',
    'referrer-policy': 'no-referrer',
    'x-content-type-options': 'nosniff'
  }
  if (etag) headers.etag = etag
  if (inline) headers['content-security-policy'] = "default-src 'none'; sandbox"

  if (req.method === 'HEAD') {
    res.writeHead(200, headers)
    res.end()
    return
  }
  if (etag && String(req.headers['if-none-match'] ?? '').trim() === etag) {
    res.writeHead(304, { etag, 'cache-control': 'no-store' })
    res.end()
    return
  }
  const range = parseRangeHeader(req.headers.range, size)
  if (range && range.unsatisfiable) {
    res.writeHead(416, {
      ...headers,
      'content-range': `bytes */${size}`,
      'content-length': '0'
    })
    res.end()
    return
  }
  if (range) {
    res.writeHead(206, {
      ...headers,
      'content-range': `bytes ${range.start}-${range.end}/${size}`,
      'content-length': String(range.end - range.start + 1)
    })
    streamFile(resolved.file, res, range)
    return
  }
  res.writeHead(200, headers)
  streamFile(resolved.file, res, null)
}

async function sendResourceFile (req, res, ctx, url, categoryId, dir, name) {
  if (req.method !== 'GET' && req.method !== 'HEAD') {
    writeMethodNotAllowed(res, 'GET, HEAD, PUT, POST, DELETE')
    return
  }
  const resolved = resolveResource(categoryId, dir, name)
  if (!resolved) {
    writeNotFound(res)
    return
  }
  await sendResolvedFile(req, res, url, resolved)
}

async function handleLegacyApkDownload (req, res, ctx, url, pathname) {
  if (req.method !== 'GET' && req.method !== 'HEAD') {
    writeMethodNotAllowed(res, 'GET, HEAD')
    return
  }
  let name = ''
  try {
    name = decodeURIComponent(pathname.slice(DOWNLOAD_PAGE_PATH.length + 1))
  } catch {
    name = ''
  }
  const resolved = resolveLegacyResource('apk', ctx.config, name)
  if (!resolved) {
    writeNotFound(res)
    return
  }
  await sendResolvedFile(req, res, url, resolved)
}

// ---------------------------------------------------------------------------
// Upload / delete
// ---------------------------------------------------------------------------

/**
 * Stream the request body into `tempPath`, hashing as we go, never buffering
 * more than one chunk. Returns a plain result object instead of throwing so the
 * caller can always answer with the right status code.
 */
function receiveToFile (req, tempPath, maxBytes) {
  return new Promise((resolve) => {
    const hash = crypto.createHash('sha256')
    const out = fs.createWriteStream(tempPath, { flags: 'wx', mode: 0o644 })
    let size = 0
    let settled = false
    const finish = (value) => {
      if (settled) return
      settled = true
      resolve(value)
    }
    out.on('error', (error) => finish({ ok: false, reason: 'write-error', error }))
    req.on('error', () => finish({ ok: false, reason: 'aborted' }))
    req.on('aborted', () => finish({ ok: false, reason: 'aborted' }))
    req.on('data', (chunk) => {
      if (settled) return
      size += chunk.length
      if (size > maxBytes) {
        finish({ ok: false, reason: 'too-large', size })
        try { out.destroy() } catch {}
        return
      }
      hash.update(chunk)
      if (!out.write(chunk)) req.pause()
    })
    out.on('drain', () => {
      if (!settled) req.resume()
    })
    req.on('end', () => {
      if (settled) return
      out.end(() => {
        if (settled) return
        finish({ ok: true, size, sha256: hash.digest('hex') })
      })
    })
  })
}

async function removeIfExists (file) {
  try {
    await fs.promises.unlink(file)
  } catch {
    // best effort
  }
}

function writeTooLarge (res, maxBytes) {
  res.writeHead(413, {
    'content-type': 'application/json; charset=utf-8',
    'cache-control': 'no-store',
    connection: 'close'
  })
  res.end(JSON.stringify({ ok: false, error: 'upload-too-large', message: `请求体超过上限 ${maxBytes} 字节`, maxUploadBytes: maxBytes }))
}

async function handleUpload (req, res, ctx, url, categoryId, dir, name) {
  const auth = authorizeWrite(req, ctx.config)
  if (!auth.ok) {
    writeAuthError(res, auth)
    return
  }
  const category = categoryById(categoryId)
  if (!category) {
    writeNotFound(res)
    return
  }
  const keep = parseKeepVersions(url)
  if (!keep.ok) {
    writeJson(res, 400, {
      ok: false,
      error: 'invalid-keep-versions',
      message: 'keepVersions 只接受 1/true/yes/on 或 0/false/no/off',
      allowedValues: ['0', '1']
    })
    return
  }
  if (!isSafeResourceName(name)) {
    writeJson(res, 400, {
      ok: false,
      error: 'invalid-name',
      message: `文件名不合法：需匹配 ^[A-Za-z0-9][A-Za-z0-9._-]*$、不含 ".."、扩展名需在白名单内`
    })
    return
  }
  const finalPath = path.join(dir, name)
  const maxBytes = ctx.config.maxUploadBytes
  const declared = Number(req.headers['content-length'])
  if (Number.isFinite(declared) && declared > maxBytes) {
    writeTooLarge(res, maxBytes)
    res.on('finish', () => {
      try { req.destroy() } catch {}
    })
    return
  }

  try {
    await fs.promises.mkdir(dir, { recursive: true, mode: 0o755 })
  } catch (error) {
    ctx.log?.('error', `resource upload cannot create ${dir}: ${error?.message ?? error}`)
    writeJson(res, 500, { ok: false, error: 'storage-unavailable', message: '服务端资源目录不可写' })
    return
  }

  const replaced = fs.existsSync(finalPath)
  const tempPath = path.join(dir, `.upload-${crypto.randomBytes(8).toString('hex')}-${name}`)
  const received = await receiveToFile(req, tempPath, maxBytes)
  if (!received.ok) {
    await removeIfExists(tempPath)
    if (received.reason === 'too-large') {
      writeTooLarge(res, maxBytes)
      // Drain/discard whatever the client is still sending, then let the socket
      // close on its own; never buffer it.
      try { req.resume() } catch {}
      return
    }
    if (received.reason === 'aborted') return // client is gone; nothing to answer
    ctx.log?.('error', `resource upload write failed for ${categoryId}/${name}: ${received.error?.message ?? 'unknown'}`)
    writeJson(res, 500, { ok: false, error: 'storage-error', message: '写入临时文件失败' })
    return
  }

  try {
    const handle = await fs.promises.open(tempPath, 'r+')
    try { await handle.sync() } finally { await handle.close() }
    await fs.promises.rename(tempPath, finalPath)
  } catch (error) {
    await removeIfExists(tempPath)
    ctx.log?.('error', `resource upload rename failed for ${categoryId}/${name}: ${error?.message ?? error}`)
    writeJson(res, 500, { ok: false, error: 'storage-error', message: '临时文件落盘失败' })
    return
  }
  invalidateHash(finalPath)

  // Retention is part of the upload contract: keep the newest 3 versions, or
  // only the uploaded one. Failures never fail the upload itself.
  const retention = applyRetention(categoryId, dir, keep.value)
  for (const error of retention.errors) {
    ctx.log?.('error', `resource prune failed for ${categoryId}/${error.name}: ${error.message}`)
  }

  const base = publicBaseUrl(ctx.config)
  const urlPath = `${RESOURCE_BASE_PATH}/${encodeURIComponent(categoryId)}/${encodeURIComponent(name)}`
  ctx.log?.(
    'info',
    `resource uploaded: id=${categoryId} name=${name} size=${received.size} ` +
    `keepVersions=${keep.value} replaced=${replaced} pruned=${retention.pruned.length}`
  )
  const payload = {
    ok: true,
    id: categoryId,
    name,
    sizeBytes: received.size,
    sha256: received.sha256,
    keepVersions: keep.value,
    replaced,
    retained: retention.retained,
    pruned: retention.pruned,
    path: urlPath,
    url: base ? `${base}${urlPath}` : urlPath
  }
  if (retention.errors.length > 0) payload.pruneErrors = retention.errors
  writeJson(res, replaced ? 200 : 201, payload, { 'x-content-sha256': received.sha256 })
}

async function handleDelete (req, res, ctx, categoryId, dir, name) {
  const auth = authorizeWrite(req, ctx.config)
  if (!auth.ok) {
    writeAuthError(res, auth)
    return
  }
  const resolved = resolveResource(categoryId, dir, name)
  if (!resolved) {
    writeNotFound(res)
    return
  }
  try {
    await fs.promises.unlink(resolved.file)
  } catch (error) {
    if (error && error.code === 'ENOENT') {
      writeNotFound(res)
      return
    }
    ctx.log?.('error', `resource delete failed for ${categoryId}/${name}: ${error?.message ?? error}`)
    writeJson(res, 500, { ok: false, error: 'storage-error', message: '删除失败' })
    return
  }
  invalidateHash(resolved.file)
  ctx.log?.('info', `resource deleted: id=${categoryId} name=${name}`)
  writeJson(res, 200, { ok: true, id: categoryId, name, deleted: true })
}

/** A write through a retired legacy kind path: tell the caller the new API. */
function writeIdRequired (res, legacyKindId) {
  writeJson(res, 400, {
    ok: false,
    error: 'id-required',
    message:
      `上传接口已改为 /__gateway/resources/<id>/<name>?keepVersions=0|1` +
      `（id: ${RESOURCE_CATEGORIES.map((category) => category.id).join('|')}）；` +
      `旧的 kind 路径 ${JSON.stringify(legacyKindId)} 仅保留下载兼容`
  })
}

// ---------------------------------------------------------------------------
// Canonical resource route: /__gateway/resources/<id>/<name>
// ---------------------------------------------------------------------------

function parseResourcePath (pathname) {
  const rest = pathname.slice(RESOURCE_BASE_PATH.length + 1)
  if (rest === '') return null
  const slash = rest.indexOf('/')
  if (slash <= 0) return null
  try {
    return {
      id: decodeURIComponent(rest.slice(0, slash)),
      name: decodeURIComponent(rest.slice(slash + 1))
    }
  } catch {
    return null
  }
}

async function handleResourceRequest (req, res, ctx, url) {
  const parsed = parseResourcePath(url.pathname)
  if (!parsed) {
    writeNotFound(res)
    return
  }
  const category = categoryById(parsed.id)
  if (category) {
    const dir = categoryDir(category.id, ctx.config)
    if (!dir) {
      writeNotFound(res)
      return
    }
    switch (req.method) {
      case 'GET':
      case 'HEAD':
        await sendResourceFile(req, res, ctx, url, category.id, dir, parsed.name)
        return
      case 'PUT':
      case 'POST':
        await handleUpload(req, res, ctx, url, category.id, dir, parsed.name)
        return
      case 'DELETE':
        await handleDelete(req, res, ctx, category.id, dir, parsed.name)
        return
      default:
        writeMethodNotAllowed(res, 'GET, HEAD, PUT, POST, DELETE')
    }
    return
  }

  const legacy = legacyKindById(parsed.id)
  if (legacy) {
    if (req.method === 'GET' || req.method === 'HEAD') {
      const resolved = resolveLegacyResource(legacy.id, ctx.config, parsed.name)
      if (!resolved) {
        writeNotFound(res)
        return
      }
      await sendResolvedFile(req, res, url, resolved)
      return
    }
    if (req.method === 'PUT' || req.method === 'POST' || req.method === 'DELETE') {
      writeIdRequired(res, legacy.id)
      return
    }
    writeMethodNotAllowed(res, 'GET, HEAD')
    return
  }

  writeNotFound(res)
}

// ---------------------------------------------------------------------------
// Entry point
// ---------------------------------------------------------------------------

/**
 * Handle a request owned by the resource centre.
 *
 * @param {import('node:http').IncomingMessage} req
 * @param {import('node:http').ServerResponse} res
 * @param {URL} url
 * @param {{
 *   config: { apkDir?: string, resourceDir?: string, uploadToken?: string|null, maxUploadBytes: number, publicUrl?: string },
 *   gateway: { version: string, name: string, fp: string, tunnels?: number },
 *   log?: (level: string, message: string) => void
 * }} ctx
 * @returns {Promise<boolean>} true when the path belongs to the resource centre
 */
export async function handleResourcesRequest (req, res, url, ctx) {
  const pathname = url.pathname
  if (pathname === DOWNLOAD_PAGE_PATH || pathname === `${DOWNLOAD_PAGE_PATH}/`) {
    await handleDownloadPage(req, res, ctx)
    return true
  }
  if (pathname.startsWith(`${DOWNLOAD_PAGE_PATH}/`)) {
    await handleLegacyApkDownload(req, res, ctx, url, pathname)
    return true
  }
  if (pathname === RESOURCE_INDEX_PATH || pathname === `${RESOURCE_INDEX_PATH}/` || pathname === RESOURCE_INDEX_ALIAS) {
    await handleIndex(req, res, ctx, url)
    return true
  }
  if (pathname.startsWith(`${RESOURCE_BASE_PATH}/`)) {
    await handleResourceRequest(req, res, ctx, url)
    return true
  }
  return false
}
