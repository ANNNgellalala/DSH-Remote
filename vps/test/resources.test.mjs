// Unit tests for the category-ID resource catalogue (node:test, no network).
import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import test from 'node:test'

import {
  DOWNLOAD_PAGE_PATH,
  MAX_FILE_NAME_LENGTH,
  MAX_RETAINED_VERSIONS,
  RESOURCE_BASE_PATH,
  RESOURCE_CATEGORIES,
  RESOURCE_INDEX_PATH,
  applyRetention,
  categoryById,
  categoryDir,
  contentTypeFor,
  fileNameExtension,
  hashCategories,
  hashEntries,
  invalidateHash,
  isCategoryId,
  isInlineSafe,
  isSafeFileName,
  isSafeResourceName,
  legacyKindById,
  parseApkName,
  resolveDownload,
  resolveLegacyResource,
  resolveResource,
  resourceFileUrl,
  scanApkDir,
  scanCategories,
  scanCategory,
  scanDownloads,
  summarizeDownloads,
  summarizeResources
} from '../resources.mjs'
import { makeFixture } from './fixtures.mjs'

const APK_OLD = 'dshremote-1.2.0-13-arm64-release.apk'
const APK_NEW = 'dshremote-1.6.0-32-arm64-release.apk'
const SMARTBILL = 'smartbill-1.0.2-3-universal-debugsign.apk'

function sha256 (value) {
  return crypto.createHash('sha256').update(value).digest('hex')
}

test('category registry exposes the four documented ids', () => {
  assert.deepEqual(RESOURCE_CATEGORIES.map((category) => category.id), ['dsh-remote', 'smartbill', 'relay', 'docs'])
  assert.equal(categoryById('dsh-remote').flavor, 'apk')
  assert.equal(categoryById('smartbill').flavor, 'apk')
  assert.equal(categoryById('relay').flavor, 'file')
  assert.equal(categoryById('docs').flavor, 'file')
  assert.equal(categoryById('apk'), undefined) // retired kind, download alias only
  assert.equal(categoryById('nope'), undefined)
  assert.equal(isCategoryId('dsh-remote'), true)
  assert.equal(isCategoryId('Dsh-Remote'), false)
  assert.equal(isCategoryId('-bad'), false)
})

test('categoryDir maps every id to RESOURCE_DIR/<id>', () => {
  const fixture = makeFixture()
  try {
    for (const category of RESOURCE_CATEGORIES) {
      assert.equal(categoryDir(category.id, fixture.config), path.join(fixture.resourceDir, category.id))
    }
    assert.equal(categoryDir('apk', fixture.config), null)
    assert.equal(categoryDir('nope', fixture.config), null)
    assert.equal(categoryDir('dsh-remote', {}), null)
  } finally {
    fixture.cleanup()
  }
})

test('isSafeResourceName enforces pattern, global extension whitelist and no traversal', () => {
  // valid names across the old kinds are still accepted (global whitelist)
  assert.equal(isSafeResourceName(APK_NEW), true)
  assert.equal(isSafeResourceName(SMARTBILL), true)
  assert.equal(isSafeResourceName('release.TGZ'), true)
  assert.equal(isSafeResourceName('archive.tar.gz'), true)
  assert.equal(isSafeResourceName('README.md'), true)
  assert.equal(isSafeResourceName('photo.webp'), true)
  assert.equal(isSafeResourceName('data.json'), true)
  // extension whitelist / traversal / hidden / odd names
  assert.equal(isSafeResourceName('evil.exe'), false)
  assert.equal(isSafeResourceName('../gateway.env'), false)
  assert.equal(isSafeResourceName('..%2Fgateway.env'), false)
  assert.equal(isSafeResourceName('a/b.md'), false)
  assert.equal(isSafeResourceName('a\\b.md'), false)
  assert.equal(isSafeResourceName('.hidden.md'), false)
  assert.equal(isSafeResourceName('evil..md'), false)
  assert.equal(isSafeResourceName(''), false)
  assert.equal(isSafeResourceName('x'.repeat(MAX_FILE_NAME_LENGTH) + '.md'), false)
  // legacy per-kind helper still applies the historical extension sets
  assert.equal(isSafeFileName('README.md', 'apk'), false)
  assert.equal(isSafeFileName(APK_NEW, 'apk'), true)
  assert.equal(isSafeFileName('archive.tar.gz', 'relay'), true)
  assert.equal(legacyKindById('apk').extensions.includes('.apk'), true)
})

