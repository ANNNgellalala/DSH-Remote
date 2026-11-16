// Unit tests for the public category-grouped download page (node:test, pure render).
import assert from 'node:assert/strict'
import test from 'node:test'

import { downloadPageHeaders, renderDownloadPage } from '../download-page.mjs'
import { DOWNLOAD_PAGE_PATH, RESOURCE_INDEX_PATH } from '../resources.mjs'

const GATEWAY = {
  gatewayVersion: '0.4.0',
  gatewayName: 'DSH Remote Gateway',
  fingerprint: 'b'.repeat(64),
  baseUrl: 'https://gw.test:9443',
  tunnels: 2,
  generatedAt: '2026-11-13T05:00:00.000Z'
}

function apkFile (overrides = {}) {
  return {
    id: 'dsh-remote',
    name: 'dshremote-1.6.0-32-arm64-release.apk',
    sizeBytes: 36_506_208,
    sha256: 'c'.repeat(64),
    updatedAt: '2026-11-12T16:54:14.000Z',
    contentType: 'application/vnd.android.package-archive',
    url: '/__gateway/resources/dsh-remote/dshremote-1.6.0-32-arm64-release.apk',
    version: '1.6.0',
    versionCode: 32,
    ...overrides
  }
}

function category (overrides = {}) {
  const files = overrides.files ?? []
  return {
    id: 'docs',
    title: '文档资料',
    description: '使用说明、部署文档。',
    flavor: 'file',
    dir: '/opt/dsh-remote/resources/docs',
    count: files.length,
    latest: files[0] ?? null,
    files,
    ...overrides
  }
}

function render (categories) {
  return renderDownloadPage({ ...GATEWAY, dir: '/opt/dsh-remote/apk', resourceDir: '/opt/dsh-remote/resources', categories })
}

