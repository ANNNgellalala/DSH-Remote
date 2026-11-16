// Loopback-only pairing management page.
//
// v1 (LAN): shows the LAN pairing URI/QR and the process launchToken.
// v2 (gateway): shows desktop name, gateway server URL, remote token (masked,
// reveal/copy), the v2 QR (host=gateway, token=remoteToken, fp=gateway cert)
// and keeps the LAN URI as an alternative. Rotating the token is a loopback-only
// POST handled by proxy.mjs (`/__relay/pair/rotate`).
//
// The page is served only to loopback clients; the token lives inside the QR/URI
// by design and must never be written to a log. No inline script: the same-origin
// /__relay/pair.js drives reveal/copy/rotate so CSP can stay strict.

export const PAIR_PAGE_PATH = '/__relay/pair'
export const PAIR_ROTATE_PATH = '/__relay/pair/rotate'
export const PAIR_QR_ASSET_PATH = '/__relay/qrcode.js'
export const PAIR_JS_ASSET_PATH = '/__relay/pair.js'
/** Manual re-run of the startup protocol self-check (POST, loopback only). */
export const SELF_CHECK_PATH = '/__relay/probe'
/** Persisted relay-server settings (GET/POST, loopback only). */
export const CONFIG_PATH = '/__relay/config'

/** True only for a genuinely local TCP peer (never trust forwarded headers). */
export function isLoopbackAddress (address) {
  if (typeof address !== 'string' || address.length === 0) return false
  const plain = address.startsWith('::ffff:') ? address.slice('::ffff:'.length) : address
  if (plain === '::1' || plain === 'localhost') return true
  return /^127(?:\.\d{1,3}){3}$/.test(plain)
}

