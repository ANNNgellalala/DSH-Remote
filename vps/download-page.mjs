// Public resource download page served by the gateway (v0.4.0).
//
// Static HTML with an inline stylesheet, strict CSP, no external assets and no
// scripts: the page is reachable from the open internet and must not become a
// content-injection surface. File metadata comes from resources.mjs (strict
// whitelist only); directory paths are shown for operators but nothing is
// listable.
//
// Layout: one card per resource category ID (dsh-remote / smartbill / relay /
// docs) instead of the old file-type tabs, so the two apps are never mixed in
// one list. Each card keeps a "download latest" button plus a table of the
// retained versions. Empty categories are hidden; a gateway with no files at
// all shows a friendly empty state. Rendered fresh on every request, so a file
// dropped into a category directory shows up without a restart.
//
// On narrow screens every table row turns into a stacked card (file name +
// selectable sha256 block + version / size / updated pills) so 64-char hashes
// and long APK names never get squeezed into a few characters per line.
// Everything stays CSS-only (no scripts, no external assets) because of the
// strict CSP.
import { DOWNLOAD_PAGE_PATH, RESOURCE_BASE_PATH, RESOURCE_INDEX_PATH } from './resources.mjs'

function escapeHtml (value) {
  return String(value ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;')
}

function formatSize (bytes) {
  if (!Number.isFinite(bytes) || bytes < 0) return '?'
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KiB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(2)} MiB`
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GiB`
}

/** Compact, unambiguous timestamp: `2026-11-11 08:38 UTC` (falls back to the raw value). */
function formatUpdatedAt (value) {
  const text = String(value ?? '')
  const date = new Date(text)
  if (Number.isNaN(date.getTime())) return text
  const pad = (part) => String(part).padStart(2, '0')
  return `${date.getUTCFullYear()}-${pad(date.getUTCMonth() + 1)}-${pad(date.getUTCDate())} ` +
    `${pad(date.getUTCHours())}:${pad(date.getUTCMinutes())} UTC`
}

function downloadHref (file) {
  if (file && typeof file.url === 'string' && file.url.startsWith('/')) return file.url
  return `${DOWNLOAD_PAGE_PATH}/${encodeURIComponent(String(file?.name ?? ''))}`
}

function anchorId (categoryId) {
  return `category-${String(categoryId ?? '').replace(/[^A-Za-z0-9_-]/g, '')}`
}

/** One labelled, selectable sha256 block (wraps by character, not by squashing the cell). */
function hashLine (sha256) {
  return `<span class="hash-line"><span class="hash-label">sha256</span><code class="hash">${escapeHtml(sha256 ?? 'unavailable')}</code></span>`
}

/**
 * The version cell of an APK row. Files that do not follow the release naming
 * scheme fall back to their file name as "version"; that duplicates the file
 * column and overflows a fixed-width cell, so it becomes an em dash with the
 * full value in the tooltip.
 */
function versionCell (file) {
  const name = String(file.name ?? '')
  const version = String(file.version ?? '')
  const hasVersion = version !== '' && version !== name && version.length <= 32
  const label = hasVersion ? `v${escapeHtml(version)}` : '—'
  const code = hasVersion && file.versionCode !== null && file.versionCode !== undefined
    ? ` <span class="cell-muted">(${escapeHtml(String(file.versionCode))})</span>`
    : ''
  return `<td class="version-cell" data-label="版本"><span class="version-text" title="${escapeHtml(hasVersion ? version : name)}">${label}${code}</span></td>`
}

export function downloadPageHeaders (contentType, extra = {}) {
  return {
    'content-type': contentType,
    'cache-control': 'no-store',
    'referrer-policy': 'no-referrer',
    'x-content-type-options': 'nosniff',
    'x-frame-options': 'DENY',
    'content-security-policy': [
      "default-src 'none'",
      "style-src 'unsafe-inline'",
      "img-src 'none'",
      "base-uri 'none'",
      "form-action 'none'",
      "frame-ancestors 'none'"
    ].join('; '),
    ...extra
  }
}