test('renders one card per non-empty category with links, counts and latest buttons', () => {
  const dsh = category({
    id: 'dsh-remote',
    title: 'DSH Remote 客户端',
    description: 'Release 签名 APK。',
    flavor: 'apk',
    dir: '/opt/dsh-remote/resources/dsh-remote',
    files: [
      apkFile(),
      apkFile({ name: 'dshremote-1.5.2-31-arm64-release.apk', url: '/__gateway/resources/dsh-remote/dshremote-1.5.2-31-arm64-release.apk', version: '1.5.2', versionCode: 31 })
    ],
    count: 2,
    latest: apkFile()
  })
  const smartbill = category({
    id: 'smartbill',
    title: 'SmartBill（我的账单）客户端',
    description: 'SmartBill APK。',
    flavor: 'apk',
    dir: '/opt/dsh-remote/resources/smartbill',
    files: [{
      id: 'smartbill',
      name: 'smartbill-1.0.2-3-universal-debugsign.apk',
      sizeBytes: 12_000_000,
      sha256: 'e'.repeat(64),
      updatedAt: '2026-11-12T10:00:00.000Z',
      contentType: 'application/vnd.android.package-archive',
      url: '/__gateway/resources/smartbill/smartbill-1.0.2-3-universal-debugsign.apk',
      version: 'smartbill-1.0.2-3-universal-debugsign.apk',
      versionCode: null
    }],
    count: 1
  })
  const docs = category({
    files: [{
      id: 'docs',
      name: 'README.md',
      sizeBytes: 5_633,
      sha256: 'd'.repeat(64),
      updatedAt: '2026-11-10T10:00:00.000Z',
      contentType: 'text/markdown; charset=utf-8',
      url: '/__gateway/resources/docs/README.md'
    }]
  })
  const html = render([dsh, smartbill, docs])
  assert.match(html, /资源下载中心/)
  assert.match(html, /DSH Remote 客户端/)
  assert.match(html, /SmartBill（我的账单）客户端/)
  assert.match(html, /文档资料/)
  assert.match(html, /下载最新版 APK/)
  assert.match(html, /下载最新文件/)
  assert.match(html, /v1\.6\.0/)
  assert.match(html, /34\.82 MiB/) // 36,506,208 bytes
  assert.match(html, /c{64}/)
  assert.match(html, /e{64}/)
  assert.match(html, /d{64}/)
  assert.match(html, /href="\/__gateway\/resources\/dsh-remote\/dshremote-1\.6\.0-32-arm64-release\.apk"/)
  assert.match(html, /href="\/__gateway\/resources\/smartbill\/smartbill-1\.0\.2-3-universal-debugsign\.apk"/)
  assert.match(html, /href="\/__gateway\/resources\/docs\/README\.md"/)
  assert.match(html, /README\.md/)
  // per-category anchors (for operators/deep links) but no chip navigation
  assert.match(html, /id="category-dsh-remote"/)
  assert.match(html, /id="category-smartbill"/)
  assert.match(html, /id="category-docs"/)
  assert.doesNotMatch(html, /<nav class="chips"/)
  assert.doesNotMatch(html, /class="chip"/)
  assert.doesNotMatch(html, /id="kind-/)
})

test('file rows stay readable on narrow screens (stacked cards, selectable hash)', () => {
  const html = render([category({
    id: 'dsh-remote',
    title: 'DSH Remote 客户端',
    flavor: 'apk',
    files: [apkFile()],
    count: 1,
    latest: apkFile()
  })])
  assert.match(html, /@media \(max-width: 719px\)/)
  assert.match(html, /tbody tr \{ display: flex; flex-wrap: wrap;/)
  assert.match(html, /class="file-cell"><a class="file-link"/)
  assert.match(html, /class="hash-line"><span class="hash-label">sha256<\/span><code class="hash">/)
  assert.match(html, /\.hash \{[^}]*user-select: all/)
  assert.match(html, /data-label="大小"/)
  assert.match(html, /data-label="更新"/)
  assert.match(html, /class="version-cell" data-label="版本"/)
  // Timestamps are compact and explicitly UTC (no 24-char ISO string in a cell).
  assert.match(html, /2026-11-12 16:54 UTC/)
  assert.doesNotMatch(html, /2026-11-12T16:54:14\.000Z/)
  assert.doesNotMatch(html, /<th scope="col">sha256/)
})

test('non-release APK names stay out of the fixed-width version column', () => {
  const debugApk = apkFile({
    id: 'smartbill',
    name: 'smartbill-1.0.2-3-universal-debugsign.apk',
    url: '/__gateway/resources/smartbill/smartbill-1.0.2-3-universal-debugsign.apk',
    version: 'smartbill-1.0.2-3-universal-debugsign.apk',
    versionCode: null
  })
  const html = render([category({
    id: 'smartbill',
    title: 'SmartBill（我的账单）客户端',
    flavor: 'apk',
    files: [debugApk],
    count: 1,
    latest: debugApk
  })])
  assert.match(html, /class="version-text" title="smartbill-1\.0\.2-3-universal-debugsign\.apk">—<\/span>/)
  assert.match(html, /href="\/__gateway\/resources\/smartbill\/smartbill-1\.0\.2-3-universal-debugsign\.apk"/)
})

test('empty categories are hidden and a fully empty gateway has a friendly empty state', () => {
  const emptyDocs = category()
  const html = render([category({ id: 'dsh-remote', title: 'DSH Remote 客户端', flavor: 'apk', files: [], latest: null }), emptyDocs])
  assert.match(html, /当前没有任何可下载资源/)
  assert.match(html, /scripts\/upload-resource\.ps1/)
  // no cards for empty categories
  assert.doesNotMatch(html, /id="category-docs"/)
  assert.doesNotMatch(html, /该类别暂无资源/)

  const noCategories = render([])
  assert.match(noCategories, /当前没有任何可下载资源/)
  assert.doesNotMatch(noCategories, /undefined/)
})

test('escapes untrusted metadata (no HTML injection, no script)', () => {
  const nasty = '"><script>alert(1)</script>'
  const html = renderDownloadPage({
    ...GATEWAY,
    gatewayName: nasty,
    gatewayVersion: nasty,
    fingerprint: nasty,
    baseUrl: nasty,
    generatedAt: nasty,
    dir: nasty,
    resourceDir: nasty,
    categories: [category({
      id: 'docs',
      title: nasty,
      description: nasty,
      dir: nasty,
      files: [{
        id: 'docs',
        name: 'x.md',
        sizeBytes: 1,
        sha256: nasty,
        updatedAt: nasty,
        contentType: 'text/markdown',
        url: '/__gateway/resources/docs/x.md'
      }]
    })]
  })
  assert.doesNotMatch(html, /<script/i)
  assert.doesNotMatch(html, /alert\(1\)<\/script>/)
  assert.match(html, /&lt;script&gt;/)
  assert.match(html, /&quot;&gt;&lt;script&gt;/)
})

test('legacy model (files + dir only) still renders the APK page', () => {
  const legacyFile = { ...apkFile() }
  delete legacyFile.url // old model had no canonical url; fall back to legacy link
  const html = renderDownloadPage({
    ...GATEWAY,
    dir: '/opt/dsh-remote/apk',
    files: [legacyFile]
  })
  assert.match(html, /下载最新版 APK/)
  assert.match(html, /Android 安装包（APK）/)
  assert.match(html, /\/__gateway\/download\//)
})

test('page links to the JSON index and never mentions tokens', () => {
  const html = render([category({ files: [apkFile()] })])
  assert.match(html, new RegExp(RESOURCE_INDEX_PATH.replaceAll('/', '\\/')))
  assert.match(html, new RegExp(DOWNLOAD_PAGE_PATH.replaceAll('/', '\\/')))
  assert.match(html, /\?id=dsh-remote/)
  assert.doesNotMatch(html, /UPLOAD_TOKEN\s*=\s*[0-9a-f]{16,}/)
  assert.match(html, /不含任何 token/)
  // retention policy is explained on the page
  assert.match(html, /keepVersions/)
})

test('downloadPageHeaders keeps the strict no-script CSP', () => {
  const headers = downloadPageHeaders('text/html; charset=utf-8')
  assert.match(headers['content-security-policy'], /default-src 'none'/)
  assert.doesNotMatch(headers['content-security-policy'], /script-src/)
  assert.equal(headers['x-frame-options'], 'DENY')
  assert.equal(headers['x-content-type-options'], 'nosniff')
  assert.equal(headers['cache-control'], 'no-store')
  assert.equal(headers['referrer-policy'], 'no-referrer')
})
