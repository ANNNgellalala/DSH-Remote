// HTTP-level tests for the gateway resource centre (node:test, loopback only).
import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import test from 'node:test'

import { withResourceServer, rawRequest } from './fixtures.mjs'

const APK_NAME = 'dshremote-1.6.0-32-arm64-release.apk'
const APK_OLD = 'dshremote-1.2.0-13-arm64-release.apk'
const APK_BYTES = Buffer.from('fake-apk-bytes-for-tests')
const SMARTBILL_NAME = 'smartbill-1.0.2-3-universal-debugsign.apk'

function sha256 (value) {
  return crypto.createHash('sha256').update(value).digest('hex')
}

function auth (token = 'test-upload-token') {
  return { authorization: `Bearer ${token}` }
}

function sleep (ms) {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

function seedApk (fixture) {
  fixture.write(fixture.dirFor('dsh-remote'), APK_NAME, APK_BYTES)
  return APK_NAME
}

test('download page groups by category, has no kind chips and keeps the strict CSP', async () => {
  await withResourceServer(async ({ fixture, base }) => {
    seedApk(fixture)
    fixture.write(fixture.dirFor('smartbill'), SMARTBILL_NAME, 'smartbill-apk')
    fixture.write(fixture.dirFor('relay'), 'dsh-remote-relay-0.4.0.tgz', 'relay-bytes')
    fixture.write(fixture.dirFor('docs'), 'README.md', '# docs')
    const response = await fetch(`${base}/__gateway/download`)
    assert.equal(response.status, 200)
    assert.match(response.headers.get('content-type'), /text\/html/)
    assert.match(response.headers.get('content-security-policy'), /default-src 'none'/)
    assert.equal(response.headers.get('x-frame-options'), 'DENY')
    const html = await response.text()
    assert.match(html, /资源下载中心/)
    assert.match(html, /DSH Remote 客户端/)
    assert.match(html, /SmartBill（我的账单）客户端/)
    assert.match(html, /桌面 Relay 插件包/)
    assert.match(html, /文档资料/)
    assert.match(html, /下载最新版 APK/)
    assert.match(html, /下载最新文件/)
    assert.match(html, new RegExp(APK_NAME))
    assert.match(html, new RegExp(SMARTBILL_NAME))
    assert.match(html, /dsh-remote-relay-0\.4\.0\.tgz/)
    assert.match(html, /README\.md/)
    // v0.4.0: the file-type chip navigation at the top is gone
    assert.doesNotMatch(html, /<nav class="chips"/)
    assert.doesNotMatch(html, /class="chip"/)
    assert.doesNotMatch(html, /chip-count/)
    assert.doesNotMatch(html, /id="kind-/)
    // and still no scripts
    assert.doesNotMatch(html, /<script/i)
  })
})

test('download page supports HEAD and friendly empty state', async () => {
  await withResourceServer(async ({ base }) => {
    const head = await fetch(`${base}/__gateway/download`, { method: 'HEAD' })
    assert.equal(head.status, 200)
    assert.equal(await head.text(), '')
    const html = await (await fetch(`${base}/__gateway/download`)).text()
    assert.match(html, /当前没有任何可下载资源/)
    assert.doesNotMatch(html, /<nav class="chips"/)
  })
})

test('JSON index exposes categories by id, metadata, absolute URLs and filters', async () => {
  await withResourceServer(async ({ fixture, base }) => {
    seedApk(fixture)
    fixture.write(fixture.dirFor('docs'), 'README.md', '# docs')
    const response = await fetch(`${base}/__gateway/resources`)
    assert.equal(response.status, 200)
    assert.match(response.headers.get('content-type'), /application\/json/)
    const index = await response.json()
    assert.equal(index.ok, true)
    assert.equal(index.page, '/__gateway/download')
    assert.equal(index.index, '/__gateway/resources')
    assert.equal(index.downloadBase, '/__gateway/resources')
    assert.equal(index.gateway.url, 'https://gw.test:9443')
    assert.deepEqual(index.categories.map((category) => category.id), ['dsh-remote', 'smartbill', 'relay', 'docs'])
    assert.equal(index.total, 2)
    assert.deepEqual(index.counts.byId, { 'dsh-remote': 1, smartbill: 0, relay: 0, docs: 1 })
    const apk = index.categories.find((category) => category.id === 'dsh-remote')
    assert.equal(apk.latest.name, APK_NAME)
    assert.equal(apk.latest.version, '1.6.0')
    assert.equal(apk.latest.versionCode, 32)
    assert.equal(apk.latest.sha256, sha256(APK_BYTES))
    assert.equal(apk.latest.path, `/__gateway/resources/dsh-remote/${APK_NAME}`)
    assert.equal(apk.latest.url, `https://gw.test:9443/__gateway/resources/dsh-remote/${APK_NAME}`)
    assert.equal(apk.latest.legacyUrl, `/__gateway/download/${APK_NAME}`)
    assert.equal(apk.latest.sizeBytes, APK_BYTES.length)
    assert.equal(apk.latest.contentType, 'application/vnd.android.package-archive')
    const docs = index.categories.find((category) => category.id === 'docs')
    assert.equal(docs.files[0].name, 'README.md')
    assert.equal(docs.latest.version, undefined)
    assert.match(docs.files[0].sha256, /^[0-9a-f]{64}$/)
    assert.equal(index.apk.count, 1)
    assert.equal(index.apk.latest.file, APK_NAME)
    // no absolute server paths / secrets in the public index
    const serialized = JSON.stringify(index)
    assert.equal(serialized.includes(fixture.root), false)
    assert.equal(serialized.includes('test-upload-token'), false)

    const filtered = await (await fetch(`${base}/__gateway/resources?id=docs`)).json()
    assert.equal(filtered.categories.length, 1)
    assert.equal(filtered.categories[0].id, 'docs')
    const unknown = await fetch(`${base}/__gateway/resources?id=nope`)
    assert.equal(unknown.status, 400)
    const unknownBody = await unknown.json()
    assert.equal(unknownBody.error, 'unknown-id')
    assert.deepEqual(unknownBody.allowedIds, ['dsh-remote', 'smartbill', 'relay', 'docs'])
    // alias endpoint + HEAD
    const alias = await fetch(`${base}/__gateway/resources/index`)
    assert.equal(alias.status, 200)
    const head = await fetch(`${base}/__gateway/resources`, { method: 'HEAD' })
    assert.equal(head.status, 200)
    assert.equal(await head.text(), '')
  })
})

test('resource download: GET/HEAD/Range/ETag and the legacy routes', async () => {
  await withResourceServer(async ({ fixture, base }) => {
    seedApk(fixture)
    const url = `${base}/__gateway/resources/dsh-remote/${APK_NAME}`
    const get = await fetch(url)
    assert.equal(get.status, 200)
    assert.equal(get.headers.get('content-type'), 'application/vnd.android.package-archive')
    assert.match(get.headers.get('content-disposition'), /^attachment;/)
    assert.equal(get.headers.get('x-content-sha256'), sha256(APK_BYTES))
    assert.equal(get.headers.get('accept-ranges'), 'bytes')
    assert.equal(get.headers.get('content-length'), String(APK_BYTES.length))
    assert.deepEqual(Buffer.from(await get.arrayBuffer()), APK_BYTES)
    const etag = get.headers.get('etag')
    assert.match(etag, /^"[0-9a-f]{64}"$/)

    const head = await fetch(url, { method: 'HEAD' })
    assert.equal(head.status, 200)
    assert.equal(head.headers.get('content-length'), String(APK_BYTES.length))
    assert.equal(await head.text(), '')

    const range = await fetch(url, { headers: { range: 'bytes=0-3' } })
    assert.equal(range.status, 206)
    assert.equal(range.headers.get('content-range'), `bytes 0-3/${APK_BYTES.length}`)
    assert.equal(range.headers.get('content-length'), '4')
    assert.deepEqual(Buffer.from(await range.arrayBuffer()), APK_BYTES.subarray(0, 4))

    const badRange = await fetch(url, { headers: { range: 'bytes=9999-' } })
    assert.equal(badRange.status, 416)
    assert.equal(badRange.headers.get('content-range'), `bytes */${APK_BYTES.length}`)

    const cached = await fetch(url, { headers: { 'if-none-match': etag } })
    assert.equal(cached.status, 304)

    // legacy APK route still serves the same file by name across categories
    const legacy = await fetch(`${base}/__gateway/download/${APK_NAME}`)
    assert.equal(legacy.status, 200)
    assert.deepEqual(Buffer.from(await legacy.arrayBuffer()), APK_BYTES)
    // legacy apk kind alias for downloads only
    const alias = await fetch(`${base}/__gateway/resources/apk/${APK_NAME}`)
    assert.equal(alias.status, 200)
    assert.deepEqual(Buffer.from(await alias.arrayBuffer()), APK_BYTES)
    assert.equal((await fetch(`${base}/__gateway/resources/apk/evil.exe`)).status, 404)
    // and rejects traversal / unknown files
    assert.equal((await fetch(`${base}/__gateway/download/${encodeURIComponent('../gateway.env')}`)).status, 404)
    assert.equal((await fetch(`${base}/__gateway/download/nope.apk`)).status, 404)
  })
})

test('inline vs attachment content-disposition is enforced by extension', async () => {
  await withResourceServer(async ({ fixture, base }) => {
    seedApk(fixture)
    fixture.write(fixture.dirFor('docs'), 'logo.png', Buffer.from([0x89, 0x50, 0x4e, 0x47]))
    const inlineImage = await fetch(`${base}/__gateway/resources/docs/logo.png?inline=1`)
    assert.equal(inlineImage.status, 200)
    assert.match(inlineImage.headers.get('content-disposition'), /^inline;/)
    assert.match(inlineImage.headers.get('content-security-policy'), /sandbox/)
    const attachmentImage = await fetch(`${base}/__gateway/resources/docs/logo.png`)
    assert.match(attachmentImage.headers.get('content-disposition'), /^attachment;/)
    // APK can never be inline, even when asked
    const apk = await fetch(`${base}/__gateway/resources/dsh-remote/${APK_NAME}?inline=1`)
    assert.match(apk.headers.get('content-disposition'), /^attachment;/)
  })
})

test('invalid ids, names and traversal are 404 for downloads', async () => {
  await withResourceServer(async ({ fixture, base }) => {
    seedApk(fixture)
    fixture.write(fixture.resourceDir, 'gateway.env', 'TOKEN=secret')
    for (const requestPath of [
      '/__gateway/resources/dsh-remote/..%2Fgateway.env',
      '/__gateway/resources/dsh-remote/%2e%2e%2f%2e%2e%2fetc%2fpasswd',
      '/__gateway/resources/dsh-remote/',
      '/__gateway/resources/dsh-remote',
      '/__gateway/resources/nokind/x.apk',
      '/__gateway/resources/dsh-remote/evil.exe',
      '/__gateway/resources/dsh-remote/missing.apk'
    ]) {
      const response = await fetch(`${base}${requestPath}`)
      assert.equal(response.status, 404, `expected 404 for ${requestPath}`)
    }
    // `/__gateway/resources/` (trailing slash) is the index, not a resource path
    assert.equal((await fetch(`${base}/__gateway/resources/`)).status, 200)
  })
})

test('upload requires a configured token and a valid bearer token', async () => {
  await withResourceServer(async ({ fixture, base }) => {
    const target = `${base}/__gateway/resources/docs/notes.md`
    const noToken = await fetch(target, { method: 'PUT', body: Buffer.from('# hi') })
    assert.equal(noToken.status, 403)
    assert.match(noToken.headers.get('www-authenticate'), /Bearer/)
    const wrongToken = await fetch(target, { method: 'PUT', headers: auth('wrong-token'), body: Buffer.from('# hi') })
    assert.equal(wrongToken.status, 403)
    assert.equal(fs.existsSync(path.join(fixture.dirFor('docs'), 'notes.md')), false)
  })
})

test('upload/delete are disabled with 404 when UPLOAD_TOKEN is not configured', async () => {
  await withResourceServer(async ({ fixture, base }) => {
    seedApk(fixture)
    const put = await fetch(`${base}/__gateway/resources/docs/x.md`, {
      method: 'PUT',
      headers: auth('anything'),
      body: Buffer.from('x')
    })
    assert.equal(put.status, 404)
    assert.equal((await put.json()).error, 'uploads-disabled')
    const del = await fetch(`${base}/__gateway/resources/dsh-remote/${APK_NAME}`, { method: 'DELETE', headers: auth() })
    assert.equal(del.status, 404)
  }, { configOverrides: { uploadToken: null } })
})

test('upload streams to an atomic file, returns sha256, then delete removes it', async () => {
  await withResourceServer(async ({ fixture, base }) => {
    const content = Buffer.from('resource upload payload 你好')
    const put = await fetch(`${base}/__gateway/resources/docs/notes.md`, {
      method: 'PUT',
      headers: { ...auth(), 'content-type': 'text/markdown' },
      body: content
    })
    assert.equal(put.status, 201)
    const uploaded = await put.json()
    assert.equal(uploaded.ok, true)
    assert.equal(uploaded.id, 'docs')
    assert.equal(uploaded.name, 'notes.md')
    assert.equal(uploaded.sizeBytes, content.length)
    assert.equal(uploaded.sha256, sha256(content))
    assert.equal(uploaded.keepVersions, false)
    assert.equal(uploaded.replaced, false)
    assert.equal(uploaded.path, '/__gateway/resources/docs/notes.md')
    assert.equal(uploaded.url, 'https://gw.test:9443/__gateway/resources/docs/notes.md')
    assert.deepEqual(uploaded.pruned, [])
    // landed atomically: exact bytes, no temp files left behind
    const onDisk = path.join(fixture.dirFor('docs'), 'notes.md')
    assert.deepEqual(fs.readFileSync(onDisk), content)
    assert.deepEqual(fs.readdirSync(path.dirname(onDisk)).filter((name) => name.startsWith('.upload-')), [])
    // appears in the index and downloads back byte-identical
    const index = await (await fetch(`${base}/__gateway/resources?id=docs`)).json()
    assert.equal(index.categories[0].files[0].name, 'notes.md')
    const download = await fetch(`${base}/__gateway/resources/docs/notes.md`)
    assert.equal(download.status, 200)
    assert.deepEqual(Buffer.from(await download.arrayBuffer()), content)
    // PUT again replaces atomically
    const replacedContent = Buffer.from('v2')
    const put2 = await fetch(`${base}/__gateway/resources/docs/notes.md`, {
      method: 'PUT',
      headers: auth(),
      body: replacedContent
    })
    assert.equal(put2.status, 200)
    assert.equal((await put2.json()).replaced, true)
    assert.deepEqual(fs.readFileSync(onDisk), replacedContent)
    // delete
    const del = await fetch(`${base}/__gateway/resources/docs/notes.md`, { method: 'DELETE', headers: auth() })
    assert.equal(del.status, 200)
    assert.equal((await del.json()).deleted, true)
    assert.equal(fs.existsSync(onDisk), false)
    assert.equal((await fetch(`${base}/__gateway/resources/docs/notes.md`)).status, 404)
    assert.equal((await fetch(`${base}/__gateway/resources/docs/notes.md`, { method: 'DELETE', headers: auth() })).status, 404)
  })
})

test('keepVersions=0 (default) keeps only the uploaded version per category', async () => {
  await withResourceServer(async ({ fixture, base }) => {
    const upload = (name, body) => fetch(`${base}/__gateway/resources/docs/${name}`, {
      method: 'PUT', headers: auth(), body: Buffer.from(body)
    })
    const first = await upload('notes-1.md', 'one')
    assert.equal(first.status, 201)
    assert.deepEqual((await first.json()).retained, ['notes-1.md'])
    await sleep(15)
    const second = await upload('notes-2.md', 'two')
    assert.equal(second.status, 201)
    const body = await second.json()
    assert.equal(body.keepVersions, false)
    assert.deepEqual(body.retained, ['notes-2.md'])
    assert.deepEqual(body.pruned, ['notes-1.md'])
    assert.deepEqual(fs.readdirSync(fixture.dirFor('docs')), ['notes-2.md'])
  })
})

test('keepVersions=1 keeps at most three newest versions', async () => {
  await withResourceServer(async ({ fixture, base }) => {
    const upload = (name, versionCode) => fetch(`${base}/__gateway/resources/dsh-remote/${name}?keepVersions=1`, {
      method: 'PUT', headers: auth(), body: Buffer.from(`apk-${versionCode}`)
    })
    const names = [
      'dshremote-1.2.0-13-arm64-release.apk',
      'dshremote-1.3.0-23-arm64-release.apk',
      'dshremote-1.4.0-25-arm64-release.apk',
      'dshremote-1.6.0-32-arm64-release.apk'
    ]
    const codes = [13, 23, 25, 32]
    for (let i = 0; i < names.length; i++) {
      await sleep(15)
      const response = await upload(names[i], codes[i])
      assert.equal(response.status, 201)
      assert.equal((await response.json()).keepVersions, true)
    }
    const last = await fetch(`${base}/__gateway/resources?id=dsh-remote`).then((response) => response.json())
    assert.equal(last.categories[0].count, 3)
    assert.deepEqual(last.categories[0].files.map((file) => file.name), [names[3], names[2], names[1]])
    assert.deepEqual(fs.readdirSync(fixture.dirFor('dsh-remote')).sort(), [names[1], names[2], names[3]].sort())
  })
})

test('invalid keepVersions values are rejected with 400', async () => {
  await withResourceServer(async ({ base }) => {
    for (const value of ['2', 'maybe', '']) {
      const response = await fetch(`${base}/__gateway/resources/docs/x.md?keepVersions=${encodeURIComponent(value)}`, {
        method: 'PUT', headers: auth(), body: Buffer.from('x')
      })
      assert.equal(response.status, 400, `expected 400 for keepVersions=${JSON.stringify(value)}`)
      assert.equal((await response.json()).error, 'invalid-keep-versions')
    }
    // explicit falsy spellings are accepted
    const falsy = await fetch(`${base}/__gateway/resources/docs/x.md?keepVersions=false`, {
      method: 'PUT', headers: auth(), body: Buffer.from('x')
    })
    assert.equal(falsy.status, 201)
    assert.equal((await falsy.json()).keepVersions, false)
  })
})

test('upload rejects invalid names and legacy kind paths with id-required', async () => {
  await withResourceServer(async ({ fixture, base }) => {
    const cases = [
      '/__gateway/resources/docs/..%2Fgateway.env',
      '/__gateway/resources/docs/evil.exe',
      '/__gateway/resources/docs/.hidden.md',
      '/__gateway/resources/nokind/file.md'
    ]
    for (const requestPath of cases) {
      const response = await fetch(`${base}${requestPath}`, {
        method: 'PUT',
        headers: auth(),
        body: Buffer.from('x')
      })
      assert.ok([400, 404].includes(response.status), `unexpected ${response.status} for ${requestPath}`)
    }
    // legacy upload paths (apk/image/misc) are download-only now
    for (const requestPath of ['/__gateway/resources/apk/x.apk', '/__gateway/resources/image/x.png', '/__gateway/resources/misc/x.bin']) {
      const response = await fetch(`${base}${requestPath}`, { method: 'PUT', headers: auth(), body: Buffer.from('x') })
      assert.equal(response.status, 400, `expected 400 for ${requestPath}`)
      const body = await response.json()
      assert.equal(body.error, 'id-required')
      assert.match(body.message, /keepVersions/)
    }
    // path traversal must never create a file outside the category directory
    assert.equal(fs.existsSync(path.join(fixture.resourceDir, 'gateway.env')), false)
    const del = await fetch(`${base}/__gateway/resources/docs/notes.md`, { method: 'DELETE' })
    assert.equal(del.status, 403)
  })
})

test('oversized uploads are rejected with 413 (declared and chunked)', async () => {
  await withResourceServer(async ({ fixture, base }) => {
    const url = `${base}/__gateway/resources/docs/big.md`
    const declared = await fetch(url, {
      method: 'PUT',
      headers: auth(),
      body: Buffer.alloc(2048, 0x61)
    })
    assert.equal(declared.status, 413)
    assert.equal((await declared.json()).error, 'upload-too-large')
    assert.equal(fs.existsSync(path.join(fixture.dirFor('docs'), 'big.md')), false)

    const chunked = await rawRequest(base, {
      method: 'PUT',
      path: '/__gateway/resources/docs/big.md',
      headers: { ...auth(), 'transfer-encoding': 'chunked' },
      chunks: [Buffer.alloc(40, 0x61), Buffer.alloc(40, 0x62), Buffer.alloc(40, 0x63)]
    })
    assert.equal(chunked.status, 413)
    assert.equal(fs.existsSync(path.join(fixture.dirFor('docs'), 'big.md')), false)
  }, { configOverrides: { maxUploadBytes: 64 } })
})

test('method not allowed and unknown gateway endpoints', async () => {
  await withResourceServer(async ({ fixture, base }) => {
    seedApk(fixture)
    const patch = await fetch(`${base}/__gateway/resources/dsh-remote/${APK_NAME}`, { method: 'PATCH' })
    assert.equal(patch.status, 405)
    assert.match(patch.headers.get('allow'), /GET/)
    const postPage = await fetch(`${base}/__gateway/download`, { method: 'POST' })
    assert.equal(postPage.status, 405)
    const unknown = await fetch(`${base}/__gateway/unknown-endpoint`)
    assert.equal(unknown.status, 404)
    assert.equal((await unknown.json()).error, 'unknown-gateway-endpoint')
    const unknownResource = await fetch(`${base}/__gateway/resources/dsh-remote/../x`)
    assert.equal([400, 404].includes(unknownResource.status), true)
  })
})

test('legacy APK paths still resolve files uploaded to a category', async () => {
  await withResourceServer(async ({ fixture, base }) => {
    const content = Buffer.from('uploaded-apk')
    const put = await fetch(`${base}/__gateway/resources/dsh-remote/${APK_NAME}`, {
      method: 'PUT',
      headers: auth(),
      body: content
    })
    assert.equal(put.status, 201)
    assert.equal((await put.json()).sha256, sha256(content))
    const page = await (await fetch(`${base}/__gateway/download`)).text()
    assert.match(page, new RegExp(APK_NAME))
    const legacy = await fetch(`${base}/__gateway/download/${APK_NAME}`)
    assert.equal(legacy.status, 200)
    assert.deepEqual(Buffer.from(await legacy.arrayBuffer()), content)
    const canonical = await fetch(`${base}/__gateway/resources/dsh-remote/${APK_NAME}`)
    assert.equal(canonical.status, 200)
    assert.ok(fs.existsSync(path.join(fixture.dirFor('dsh-remote'), APK_NAME)))
    // legacy APK_DIR files stay downloadable via the legacy route only
    fixture.write(fixture.apkDir, APK_OLD, 'legacy-apk-dir')
    const legacyOnly = await fetch(`${base}/__gateway/download/${APK_OLD}`)
    assert.equal(legacyOnly.status, 200)
    assert.equal((await fetch(`${base}/__gateway/resources/dsh-remote/${APK_OLD}`)).status, 404)
  })
})