function fileRows (files, { withVersion = false } = {}) {
  return files.map((file) => {
    return `<tr>
      ${withVersion ? versionCell(file) : ''}
      <td class="file-cell"><a class="file-link" href="${escapeHtml(downloadHref(file))}">${escapeHtml(file.name)}</a>${hashLine(file.sha256)}</td>
      <td class="size-cell" data-label="大小">${formatSize(file.sizeBytes)}</td>
      <td class="date-cell" data-label="更新">${escapeHtml(formatUpdatedAt(file.updatedAt))}</td>
    </tr>`
  }).join('\n')
}

function filesTable (files, { withVersion = false } = {}) {
  return `<table>
    <thead><tr>${withVersion ? '<th scope="col" class="col-version">版本</th>' : ''}<th scope="col" class="col-file">文件 · sha256</th><th scope="col" class="col-size">大小</th><th scope="col" class="col-date">更新时间</th></tr></thead>
    <tbody>
${fileRows(files, { withVersion })}
    </tbody>
  </table>`
}

function latestLabel (category) {
  return category.flavor === 'apk' ? '下载最新版 APK' : '下载最新文件'
}

function categoryCard (category) {
  const files = Array.isArray(category.files) ? category.files : []
  const withVersion = category.flavor === 'apk'
  const latest = category.latest ?? files[0] ?? null
  const button = latest
    ? `<p><a class="button" href="${escapeHtml(downloadHref(latest))}">${latestLabel(category)}</a></p>`
    : ''
  return `<div class="card" id="${anchorId(category.id)}">
  <div class="eyebrow">${escapeHtml(category.title)} · ${files.length} 个文件 <code class="category-id">${escapeHtml(category.id)}</code></div>
  <p class="desc">${escapeHtml(category.description)}</p>
  ${button}
  ${filesTable(files, { withVersion })}
</div>`
}

/**
 * @param {{
 *   categories?: Array<{id:string,title:string,description:string,flavor:string,dir:string,count:number,latest:object|null,files:Array<object>}>,
 *   files?: Array<object>,       // legacy APK-only model (still supported)
 *   dir?: string,                // legacy APK directory
 *   resourceDir?: string,
 *   gatewayVersion: string,
 *   gatewayName: string,
 *   fingerprint: string,
 *   baseUrl: string,
 *   tunnels: number,
 *   generatedAt: string
 * }} model
 */