test('extension/content-type helpers cover compound extensions and inline policy', () => {
  assert.equal(fileNameExtension('archive.tar.gz'), '.tar.gz')
  assert.equal(fileNameExtension('archive.TGZ'), '.tgz')
  assert.equal(fileNameExtension('noext'), '')
  assert.equal(contentTypeFor(APK_NEW), 'application/vnd.android.package-archive')
  assert.equal(contentTypeFor('bundle.tgz'), 'application/gzip')
  assert.equal(contentTypeFor('README.md'), 'text/markdown; charset=utf-8')
  assert.equal(contentTypeFor('logo.svg'), 'image/svg+xml')
  assert.equal(contentTypeFor('unknown.xyz'), 'application/octet-stream')
  assert.equal(isInlineSafe('logo.png'), true)
  assert.equal(isInlineSafe('manual.pdf'), true)
  assert.equal(isInlineSafe('logo.svg'), false) // SVG can carry script: attachment only
  assert.equal(isInlineSafe(APK_NEW), false)
})

test('scanCategory lists only whitelisted regular files, newest APK first', async () => {
  const fixture = makeFixture()
  try {
    const dir = fixture.dirFor('dsh-remote')
    fixture.write(dir, APK_OLD, 'old-apk')
    fixture.write(dir, APK_NEW, 'new-apk')
    fixture.write(dir, 'notes.txt', 'not an apk but whitelisted')
    fixture.write(dir, '.hidden.apk', 'hidden')
    const entries = scanCategory('dsh-remote', dir)
    assert.deepEqual(entries.map((entry) => entry.name), [APK_NEW, APK_OLD, 'notes.txt'])
    assert.equal(entries[0].id, 'dsh-remote')
    assert.equal(entries[0].version, '1.6.0')
    assert.equal(entries[0].versionCode, 32)
    assert.equal(entries[0].contentType, 'application/vnd.android.package-archive')
    assert.equal(entries[0].url, `${RESOURCE_BASE_PATH}/dsh-remote/${encodeURIComponent(APK_NEW)}`)
    assert.equal(entries[0].legacyUrl, `${DOWNLOAD_PAGE_PATH}/${encodeURIComponent(APK_NEW)}`)
    const hashed = await hashEntries(entries)
    assert.equal(hashed[0].sha256, sha256('new-apk'))
    assert.equal(hashed[1].sha256, sha256('old-apk'))
    // missing directory is an empty scan, never an error
    assert.deepEqual(scanCategory('dsh-remote', path.join(fixture.root, 'does-not-exist')), [])
    assert.deepEqual(scanCategory('nope', dir), [])
  } finally {
    fixture.cleanup()
  }
})

test('scanCategory sorts file-flavor categories by mtime (newest first)', () => {
  const fixture = makeFixture()
  try {
    const docsDir = fixture.dirFor('docs')
    fixture.write(docsDir, 'older.md', 'old', new Date('2024-01-01T00:00:00Z'))
    fixture.write(docsDir, 'newer.md', 'new', new Date('2025-01-01T00:00:00Z'))
    const entries = scanCategory('docs', docsDir)
    assert.deepEqual(entries.map((entry) => entry.name), ['newer.md', 'older.md'])
  } finally {
    fixture.cleanup()
  }
})

test('symlinks are skipped while scanning and never resolved for download', (t) => {
  const fixture = makeFixture()
  const outside = path.join(fixture.root, 'outside')
  fs.mkdirSync(outside, { recursive: true })
  fs.writeFileSync(path.join(outside, 'secret.apk'), 'secret')
  try {
    try {
      fs.symlinkSync(path.join(outside, 'secret.apk'), path.join(fixture.dirFor('dsh-remote'), 'link.apk'))
    } catch (error) {
      t.skip(`symlink creation is not permitted here (${error?.code ?? error})`)
      return
    }
    assert.deepEqual(scanCategory('dsh-remote', fixture.dirFor('dsh-remote')).map((entry) => entry.name), [])
    assert.equal(resolveResource('dsh-remote', fixture.dirFor('dsh-remote'), 'link.apk'), null)
  } finally {
    fixture.cleanup()
  }
})

