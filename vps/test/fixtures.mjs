// Shared fixtures for the gateway resource-centre tests (no network, no VPS).
//
// Every test gets a throw-away temp tree:
//
//   <root>/apk/                    legacy APK_DIR (download fallback only)
//   <root>/resources/<category>/   one directory per registered category
//
// and a plain HTTP server that mounts `handleResourcesRequest` exactly like
// gateway.mjs does (falling back to a 404 for paths the handler does not own).
import fs from 'node:fs'
import http from 'node:http'
import os from 'node:os'
import path from 'node:path'

import { RESOURCE_CATEGORIES } from '../resources.mjs'
import { handleResourcesRequest } from '../resources-http.mjs'

export const GATEWAY = Object.freeze({
  version: '0.4.0',
  name: 'DSH Remote Gateway',
  fp: 'a'.repeat(64),
  tunnels: 1
})

export const RESOURCE_CATEGORY_IDS = RESOURCE_CATEGORIES.map((category) => category.id)

export function makeFixture () {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'dsh-gw-resources-test-'))
  const apkDir = path.join(root, 'apk')
  const resourceDir = path.join(root, 'resources')
  fs.mkdirSync(apkDir, { recursive: true })
  for (const id of RESOURCE_CATEGORY_IDS) fs.mkdirSync(path.join(resourceDir, id), { recursive: true })
  const write = (dir, name, content, mtime) => {
    const file = path.join(dir, name)
    fs.writeFileSync(file, content)
    if (mtime) fs.utimesSync(file, mtime, mtime)
    return file
  }
  return {
    root,
    apkDir,
    resourceDir,
    config: { apkDir, resourceDir },
    dirFor: (id) => path.join(resourceDir, id),
    write,
    cleanup: () => fs.rmSync(root, { recursive: true, force: true })
  }
}

export function makeConfig (fixture, overrides = {}) {
  return {
    apkDir: fixture.apkDir,
    resourceDir: fixture.resourceDir,
    uploadToken: 'test-upload-token',
    maxUploadBytes: 1024 * 1024,
    publicUrl: 'https://gw.test:9443',
    ...overrides
  }
}

/**
 * Run `fn({ fixture, config, base, ctx, server })` with a live loopback server,
 * then close it and delete the temp tree.
 */
export async function withResourceServer (fn, { configOverrides = {} } = {}) {
  const fixture = makeFixture()
  const config = makeConfig(fixture, configOverrides)
  const ctx = { config, gateway: { ...GATEWAY }, log: () => {} }
  const server = http.createServer(async (req, res) => {
    try {
      const url = new URL(String(req.url ?? '/'), 'https://gw.test:9443')
      const handled = await handleResourcesRequest(req, res, url, ctx)
      if (!handled) {
        const body = Buffer.from(JSON.stringify({ ok: false, error: 'unknown-gateway-endpoint' }))
        res.writeHead(404, { 'content-type': 'application/json; charset=utf-8', 'content-length': body.length })
        res.end(body)
      }
    } catch (error) {
      if (!res.headersSent) {
        const body = Buffer.from(JSON.stringify({ ok: false, error: 'gateway-error', message: String(error?.message ?? error) }))
        res.writeHead(500, { 'content-type': 'application/json; charset=utf-8', 'content-length': body.length })
        res.end(body)
      } else {
        res.destroy()
      }
    }
  })
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve))
  const base = `http://127.0.0.1:${server.address().port}`
  try {
    return await fn({ fixture, config, base, ctx, server })
  } finally {
    server.closeAllConnections?.()
    await new Promise((resolve) => server.close(resolve))
    fixture.cleanup()
  }
}

/** Raw node:http request (used for chunked uploads, where fetch sets content-length). */
export function rawRequest (base, { method = 'GET', path: requestPath, headers = {}, chunks = [] } = {}) {
  const url = new URL(requestPath, base)
  return new Promise((resolve, reject) => {
    const req = http.request({
      hostname: url.hostname,
      port: url.port,
      path: `${url.pathname}${url.search}`,
      method,
      headers
    }, (res) => {
      const body = []
      res.on('data', (chunk) => body.push(chunk))
      res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body: Buffer.concat(body) }))
    })
    req.on('error', reject)
    for (const chunk of chunks) req.write(chunk)
    req.end()
  })
}
