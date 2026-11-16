// LAN-accessible APK download page. Lists only the whitelisted release APKs
// found by downloads.mjs; no directory listing, no scripts (strict CSP).
//
// Layout mirrors vps/download-page.mjs: a four-column table on wide screens and
// stacked, card-style rows below 720px (file name + selectable sha256 block +
// version / size / updated pills), so long APK names and 64-char hashes stay
// readable on phones instead of being squeezed into narrow table columns. The
// page must stay CSS-only: strict CSP, no scripts, no external assets.
import { DOWNLOAD_PAGE_PATH } from './downloads.mjs'
import { escapeHtml } from './pair-page.mjs'

function formatMiB (bytes) {
  if (!Number.isFinite(bytes)) return '?'
  return `${(bytes / (1024 * 1024)).toFixed(2)} MiB`
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

function downloadHref (name) {
  return `${DOWNLOAD_PAGE_PATH}/${encodeURIComponent(name)}`
}

function hashLine (sha256) {
  return `<span class="hash-line"><span class="hash-label">sha256</span><code class="hash">${escapeHtml(sha256)}</code></span>`
}

/**
 * The version cell of a history row. Non-release APKs (e.g. debug builds) fall
 * back to their file name as "version"; that duplicates the file column and
 * overflows a fixed-width cell, so it becomes an em dash with the full value in
 * the tooltip. Real versions are ellipsized so a long tag cannot bleed into the
 * next column.
 */
function versionLabel (file, { isLatest = false } = {}) {
  const name = String(file.name ?? '')
  const version = String(file.version ?? '')
  const hasVersion = version !== '' && version !== name && version.length <= 32
  const label = hasVersion
    ? (isLatest ? `最新 <span class="cell-muted">(v${escapeHtml(version)})</span>` : `v${escapeHtml(version)}`)
    : (isLatest ? '最新' : '—')
  return `<span class="version-text" title="${escapeHtml(hasVersion ? version : name)}">${label}</span>`
}

export function renderDownloadPage ({ dir, files, relayVersion, dshVersion, generatedAt }) {
  const latest = files[0] ?? null
  const rows = files.map((file, index) => {
    return `<tr>
      <td class="version-cell" data-label="版本">${versionLabel(file, { isLatest: index === 0 })}</td>
      <td class="file-cell"><a class="file-link" href="${downloadHref(file.name)}">${escapeHtml(file.name)}</a>${hashLine(file.sha256)}</td>
      <td class="size-cell" data-label="大小">${formatMiB(file.sizeBytes)}</td>
      <td class="date-cell" data-label="更新">${escapeHtml(formatUpdatedAt(file.updatedAt))}</td>
    </tr>`
  }).join('\n')

  const latestVersion = String(latest?.version ?? '')
  const latestVersionLabel = latestVersion !== '' && latestVersion !== String(latest?.name ?? '')
    ? `v${escapeHtml(latestVersion)}`
    : '当前 APK'
  const latestCard = latest
    ? `<div class="card latest">
        <div class="eyebrow">最新版本</div>
        <div class="version">${latestVersionLabel}${latest.versionCode !== null && latest.versionCode !== undefined ? ` <span class="code">(${escapeHtml(String(latest.versionCode))})</span>` : ''}</div>
        <p class="file">${escapeHtml(latest.name)}</p>
        <p class="meta">${formatMiB(latest.sizeBytes)} · ${escapeHtml(formatUpdatedAt(latest.updatedAt))}</p>
        ${hashLine(latest.sha256)}
        <p><a class="button" href="${downloadHref(latest.name)}">下载最新版 APK</a></p>
      </div>`
    : `<div class="card latest empty">
        <div class="eyebrow">暂无可下载 APK</div>
        <p>下载目录里还没有构建产物：<code>${escapeHtml(dir)}</code></p>
        <p>在电脑上构建后刷新本页：</p>
        <pre>pwsh -File scripts/build-release-apk.ps1</pre>
        <p class="muted">页面会自动扫描目录，无需重启 relay。</p>
      </div>`

  return `<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="referrer" content="no-referrer">
<title>DSH Remote — APK 下载</title>
<style>
  :root { color-scheme: light dark; }
  *, *::before, *::after { box-sizing: border-box; }
  html { -webkit-text-size-adjust: 100%; }
  body { margin: 0 auto; padding: 20px clamp(14px, 4vw, 24px) 48px; max-width: 860px; font: 15px/1.6 -apple-system, "Segoe UI", system-ui, sans-serif; }
  h1 { font-size: 22px; margin: 6px 0 2px; }
  .sub { color: #777; margin: 0 0 16px; font-size: 13px; }
  .card { border: 1px solid #8884; border-radius: 12px; padding: 16px; margin: 14px 0; }
  .eyebrow { font-size: 12px; letter-spacing: .08em; color: #777; text-transform: uppercase; }
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
  .cell-muted { color: #888; font-weight: 400; }
  .empty { border-style: dashed; }
  ul { padding-left: 20px; }
  li { margin: 4px 0; }
  .warn { border: 1px solid #d9a021; background: #d9a02114; border-radius: 12px; padding: 12px 16px; margin: 14px 0; }
  .warn strong { color: #b07d00; }
  /* Narrow screens: history rows become cards; nothing is squeezed into a table column. */
  @media (max-width: 719px) {
    body { padding: 14px 12px 36px; }
    h1 { font-size: 19px; }
    .card { padding: 14px 12px; border-radius: 10px; margin: 12px 0; }
    .version { font-size: 26px; }
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
    a.button { display: block; text-align: center; margin-top: 12px; }
  }
</style>
</head>
<body>
<h1>DSH Remote — APK 下载</h1>
<p class="sub">手机与电脑同一 Wi-Fi / VPN 时可直接下载安装。relay ${escapeHtml(relayVersion ?? '?')} · DSH ${escapeHtml(dshVersion ?? '?')} · 生成于 ${escapeHtml(formatUpdatedAt(generatedAt))}</p>

${latestCard}

<div class="card">
  <div class="eyebrow">安装提示</div>
  <ol>
    <li>手机浏览器提示证书不安全时：选择「高级 / 继续前往」（本服务使用自签证书 + App 内 SPKI pin，属于预期行为）。</li>
    <li>下载完成后点通知栏/下载列表里的 APK 安装。若显示「安装增强防护」拦截：点「更多」→「开始深度扫描」→ 完成后「继续安装」；部分 ROM 需为浏览器开启「允许安装未知应用」。</li>
    <li>安装完成后回到电脑运行 <code>pwsh -File scripts/show-pairing.ps1</code>，用手机系统相机扫码配对（配对页只在电脑本机可访问）。</li>
  </ol>
</div>

<div class="warn">
  <strong>安全提示</strong>
  <ul>
    <li>本页只列出下载目录中的 <code>*.apk</code>，不允许读取其他文件；请只安装你（或维护者）自己构建的 APK。</li>
    <li>建议安装前在本机核对 sha256：<code>Get-FileHash &lt;apk&gt; -Algorithm SHA256</code>。</li>
    <li>APK 是 Release 签名包（arm64-v8a）；模拟器/非 arm64 设备请改用 Debug 包。</li>
  </ul>
</div>

<div class="card">
  <div class="eyebrow">历史版本</div>
  ${files.length > 0
    ? `<table>
    <thead><tr><th scope="col" class="col-version">版本</th><th scope="col" class="col-file">文件 · sha256</th><th scope="col" class="col-size">大小</th><th scope="col" class="col-date">更新时间</th></tr></thead>
    <tbody>
${rows}
    </tbody>
  </table>`
    : '<p class="muted">暂无文件。</p>'}
</div>

<p class="muted">下载目录：<code>${escapeHtml(dir)}</code> · 页面自动扫描该目录取最新版本；更详细的安装/升级说明见仓库 <code>docs/install.md</code> 与 <code>docs/upgrade.md</code>。</p>
</body>
</html>
`
}

/** Headers for the download page (and download error bodies). */
export function downloadPageHeaders (contentType) {
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
    ].join('; ')
  }
}