test('resolveResource rejects traversal, wrong extension and escapes; accepts real files', () => {
  const fixture = makeFixture()
  try {
    fixture.write(fixture.dirFor('dsh-remote'), APK_NEW, 'apk-bytes')
    fixture.write(fixture.resourceDir, 'gateway.env', 'TOKEN=secret') // outside category dirs
    const dir = fixture.dirFor('dsh-remote')
    const resolved = resolveResource('dsh-remote', dir, APK_NEW)
    assert.ok(resolved)
    assert.equal(resolved.entry.name, APK_NEW)
    assert.equal(resolved.file, fs.realpathSync(path.join(dir, APK_NEW)))
    for (const bad of [
      '../gateway.env',
      '..%2Fgateway.env',
      '..',
      '.',
      '',
      APK_NEW.replace('.apk', '.exe'),
      path.join('..', 'gateway.env')
    ]) {
      assert.equal(resolveResource('dsh-remote', dir, bad), null, `expected null for ${JSON.stringify(bad)}`)
    }
    assert.equal(resolveResource('dsh-remote', dir, 'missing.apk'), null)
    assert.equal(resolveResource('nope', dir, APK_NEW), null)
  } finally {
    fixture.cleanup()
  }
})

test('scanCategories groups all registered ids and tolerates missing directories', () => {
  const fixture = makeFixture()
  try {
    fixture.write(fixture.dirFor('dsh-remote'), APK_NEW, 'apk')
    fixture.write(fixture.dirFor('smartbill'), SMARTBILL, 'bill-apk')
    fixture.write(fixture.dirFor('relay'), 'dsh-remote-relay-0.4.0.tgz', 'relay-bytes')
    fixture.write(fixture.dirFor('docs'), 'README.md', '# hi')
    fs.rmSync(fixture.dirFor('docs'), { recursive: true, force: true })
    const categories = scanCategories(fixture.config)
    assert.deepEqual(categories.map((category) => category.id), ['dsh-remote', 'smartbill', 'relay', 'docs'])
    assert.deepEqual(categories.map((category) => category.kind), ['dsh-remote', 'smartbill', 'relay', 'docs'])
    const byId = Object.fromEntries(categories.map((category) => [category.id, category]))
    assert.equal(byId['dsh-remote'].count, 1)
    assert.equal(byId['dsh-remote'].latest.name, APK_NEW)
    assert.equal(byId.smartbill.latest.name, SMARTBILL)
    assert.equal(byId.relay.latest.name, 'dsh-remote-relay-0.4.0.tgz')
    assert.equal(byId.docs.count, 0) // missing directory
    assert.equal(byId.docs.latest, null)
  } finally {
    fixture.cleanup()
  }
})

test('hashCategories fills sha256 and latest consistently', async () => {
  const fixture = makeFixture()
  try {
    fixture.write(fixture.dirFor('dsh-remote'), APK_OLD, 'old')
    fixture.write(fixture.dirFor('dsh-remote'), APK_NEW, 'new')
    const categories = await hashCategories(scanCategories(fixture.config))
    const apk = categories.find((category) => category.id === 'dsh-remote')
    assert.equal(apk.latest.name, APK_NEW)
    assert.equal(apk.latest.sha256, sha256('new'))
    assert.equal(apk.files.every((file) => /^[0-9a-f]{64}$/.test(file.sha256)), true)
  } finally {
    fixture.cleanup()
  }
})

test('summarizeResources is secret-free, path-free and reports byId', () => {
  const fixture = makeFixture()
  try {
    fixture.write(fixture.dirFor('dsh-remote'), APK_NEW, 'apk')
    fixture.write(fixture.dirFor('docs'), 'README.md', '# hi')
    const summary = summarizeResources(fixture.config)
    assert.equal(summary.page, DOWNLOAD_PAGE_PATH)
    assert.equal(summary.index, RESOURCE_INDEX_PATH)
    assert.equal(summary.total, 2)
    assert.deepEqual(summary.byId, { 'dsh-remote': 1, smartbill: 0, relay: 0, docs: 1 })
    assert.equal(summary.categories.length, 4)
    const dsh = summary.categories.find((category) => category.id === 'dsh-remote')
    assert.equal(dsh.count, 1)
    assert.equal(dsh.latest.file, APK_NEW)
    assert.equal(dsh.latest.version, '1.6.0')
    assert.equal(dsh.latest.versionCode, 32)
    const docs = summary.categories.find((category) => category.id === 'docs')
    assert.equal(docs.latest.version, undefined) // file-flavor categories carry no APK version
    assert.equal(summary.apk.count, 1)
    assert.equal(summary.apk.latest.file, APK_NEW)
    const serialized = JSON.stringify(summary)
    assert.equal(serialized.includes(fixture.root), false, 'summary must not leak absolute paths')
    assert.equal(serialized.includes('token'), false)
  } finally {
    fixture.cleanup()
  }
})