export function escapeHtml (value) {
  return String(value ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;')
}

export function maskToken (token) {
  const text = String(token ?? '')
  if (text.length <= 8) return '****'
  return `${text.slice(0, 4)}…${text.slice(-4)}`
}

export function renderPairingPage (pairing, options = {}) {
  const active = pairing?.active === true
  const gatewayMode = Boolean(pairing?.gateway && pairing.gateway.url)
  const uri = typeof pairing?.pairingUri === 'string' ? pairing.pairingUri : ''
  const localUri = typeof pairing?.localPairingUri === 'string' ? pairing.localPairingUri : (!gatewayMode ? uri : '')
  const token = typeof pairing?.token === 'string' ? pairing.token : ''
  const fp = typeof pairing?.fp === 'string' ? pairing.fp : ''
  const name = pairing?.name ?? ''
  const relayVersion = pairing?.relayVersion ?? 'unknown'
  const dshVersion = pairing?.dshVersion ?? 'unknown'
  const updatedAt = pairing?.updatedAt ?? 'unknown'
  const localHostPort = pairing?.localHost ? `${pairing.localHost}:${pairing.localPort}` : `${pairing?.host ?? '?'}:${pairing?.port ?? '?'}`
  const remoteHostPort = `${pairing?.host ?? '?'}:${pairing?.port ?? '?'}`
  const gatewayUrl = pairing?.gateway?.url ?? ''
  const gatewayOnline = pairing?.gateway?.online === true
  const gatewayFp = pairing?.gateway?.fp ?? fp
  const errorText = pairing?.error?.message ? String(pairing.error.message) : ''
  const checkedAt = options.now instanceof Date ? options.now.toISOString() : new Date().toISOString()
  const config = options.config && typeof options.config === 'object' ? options.config : null
  const configGateway = config?.gateway && typeof config.gateway === 'object' ? config.gateway : null
  const configSourceLabel = config?.source === 'file'
    ? '持久化配置文件'
    : (config?.source === 'patch' ? 'DSH profile patch（旧配置，首次保存后迁移到文件）' : '未配置')

  const recheckRow = `<div class="row">
      <button type="button" class="mini" id="recheck">重新自检</button>
      <span class="hint" id="recheck-status"></span>
    </div>`

  const statusBlock = active
    ? `<p class="ok">配对有效（active=true）· relay ${escapeHtml(relayVersion)} · DSH ${escapeHtml(dshVersion)}</p>
       ${recheckRow}`
    : `<div class="warn"><strong>配对当前不可用（active=false）</strong>
         <p>${escapeHtml(errorText || 'relay 启动自检未通过，请先修复后再扫码。')}</p>
         <p>排查：<code>curl.exe -k https://127.0.0.1:${escapeHtml(pairing?.port ?? '')}/__relay/health</code></p>
         ${recheckRow}
       </div>`

  const configCard = `<div class="card" id="config-card">
      <div class="eyebrow">中继服务器设置（持久化）</div>
      <p class="hint">手动填写公网中继服务器的 IP/域名、端口，以及可选的 API 路由端点。保存后会写入配置文件并立即重连，无需重启桌面 DSH。</p>
      ${config?.error ? `<div class="warn"><strong>配置文件有错误，当前仍在用旧配置</strong><p>${escapeHtml(config.error)}</p></div>` : ''}
      <form id="config-form" autocomplete="off" data-secure="${configGateway?.secure !== false ? 'true' : 'false'}">
        <label class="check"><input type="checkbox" id="cfg-enabled"${configGateway?.enabled ? ' checked' : ''}> 启用公网中继（关闭后仅保留局域网直连）</label>
        <label class="field">服务器 IP / 域名
          <input id="cfg-host" name="host" type="text" value="${escapeHtml(configGateway?.host ?? '')}" placeholder="例如 203.0.113.10" spellcheck="false">
        </label>
        <label class="field">端口
          <input id="cfg-port" name="port" type="number" min="1" max="65535" value="${escapeHtml(configGateway?.port ?? 9443)}">
        </label>
        <label class="field">API 路由端点（可选）
          <input id="cfg-path" name="path" type="text" value="${escapeHtml(configGateway?.path ?? '')}" placeholder="/__gateway/tunnel" spellcheck="false">
        </label>
        <label class="field">证书指纹 SPKI SHA-256（可选）
          <input id="cfg-fingerprint" name="fingerprint" type="text" value="${escapeHtml(configGateway?.fingerprint ?? '')}" placeholder="留空 = 首次连接自动信任（TOFU）" spellcheck="false">
        </label>
        <div class="row"><button type="submit" class="primary" id="cfg-save">保存并重连</button></div>
        <p class="hint" id="config-status">配置文件：<code>${escapeHtml(config?.file ?? 'unknown')}</code> · 来源：${escapeHtml(configSourceLabel)}${configGateway?.enabled ? ` · 当前：${configGateway.online ? '<span class="ok">在线</span>' : '<span class="bad">离线</span>'}` : ' · 未启用'}${config?.updatedAt ? ` · 保存于 ${escapeHtml(config.updatedAt)}` : ''}${config?.initializedBy ? ` · 初始化：${escapeHtml(config.initializedBy)}` : ''}</p>
        <p class="hint">修改方式：本页表单 / <code>pwsh -File scripts/relay-config.ps1</code> / 直接编辑上面的 JSON 文件（保存后自动生效）。</p>
      </form>
    </div>`

  const gatewayCard = gatewayMode
    ? `<div class="card">
        <div class="eyebrow">公网服务器（网关）</div>
        <dl>
          <dt>服务器地址</dt><dd>${escapeHtml(gatewayUrl)}</dd>
          <dt>状态</dt><dd class="${gatewayOnline ? 'ok' : 'bad'}">${gatewayOnline ? '在线（隧道已注册）' : '离线/未连接'}</dd>
          <dt>远程 Token</dt>
          <dd><span id="token-masked">${escapeHtml(maskToken(token))}</span>
              <code id="token-full" hidden>${escapeHtml(token)}</code>
              <button type="button" class="mini" id="reveal-token">显示</button>
              <button type="button" class="mini" id="copy-token" data-token="${escapeHtml(token)}">复制</button></dd>
          <dt>网关证书指纹</dt><dd class="mono">${escapeHtml(gatewayFp || 'unknown')}</dd>
          <dt>桌面名</dt><dd>${escapeHtml(name)}</dd>
        </dl>
        <div class="row">
          <button type="button" class="primary" id="rotate-token">生成新 Token</button>
          <button type="button" class="mini" id="copy-pairing" data-uri="${escapeHtml(uri)}">复制配对信息</button>
        </div>
        <p class="hint" id="rotate-status"></p>
        <p class="hint">轮换后旧 Token 立即失效（网关会断开旧隧道）；手机需要用新二维码/配对信息重新配对。</p>
      </div>`
    : ''

  const lanCard = `<div class="card">
      <div class="eyebrow">局域网直连（备用）</div>
      <dl>
        <dt>桌面名</dt><dd>${escapeHtml(name || 'unknown')}</dd>
        <dt>LAN 地址</dt><dd>${escapeHtml(localHostPort)}</dd>
        <dt>SPKI 指纹</dt><dd class="mono">${escapeHtml(fp || 'unknown')}</dd>
        <dt>pairing 更新时间</dt><dd>${escapeHtml(updatedAt)}</dd>
        <dt>relay / DSH</dt><dd>${escapeHtml(relayVersion)} / ${escapeHtml(dshVersion)}</dd>
        <dt>页面生成时间</dt><dd>${escapeHtml(checkedAt)}</dd>
      </dl>
      ${localUri ? `<details><summary>LAN 配对 URI（v1，含 launchToken，等同密码）</summary>
        <p class="uri">${escapeHtml(localUri)}</p>
        <p><button type="button" class="mini" id="copy-local" data-uri="${escapeHtml(localUri)}">复制 LAN 配对信息</button></p></details>` : ''}
    </div>`

  return `<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="referrer" content="no-referrer">
<title>DSH Remote 配对管理</title>
<style>
  :root { color-scheme: light dark; }
  body { margin: 0; padding: 24px 16px 48px; font: 15px/1.6 -apple-system, "Segoe UI", system-ui, sans-serif; max-width: 720px; margin-inline: auto; }
  h1 { font-size: 22px; margin: 8px 0 4px; }
  .sub { color: #666; margin-top: 0; }
  .card { border: 1px solid #8884; border-radius: 12px; padding: 16px; margin: 16px 0; }
  .ok { color: #137333; font-weight: 600; }
  .bad { color: #d93025; font-weight: 600; }
  .warn { border: 1px solid #d93025; background: #d9302511; border-radius: 12px; padding: 12px 16px; margin: 16px 0; }
  .warn strong { color: #d93025; }
  .eyebrow { font-size: 12px; letter-spacing: .08em; color: #777; text-transform: uppercase; margin-bottom: 6px; }
  #qr { display: flex; justify-content: center; align-items: center; min-height: 280px; background: #fff; border-radius: 8px; padding: 8px; }
  #qr svg { width: min(320px, 78vw); height: auto; }
  dl { display: grid; grid-template-columns: max-content 1fr; gap: 6px 16px; margin: 0; }
  dt { color: #666; }
  dd { margin: 0; word-break: break-all; font-family: ui-monospace, Consolas, monospace; }
  code, .uri { font-family: ui-monospace, Consolas, monospace; font-size: 12.5px; word-break: break-all; }
  details { margin-top: 12px; }
  .hint { color: #666; font-size: 13px; }
  .row { display: flex; gap: 8px; margin-top: 12px; flex-wrap: wrap; }
  .field { display: grid; gap: 4px; margin: 10px 0; font-size: 13px; color: #666; }
  .field input { font: inherit; color: inherit; padding: 8px 10px; border-radius: 8px; border: 1px solid #8886; background: #8881; }
  .check { display: flex; align-items: center; gap: 8px; margin: 8px 0 4px; font-size: 14px; }
  button { font: inherit; cursor: pointer; border-radius: 8px; border: 1px solid #8886; background: transparent; color: inherit; padding: 8px 12px; }
  button.primary { background: #2f6feb; border-color: #2f6feb; color: #fff; font-weight: 600; }
  button.mini { padding: 4px 8px; font-size: 12.5px; margin-left: 6px; }
  button:disabled { opacity: .5; cursor: default; }
</style>
</head>
<body>
<h1>DSH Remote 配对管理</h1>
<p class="sub">此页面只在本机（loopback）可访问。${gatewayMode ? '远程配对走公网网关；局域网直连仍可用。' : '当前为局域网直连模式。'}</p>
${statusBlock}
${configCard}
${active && uri ? `<div class="card">
  <div class="eyebrow">${gatewayMode ? '扫码配对（公网网关 v2）' : '扫码配对（局域网 v1）'}</div>
  <div id="qr" data-pairing-uri="${escapeHtml(uri)}">正在生成二维码…</div>
  <p class="hint">手机用「系统相机 / 扫码」扫描 → 以 <code>dshremote://pair?…</code> 深链拉起 DSH Remote App。</p>
</div>` : ''}
${gatewayCard}
${lanCard}
<div class="card">
  <strong>说明</strong>
  <ul>
    <li>桌面名：<code>${escapeHtml(name || 'unknown')}</code>；远程地址：<code>${escapeHtml(remoteHostPort)}</code>。</li>
    <li>${gatewayMode ? '远程 Token 只用于公网网关；轮换后需重新扫码。' : '当前未配置公网网关（gateway.enabled=false），仅局域网可用。'}</li>
    <li>token 与二维码等同密码：不要截图外传；配对页只在 loopback 提供。</li>
    <li>桌面 DSH 重启后 LAN launchToken 会变，本页刷新即可；远程 Token 持久化在 <code>identity.json</code>，不随重启变化。</li>
  </ul>
</div>
<script src="${PAIR_QR_ASSET_PATH}"></script>
<script src="${PAIR_JS_ASSET_PATH}"></script>
</body>
</html>
`
}

/** Same-origin page script: QR rendering, token reveal/copy, rotate + copy pairing. */
export const PAIR_JS_SOURCE = `(function () {
  var qrEl = document.getElementById('qr');
  if (qrEl) {
    var uri = qrEl.getAttribute('data-pairing-uri');
    if (!uri) { qrEl.textContent = '配对信息尚未就绪，请刷新本页。'; }
    else if (typeof qrcode !== 'function') { qrEl.textContent = 'QR 库加载失败（请刷新本页）'; }
    else {
      try {
        var qr = qrcode(0, 'M');
        qr.addData(uri);
        qr.make();
        qrEl.innerHTML = qr.createSvgTag({ cellSize: 6, margin: 4, scalable: true });
      } catch (error) { qrEl.textContent = '二维码生成失败：' + (error && error.message ? error.message : String(error)); }
    }
  }

  function copy(text, button, okText) {
    var done = function () { if (button) { var old = button.textContent; button.textContent = okText || '已复制'; setTimeout(function () { button.textContent = old; }, 1500); } };
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).then(done, function () { fallbackCopy(text); done(); });
    } else { fallbackCopy(text); done(); }
  }
  function fallbackCopy(text) {
    var area = document.createElement('textarea');
    area.value = text; area.setAttribute('readonly', ''); area.style.position = 'fixed'; area.style.opacity = '0';
    document.body.appendChild(area); area.select();
    try { document.execCommand('copy'); } catch (e) {}
    document.body.removeChild(area);
  }

  function bindCopy(id) {
    var el = document.getElementById(id);
    if (!el) return;
    el.addEventListener('click', function () {
      var text = el.getAttribute('data-uri') || el.getAttribute('data-token') || '';
      copy(text, el, '已复制');
    });
  }
  bindCopy('copy-pairing');
  bindCopy('copy-token');
  bindCopy('copy-local');

  var reveal = document.getElementById('reveal-token');
  if (reveal) {
    reveal.addEventListener('click', function () {
      var full = document.getElementById('token-full');
      var masked = document.getElementById('token-masked');
      if (!full) return;
      var hidden = full.hasAttribute('hidden');
      if (hidden) { full.removeAttribute('hidden'); if (masked) masked.setAttribute('hidden', ''); reveal.textContent = '隐藏'; }
      else { full.setAttribute('hidden', ''); if (masked) masked.removeAttribute('hidden'); reveal.textContent = '显示'; }
    });
  }

  var configForm = document.getElementById('config-form');
  if (configForm) {
    var cfgEnabled = document.getElementById('cfg-enabled');
    var cfgHost = document.getElementById('cfg-host');
    var cfgPort = document.getElementById('cfg-port');
    var cfgPath = document.getElementById('cfg-path');
    var cfgFingerprint = document.getElementById('cfg-fingerprint');
    var cfgSave = document.getElementById('cfg-save');
    var cfgStatus = document.getElementById('config-status');
    var cfgSecure = configForm.getAttribute('data-secure') !== 'false';
    var syncCfgDisabled = function () {
      var off = cfgEnabled && !cfgEnabled.checked;
      [cfgHost, cfgPort, cfgPath, cfgFingerprint].forEach(function (el) { if (el) el.disabled = off; });
    };
    if (cfgEnabled) cfgEnabled.addEventListener('change', syncCfgDisabled);
    syncCfgDisabled();
    configForm.addEventListener('submit', function (event) {
      event.preventDefault();
      if (cfgSave) cfgSave.disabled = true;
      if (cfgStatus) cfgStatus.textContent = '正在保存并重连 …';
      var payload = {
        gateway: {
          enabled: cfgEnabled ? !!cfgEnabled.checked : true,
          host: cfgHost ? cfgHost.value.trim() : '',
          port: cfgPort && cfgPort.value !== '' ? Number(cfgPort.value) : null,
          path: cfgPath ? cfgPath.value.trim() : '',
          fingerprint: cfgFingerprint ? cfgFingerprint.value.trim() : '',
          secure: cfgSecure
        }
      };
      fetch('${CONFIG_PATH}', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(payload)
      })
        .then(function (res) { return res.json().then(function (body) { return { ok: res.ok, body: body }; }); })
        .then(function (result) {
          if (!result.ok || !result.body || result.body.ok !== true) {
            throw new Error((result.body && result.body.error) || ('HTTP ' + (result.body ? '' : '?')));
          }
          var state = result.body;
          if (cfgStatus) {
            cfgStatus.textContent = state.gateway && state.gateway.enabled
              ? ('已保存：' + state.gateway.url + '，正在重连并刷新 …')
              : '已保存：公网中继已关闭，仅局域网可用；正在刷新 …';
          }
          setTimeout(function () { location.reload(); }, 1200);
        })
        .catch(function (error) {
          if (cfgSave) cfgSave.disabled = false;
          if (cfgStatus) cfgStatus.textContent = '保存失败：' + (error && error.message ? error.message : String(error));
        });
    });
  }

  var recheck = document.getElementById('recheck');
  if (recheck) {
    recheck.addEventListener('click', function () {
      var status = document.getElementById('recheck-status');
      recheck.disabled = true;
      if (status) status.textContent = '正在重跑协议自检 …';
      fetch('${SELF_CHECK_PATH}', { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{}' })
        .then(function (res) {
          return res.json().then(function (body) { return { ok: res.ok, status: res.status, body: body }; });
        })
        .then(function (result) {
          var body = result.body || {};
          if (!result.ok) throw new Error(body.error || ('HTTP ' + result.status));
          if (body.ok === true) {
            if (status) status.textContent = '自检通过（尝试 ' + (body.attempts || 1) + ' 次），刷新中 …';
            setTimeout(function () { location.reload(); }, 900);
            return;
          }
          recheck.disabled = false;
          if (status) status.textContent = '自检仍失败：' + (((body.missing || []).join(', ')) || (body.errors || []).join('; ') || 'see /__relay/health');
        })
        .catch(function (error) {
          recheck.disabled = false;
          if (status) status.textContent = '自检请求失败：' + (error && error.message ? error.message : String(error));
        });
    });
  }

  var rotate = document.getElementById('rotate-token');
  if (rotate) {    rotate.addEventListener('click', function () {
      var status = document.getElementById('rotate-status');
      if (!window.confirm('生成新 Token 会立即使旧 Token 失效，手机需要用新二维码重新配对。继续？')) return;
      rotate.disabled = true;
      if (status) status.textContent = '正在轮换 Token …';
      fetch('${PAIR_ROTATE_PATH}', { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{}' })
        .then(function (res) { return res.json().then(function (body) { return { ok: res.ok, body: body }; }); })
        .then(function (result) {
          if (!result.ok || !result.body || result.body.ok !== true) {
            throw new Error((result.body && result.body.error) || ('HTTP ' + (result.body ? '' : '?')));
          }
          if (status) status.textContent = 'Token 已轮换，刷新页面以显示新二维码 …';
          setTimeout(function () { location.reload(); }, 700);
        })
        .catch(function (error) {
          rotate.disabled = false;
          if (status) status.textContent = '轮换失败：' + (error && error.message ? error.message : String(error));
        });
    });
  }
})();
`

/** Headers every pairing route sends. */
export function pairingPageHeaders (contentType) {
  return {
    'content-type': contentType,
    'cache-control': 'no-store',
    'referrer-policy': 'no-referrer',
    'x-content-type-options': 'nosniff',
    'x-frame-options': 'DENY',
    'content-security-policy': [
      "default-src 'none'",
      "script-src 'self'",
      "style-src 'unsafe-inline'",
      "img-src 'self' data:",
      "connect-src 'self'",
      "base-uri 'none'",
      "form-action 'none'",
      "frame-ancestors 'none'"
    ].join('; ')
  }
}