export function renderDownloadPage (model) {
  const {
    gatewayVersion, gatewayName, fingerprint, baseUrl, tunnels, generatedAt
  } = model
  const categories = Array.isArray(model.categories) && model.categories.length > 0
    ? model.categories
    : [{
        id: 'apk',
        title: 'Android 安装包（APK）',
        description: 'Release 签名 APK。',
        flavor: 'apk',
        dir: model.dir,
        files: Array.isArray(model.files) ? model.files : [],
        count: Array.isArray(model.files) ? model.files.length : 0,
        latest: Array.isArray(model.files) ? (model.files[0] ?? null) : null
      }]
  const total = categories.reduce((sum, category) => sum + (Array.isArray(category.files) ? category.files.length : 0), 0)
  const visible = categories.filter((category) => Array.isArray(category.files) && category.files.length > 0)
  const categoryCount = visible.length

  const cards = visible.map(categoryCard).join('\n\n')

  const emptyBanner = total === 0
    ? `<div class="warn">
  <strong>当前没有任何可下载资源</strong>
  <p class="muted">资源根目录：<code>${escapeHtml(model.resourceDir ?? '')}</code>。用上传接口发布资源，例如：
  <code>pwsh -File scripts/upload-resource.ps1 -Id dsh-remote -KeepVersions -File &lt;本地文件&gt;</code></p>
</div>`
    : ''

  return `<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="referrer" content="no-referrer">
<title>资源下载中心</title>
<style>
  :root { color-scheme: light dark; }
  *, *::before, *::after { box-sizing: border-box; }
  html { -webkit-text-size-adjust: 100%; }
  body { margin: 0 auto; padding: 20px clamp(14px, 4vw, 24px) 48px; max-width: 860px; font: 15px/1.6 -apple-system, "Segoe UI", system-ui, sans-serif; }
  h1 { font-size: 22px; margin: 6px 0 2px; }
  h2 { font-size: 16px; margin: 0 0 4px; }
  .sub { color: #777; margin: 0 0 16px; font-size: 13px; }
  .card { border: 1px solid #8884; border-radius: 12px; padding: 16px; margin: 14px 0; }
  .eyebrow { font-size: 12px; letter-spacing: .08em; color: #777; text-transform: uppercase; }
  .category-id { font-size: 11px; letter-spacing: 0; text-transform: none; padding: 1px 6px; border-radius: 6px; background: #8882; }
  .desc { margin: 6px 0 10px; color: #666; font-size: 13.5px; }
  .version { font-size: 30px; line-height: 1.2; font-weight: 700; margin: 4px 0 2px; }
  .version .code { font-size: 14px; color: #777; font-weight: 400; }
  .file { margin: 6px 0 2px; font-family: ui-monospace, Consolas, monospace; font-size: 13.5px; line-height: 1.5; word-break: break-all; overflow-wrap: anywhere; }
  .meta { margin: 2px 0; color: #666; font-size: 13px; }
  .muted { color: #888; font-size: 12.5px; }
  .mono, code, pre { font-family: ui-monospace, Consolas, monospace; font-size: 12.5px; overflow-wrap: anywhere; }
  pre { background: #8881; padding: 10px; border-radius: 8px; overflow-x: auto; }
  a { color: #2f6feb; }
  a.button { display: inline-block; margin-top: 10px; padding: 11px 20px; border-radius: 10px; background: #2f6feb; color: #fff; text-decoration: none; font-weight: 600; }
  .hash-line { display: block; margin-top: 4px; min-width: 0; }
  .hash-label { font-size: 11px; letter-spacing: .06em; color: #8a8a8a; text-transform: uppercase; margin-right: 6px; }
  .hash { font-size: 11.5px; line-height: 1.55; word-break: break-all; overflow-wrap: anywhere; -webkit-user-select: all; user-select: all; }
  /* In table mode the column header already names sha256, so the inline label
     would only push the hash onto a second line. */
  @media (min-width: 720px) { .hash-label { display: none; } }
  table { width: 100%; table-layout: fixed; border-collapse: collapse; font-size: 13px; }
  /* Fixed columns keep the file name + sha256 block wide enough at every table
     width; the 文件 column takes whatever is left. */
  .col-version { width: 96px; }
  .col-size { width: 84px; }
  .col-date { width: 160px; }
  th, td { text-align: left; padding: 9px 8px; border-top: 1px solid #8882; vertical-align: top; }
  thead th { border-top: 0; padding: 0 8px 6px; color: #777; font-weight: 600; font-size: 11.5px; letter-spacing: .04em; text-transform: uppercase; }
  .version-cell, .size-cell, .date-cell, .nowrap { white-space: nowrap; }
  .version-text { display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .file-cell { min-width: 0; }
  .file-link { display: block; font-weight: 500; overflow-wrap: anywhere; }
  .cell-muted { color: #888; }
  .empty { border-style: dashed; }
  .empty-line { margin: 8px 0 2px; }
  ul { padding-left: 20px; }
  li { margin: 4px 0; }
  .warn { border: 1px solid #d9a021; background: #d9a02114; border-radius: 12px; padding: 12px 16px; margin: 14px 0; }
  .warn strong { color: #b07d00; }
  .grid { display: grid; grid-template-columns: max-content minmax(0, 1fr); gap: 4px 14px; font-size: 13px; }
  .grid div:nth-child(odd) { color: #777; }
  .footer { color: #888; font-size: 12.5px; line-height: 1.9; }
  /* Narrow screens: rows become cards; nothing is squeezed into a table column. */
  @media (max-width: 719px) {
    body { padding: 14px 12px 36px; }
    h1 { font-size: 19px; }
    .card { padding: 14px 12px; border-radius: 10px; margin: 12px 0; }
    table, thead, tbody, tr, td, th { display: block; }
    thead { position: absolute; width: 1px; height: 1px; margin: -1px; padding: 0; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
    tbody tr { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; padding: 10px; border: 1px solid #8883; border-radius: 10px; }
    tbody tr + tr { margin-top: 10px; }
    td { border: 0; padding: 0; }
    .file-cell { order: 1; flex: 1 0 100%; min-width: 0; }
    .file-link { font-weight: 600; }
    .hash { font-size: 11.5px; }
    .version-cell, .size-cell, .date-cell { order: 2; border: 0; background: #8881; border-radius: 999px; padding: 1px 8px; font-size: 11.5px; color: #666; white-space: nowrap; }
    .version-cell::before, .size-cell::before, .date-cell::before { content: attr(data-label) " "; color: #888; }
    .version-text { display: inline; overflow: visible; text-overflow: clip; }
    .grid { grid-template-columns: minmax(0, 1fr); gap: 0; }
    .grid div:nth-child(odd) { margin-top: 8px; font-size: 11.5px; }
    .grid div:nth-child(even) { margin-bottom: 2px; }
    a.button { display: block; text-align: center; margin-top: 12px; }
  }
</style>
</head>
<body>
<h1>资源下载中心</h1>
<p class="sub">${escapeHtml(gatewayName)} v${escapeHtml(gatewayVersion)} · 在线桌面 ${escapeHtml(String(tunnels))} · ${categoryCount} 个分类 / 共 ${total} 个可下载文件 · 生成于 ${escapeHtml(formatUpdatedAt(generatedAt))}</p>

${emptyBanner}

${cards}

<div class="card">
  <div class="eyebrow">网关信息（用于 App 配对 / 证书固定）</div>
  <div class="grid">
    <div>服务器</div><div class="mono">${escapeHtml(baseUrl)}</div>
    <div>SPKI SHA-256</div><div class="mono">${escapeHtml(fingerprint)}</div>
    <div>身份接口</div><div class="mono">/__gateway/identity</div>
    <div>资源索引</div><div class="mono"><a href="${RESOURCE_INDEX_PATH}">${RESOURCE_INDEX_PATH}</a>（JSON，可加 <code>?id=dsh-remote</code>）</div>
  </div>
  <p class="muted">App 配对信息（token）由桌面 relay 的管理页生成；本页不含任何 token，也不提供上传入口。</p>
</div>

<div class="card">
  <div class="eyebrow">安装提示（APK）</div>
  <ol>
    <li>手机浏览器提示证书不安全时：选择「高级 / 继续前往」（网关使用自签证书，App 内以 SPKI 指纹固定，属预期行为）。</li>
    <li>下载完成后点通知栏/下载列表里的 APK 安装。若显示「安装增强防护」拦截：点「更多」→「开始深度扫描」→ 完成后「继续安装」；部分 ROM 需为浏览器开启「允许安装未知应用」。</li>
    <li>安装后在 App 里扫码 / 粘贴桌面 relay 管理页给出的配对 URI（网关模式为 <code>dshremote://pair?v=2&amp;mode=gateway...</code>）。</li>
  </ol>
</div>

<div class="warn">
  <strong>安全提示</strong>
  <ul>
    <li>本页只列出各分类目录中<strong>通过白名单校验</strong>的普通文件，不允许读取其他文件；请只安装/使用自己（或维护者）发布的资源。</li>
    <li>下载前可在电脑核对 sha256：<code>Get-FileHash &lt;文件&gt; -Algorithm SHA256</code>，并与本页 sha256 对比。</li>
    <li>每个分类的版本数由上传时的 <code>keepVersions</code> 选项决定：开启时保留最近 3 个版本，关闭时只保留最新 1 个。</li>
    <li>上传/删除只接受 <code>Authorization: Bearer &lt;UPLOAD_TOKEN&gt;</code>；页面与 JSON 索引都不会出现令牌。</li>
  </ul>
</div>

<p class="footer">资源根目录：<code>${escapeHtml(model.resourceDir ?? '')}</code> · 下载接口前缀 <code>${RESOURCE_BASE_PATH}/&lt;id&gt;/&lt;name&gt;</code> · 旧 APK 兼容直链 <code>${DOWNLOAD_PAGE_PATH}/&lt;file&gt;.apk</code> · 上传：<code>pwsh -File scripts/upload-resource.ps1 -Id dsh-remote -KeepVersions -File &lt;本地文件&gt;</code> · 同步 APK：<code>pwsh -File scripts/sync-apk.ps1</code> · 架构与安全说明见 <code>docs/vps-gateway.md</code>。</p>
</body>
</html>
`
}