test('summarizeDownloads counts .apk files across categories (legacy dir signature kept)', () => {
  const fixture = makeFixture()
  try {
    fixture.write(fixture.dirFor('dsh-remote'), APK_NEW, 'apk')
    fixture.write(fixture.dirFor('smartbill'), SMARTBILL, 'bill')
    // the extension whitelist is global: an .apk may live in a non-APK category
    fixture.write(fixture.dirFor('docs'), 'stray.apk', 'stray')
    fixture.write(fixture.apkDir, APK_OLD, 'legacy-apk')
    const summary = summarizeDownloads(fixture.config)
    assert.equal(summary.page, DOWNLOAD_PAGE_PATH)
    // all category .apk files, including non-APK-flavoured categories
    assert.equal(summary.count, 3)
    assert.equal(summary.latest.file, APK_NEW) // versionCode 32 wins over the unversioned names
    // the historical directory-string signature still works
    const legacy = summarizeDownloads(fixture.apkDir)
    assert.equal(legacy.count, 1)
    assert.equal(legacy.latest.file, APK_OLD)
    assert.deepEqual(Object.keys(legacy.latest).sort(), ['file', 'sizeBytes', 'version', 'versionCode'])
    assert.equal(scanDownloads(fixture.apkDir).length, 1)
    assert.equal(scanApkDir(fixture.apkDir)[0].name, APK_OLD)
    assert.equal(resolveDownload(fixture.apkDir, APK_OLD)?.entry.name, APK_OLD)
    assert.deepEqual(summarizeDownloads(path.join(fixture.root, 'missing')), {
      page: DOWNLOAD_PAGE_PATH,
      count: 0,
      latest: null
    })
  } finally {
    fixture.cleanup()
  }
})

test('applyRetention keeps 3 newest versions when keepVersions is on', () => {
  const fixture = makeFixture()
  try {
    const dir = fixture.dirFor('dsh-remote')
    // oldest -> newest by mtime
    const names = [
      'dshremote-1.2.0-13-arm64-release.apk',
      'dshremote-1.3.0-23-arm64-release.apk',
      'dshremote-1.4.0-25-arm64-release.apk',
      'dshremote-1.5.0-29-arm64-release.apk',
      'dshremote-1.6.0-32-arm64-release.apk'
    ]
    names.forEach((name, index) => {
      fixture.write(dir, name, `apk-${index}`, new Date(Date.UTC(2026, 0, 1 + index)))
    })
    const result = applyRetention('dsh-remote', dir, true)
    assert.deepEqual(result.retained, [names[4], names[3], names[2]])
    assert.deepEqual(result.pruned.sort(), [names[0], names[1]])
    assert.deepEqual(fs.readdirSync(dir).sort(), [names[2], names[3], names[4]].sort())
    // idempotent: nothing left to prune
    const again = applyRetention('dsh-remote', dir, true)
    assert.deepEqual(again.pruned, [])
    assert.equal(fs.readdirSync(dir).length, MAX_RETAINED_VERSIONS)
  } finally {
    fixture.cleanup()
  }
})

test('applyRetention keeps only the uploaded version when keepVersions is off', () => {
  const fixture = makeFixture()
  try {
    const dir = fixture.dirFor('dsh-remote')
    const names = ['dshremote-1.4.0-25-arm64-release.apk', 'dshremote-1.5.0-29-arm64-release.apk', 'dshremote-1.6.0-32-arm64-release.apk']
    names.forEach((name, index) => fixture.write(dir, name, `apk-${index}`, new Date(Date.UTC(2026, 0, 1 + index))))
    const result = applyRetention('dsh-remote', dir, false)
    assert.deepEqual(result.retained, [names[2]])
    assert.deepEqual(result.pruned.sort(), [names[0], names[1]].sort())
    assert.deepEqual(fs.readdirSync(dir), [names[2]])
    // unknown category/id is a no-op, never a crash
    assert.deepEqual(applyRetention('nope', dir, false), { retained: [], pruned: [], errors: [] })
    assert.deepEqual(applyRetention('dsh-remote', path.join(fixture.root, 'missing'), true), { retained: [], pruned: [], errors: [] })
  } finally {
    fixture.cleanup()
  }
})

