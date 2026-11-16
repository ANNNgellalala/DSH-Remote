// Unit tests for the LAN APK download page (node:test, pure render).
import assert from 'node:assert/strict'
import test from 'node:test'

import { DOWNLOAD_PAGE_PATH } from '../src/downloads.mjs'
import { downloadPageHeaders, renderDownloadPage } from '../src/download-page.mjs'

function apk (overrides = {}) {
  return {
    name: 'dshremote-1.3.1-24-arm64-release.apk',
    sizeBytes: 36_444_768,
    sha256: 'a'.repeat(64),
    updatedAt: '2026-11-11T08:38:11.464Z',
    version: '1.3.1',
    versionCode: 24,
    ...overrides
  }
}

function render (files, overrides = {}) {
  return renderDownloadPage({
    dir: '/repo/dist/apk',
    files,
    relayVersion: '0.5.0',
    dshVersion: '1.0.0',
    generatedAt: '2026-11-11T12:00:00.000Z',
    ...overrides
  })
}

test('renders the latest card and history with compact timestamps and direct links', () => {
  const html = render([
    apk(),
    apk({ name: 'dshremote-1.3.0-23-arm64-release.apk', version: '1.3.0', versionCode: 23 })
  ])
  assert.match(html, /下载最新版 APK/)
  assert.match(html, new RegExp(`href="${DOWNLOAD_PAGE_PATH}/dshremote-1\\.3\\.1-24-arm64-release\\.apk"`))
  assert.match(html, /dshremote-1\.3\.0-23-arm64-release\.apk/)
  assert.match(html, /34\.76 MiB/)
  assert.match(html, /a{64}/)
  assert.match(html, /v1\.3\.1/)
  assert.match(html, /历史版本/)
  // Compact, explicitly-UTC timestamps instead of a 24-character ISO blob.
  assert.match(html, /2026-11-11 08:38 UTC/)
  assert.match(html, /2026-11-11 12:00 UTC/)
  assert.doesNotMatch(html, /2026-11-11T08:38:11\.464Z/)
})

test('rows use the stacked mobile card layout with a selectable sha256 block', () => {
  const html = render([apk()])
  assert.match(html, /@media \(max-width: 719px\)/)
  assert.match(html, /tbody tr \{ display: flex; flex-wrap: wrap;/)
  assert.match(html, /class="file-cell"><a class="file-link"/)
  assert.match(html, /class="hash-line"><span class="hash-label">sha256<\/span><code class="hash">/)
  assert.match(html, /\.hash \{[^}]*user-select: all/)
  assert.match(html, /data-label="版本"/)
  assert.match(html, /data-label="大小"/)
  assert.match(html, /data-label="更新"/)
  // The hash is no longer its own squeezed table column.
  assert.doesNotMatch(html, /<th scope="col">sha256/)
  assert.doesNotMatch(html, /<th>sha256/)
})

test('non-release APK names stay out of the fixed-width version column', () => {
  const debugApk = apk({
    name: 'smartbill-1.0.0-universal-debug.apk',
    version: 'smartbill-1.0.0-universal-debug.apk',
    versionCode: null
  })
  // A release APK is newest, so the debug build lands in the history table.
  const html = render([apk(), debugApk])
  assert.match(html, /class="version-text" title="smartbill-1\.0\.0-universal-debug\.apk">—<\/span>/)
  assert.match(html, /href=".*smartbill-1\.0\.0-universal-debug\.apk"/)
})

test('empty download directory renders the build hint', () => {
  const html = render([])
  assert.match(html, /暂无可下载 APK/)
  assert.match(html, /scripts\/build-release-apk\.ps1/)
  assert.match(html, /暂无文件/)
  assert.doesNotMatch(html, /下载最新版 APK/)
})

test('escapes untrusted metadata and never emits a script', () => {
  const nasty = '"><script>alert(1)</script>'
  const html = render([apk()], { relayVersion: nasty, dshVersion: nasty, dir: nasty, generatedAt: nasty })
  assert.doesNotMatch(html, /<script/i)
  assert.doesNotMatch(html, /alert\(1\)<\/script>/)
  assert.match(html, /&quot;&gt;&lt;script&gt;/)
})

test('downloadPageHeaders keeps the strict no-script CSP', () => {
  const headers = downloadPageHeaders('text/html; charset=utf-8')
  assert.equal(headers['content-security-policy'], [
    "default-src 'none'",
    "style-src 'unsafe-inline'",
    "img-src 'none'",
    "base-uri 'none'",
    "form-action 'none'",
    "frame-ancestors 'none'"
  ].join('; '))
  assert.doesNotMatch(headers['content-security-policy'], /script-src/)
  assert.equal(headers['x-frame-options'], 'DENY')
  assert.equal(headers['x-content-type-options'], 'nosniff')
  assert.equal(headers['cache-control'], 'no-store')
  assert.equal(headers['referrer-policy'], 'no-referrer')
})
