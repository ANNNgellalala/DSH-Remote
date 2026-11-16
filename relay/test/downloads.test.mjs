// Unit tests for APK download-catalogue path resolution (node:test, no deps).
import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import { resolveDownloadDir } from '../src/downloads.mjs'

function makeTree (layout) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'dsh-relay-downloads-test-'))
  const repo = path.join(root, 'repo')
  fs.mkdirSync(path.join(repo, 'relay'), { recursive: true })
  fs.mkdirSync(path.join(repo, 'dist', 'apk'), { recursive: true })
  if (layout === 'bundle') {
    fs.mkdirSync(path.join(repo, 'dist', 'dsh-remote-relay-9.9.9'), { recursive: true })
  }
  return repo
}

test('resolveDownloadDir resolves the dev layout (<repo>/relay)', () => {
  const repo = makeTree('dev')
  try {
    assert.equal(resolveDownloadDir(undefined, path.join(repo, 'relay')), path.join(repo, 'dist', 'apk'))
  } finally {
    fs.rmSync(path.dirname(repo), { recursive: true, force: true })
  }
})

test('resolveDownloadDir resolves the bundle layout (<repo>/dist/dsh-remote-relay-x)', () => {
  const repo = makeTree('bundle')
  try {
    const bundleDir = path.join(repo, 'dist', 'dsh-remote-relay-9.9.9')
    assert.equal(resolveDownloadDir(undefined, bundleDir), path.join(repo, 'dist', 'apk'))
  } finally {
    fs.rmSync(path.dirname(repo), { recursive: true, force: true })
  }
})

test('an explicit configured directory wins', () => {
  const repo = makeTree('bundle')
  try {
    const custom = path.join(repo, 'custom-apk')
    fs.mkdirSync(custom, { recursive: true })
    assert.equal(resolveDownloadDir(custom, path.join(repo, 'dist', 'dsh-remote-relay-9.9.9')), custom)
  } finally {
    fs.rmSync(path.dirname(repo), { recursive: true, force: true })
  }
})

test('falls back to the first candidate when nothing exists', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'dsh-relay-downloads-empty-'))
  try {
    const pkgRoot = path.join(root, 'a', 'b')
    fs.mkdirSync(pkgRoot, { recursive: true })
    const resolved = resolveDownloadDir(undefined, pkgRoot)
    assert.equal(path.isAbsolute(resolved), true)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})