test('applyRetention ignores hidden temp files and non-whitelisted files', () => {
  const fixture = makeFixture()
  try {
    const dir = fixture.dirFor('dsh-remote')
    fixture.write(dir, '.upload-deadbeef-x.apk', 'temp', new Date(Date.UTC(2026, 5, 1)))
    fixture.write(dir, 'leftover.txt.tmp', 'junk', new Date(Date.UTC(2026, 5, 2)))
    for (let index = 0; index < 4; index++) {
      fixture.write(dir, `dshremote-1.${index}.0-${index}-arm64-release.apk`, `a${index}`, new Date(Date.UTC(2026, 0, 1 + index)))
    }
    applyRetention('dsh-remote', dir, true)
    // temp/junk files are not white-listed, so retention leaves them alone
    assert.equal(fs.existsSync(path.join(dir, '.upload-deadbeef-x.apk')), true)
    assert.equal(fs.existsSync(path.join(dir, 'leftover.txt.tmp')), true)
    const kept = fs.readdirSync(dir).filter((name) => name.endsWith('.apk') && !name.startsWith('.'))
    assert.equal(kept.length, MAX_RETAINED_VERSIONS)
  } finally {
    fixture.cleanup()
  }
})

test('resolveLegacyResource finds legacy download paths across categories', () => {
  const fixture = makeFixture()
  try {
    fixture.write(fixture.dirFor('dsh-remote'), APK_NEW, 'apk')
    fixture.write(fixture.dirFor('smartbill'), SMARTBILL, 'bill')
    fixture.write(fixture.apkDir, APK_OLD, 'legacy-only-apk')
    fixture.write(fixture.dirFor('docs'), 'README.md', '# hi')
    // apk kind alias: new locations + legacy directory fallback
    assert.equal(resolveLegacyResource('apk', fixture.config, APK_NEW)?.entry.name, APK_NEW)
    assert.equal(resolveLegacyResource('apk', fixture.config, SMARTBILL)?.entry.name, SMARTBILL)
    assert.equal(resolveLegacyResource('apk', fixture.config, APK_OLD)?.entry.name, APK_OLD)
    // legacy image alias only accepts image extensions
    assert.equal(resolveLegacyResource('image', fixture.config, 'README.md'), null)
    // traversal and unknown names
    assert.equal(resolveLegacyResource('apk', fixture.config, '../gateway.env'), null)
    assert.equal(resolveLegacyResource('apk', fixture.config, 'nope.apk'), null)
    assert.equal(resolveLegacyResource('nokind', fixture.config, APK_NEW), null)
  } finally {
    fixture.cleanup()
  }
})

test('parseApkName only accepts the release naming scheme', () => {
  assert.deepEqual(parseApkName(APK_NEW), { version: '1.6.0', versionCode: 32 })
  assert.deepEqual(parseApkName('dshremote-1.0.0-10-arm64-release.apk'), { version: '1.0.0', versionCode: 10 })
  assert.equal(parseApkName('debug.apk'), undefined)
  assert.equal(parseApkName(SMARTBILL), undefined)
  assert.equal(parseApkName('dshremote-1.3.0-arm64-release.apk'), undefined)
})

test('invalidateHash drops a stale cache entry after the file changes', async () => {
  const fixture = makeFixture()
  try {
    const docsDir = fixture.dirFor('docs')
    const docsFile = fixture.write(docsDir, 'change.md', 'one')
    const stat = fs.statSync(docsFile)
    const entry = { file: docsFile, updatedAtMs: stat.mtimeMs, sizeBytes: stat.size }
    assert.equal((await hashEntries([entry]))[0].sha256, sha256('one'))
    // overwrite with same-size content; a stale cache key would hide it
    fs.writeFileSync(docsFile, 'two')
    assert.equal((await hashEntries([entry]))[0].sha256, sha256('one'))
    invalidateHash(docsFile)
    assert.equal((await hashEntries([entry]))[0].sha256, sha256('two'))
  } finally {
    fixture.cleanup()
  }
})

test('resourceFileUrl is the canonical relative URL and escapes names', () => {
  assert.equal(resourceFileUrl('docs', 'README.md'), '/__gateway/resources/docs/README.md')
  assert.equal(resourceFileUrl('dsh-remote', 'a b.apk'), '/__gateway/resources/dsh-remote/a%20b.apk')
})
