# DSH 插件页 / 插件市场可见性与配置 UI 集成调研

> 任务：`task-15`（只读调研；未修改 relay/app/scripts 产品代码）
> 目标：让 `dsh-remote-relay` 成为 DSH「插件页/插件市场」中可见、可配置、可安装的插件
> 证据来源：安装目录 `D:\Program Files\DeepSeek Harness\resources\app.asar`（0.2.2-rc.1）、运行中的 desktop Host 的 `plugin_manager` 探针、本机已装第三方插件样本（`dsh-opencode-models` 0.5.3、`dshmarket` 1.66.32）、DSH 自带插件开发 skill 文档
> 约定：`[已验证]` = 有代码/运行时证据；`[推测]` = 推断

---

## 0. 结论速览（对应任务的 5 个问题）

| # | 问题 | 结论 |
|---|---|---|
| 1 | 插件页数据源/过滤 | **侧栏「插件」页只列 bundle**（`pluginManager.listBundles` + `listPlugins` 取行状态）。relay 是 patch 插入的行、不是 bundle ⇒ **在插件页永远不出现**。它只出现在 **设置 →「内置插件」页**（数据源 `pluginInventory/list`，全部 Loader 条目），且位于 **「全局插件」分组（默认折叠）**。`[已验证]` |
| 2 | `Config` 是否自动渲染配置表单 | **不会**。`Config`（schemastery）只在激活时校验该行 `config`；DSH 没有发布任何消费 `settings` 自动表单（`autoGenerate`）的客户端。插件页的配置面板是**客户端 UI 贡献**（`plugins.bundle.config` / `plugins.row.config` / `plugins.item` 三个 slot），不是 schema 自动生成。`[已验证]` |
| 3 | 能否加自定义设置页 | **能，官方支持**：`dsh.bundle` + `dsh.client` + 手写 lazy-CJS `client.js` + `ctx.slots.register`。自带模板 `templates/decoration/`；本机已装的第三方 `dsh-opencode-models` 就是「设置页新增一个页面」的完整实证（11 KB 手写、无构建）。工作量约 **0.5–1 天**。`[已验证]` |
| 4 | 可安装形式 | **必须是 bundle**（`package.json` 声明 `dsh.bundle.patch`）：GUI「添加插件」/ `plugin_manager install_bundle` 只收 bundle，非 bundle 包会回滚（`not-bundle`）。支持 registry / git / **本地绝对目录** / **.tgz**。Desktop profile **不允许 CLI**（报错原文见 §4.3），GUI 内安装是唯一受支持入口。`[已验证]` |
| 5 | 缺口 | ① 非 bundle 行无法进入插件页；② `Config` schema → 表单无通用渲染器（宿主侧 `settings/describe` 已就绪、客户端未实现）；③ 纯 patch 无法造出插件页条目/配置入口。上游扩展点建议见 §6。`[已验证]` |

**最小可行路径（推荐，按代价从低到高）**

- **T0（零代码，立刻可做）**：给 relay 补 `locale/en.json`、`locale/zh.json` 的 `meta.title`/`meta.description` + `./icon` 导出 + `package.json` 的 `exports`。→ 用户在 **设置 → 内置插件 → 全局插件**（需展开）看到带标题/描述/图标的 relay。`[已验证机制]`
- **T1（推荐主路径，1–2 小时）**：把 relay 打成 **bundle**（`cordis.patch.yml` + `dsh.bundle.patch` + locale/icon/README），用 `plugin_manager install_bundle`（绝对目录）安装。→ 侧栏「插件」出现 relay 卡片：标题、描述、README 详情、组件行、启停开关、来源信息；配置仍可写在 `cordis.patch.yml`（README 说明）。**不需要写任何客户端代码。**
- **T2（可选，0.5–1 天）**：加 `dsh.client` 客户端半区，注册 `plugins.bundle.config`（插件页内配置面板）或 `settings.section`（设置页新导航页），表单里展示服务器地址/桌面名/Token/二维码并调用 relay 自己的 `/__relay/*` 端点或 `configEditor.edit()`。→ 真正「在 DSH 里可配置」。
- **T3（保底，无 DSH UI 依赖）**：relay 自带的 `https://<host>:8443/__relay/pair` 配对页（QR、Token、健康状态）+ `/__relay/download`，本轮已是可用方案，只是入口不在 DSH 界面里。

---

## 1. 为什么 lead 看到 `listPlugins` 有条目、但用户「看不到」

`plugin_manager list_plugins` 运行时实测（offset 198 段）确实有：

```json
{"entryId":"include:remote-relay","moduleName":"dsh-remote-relay","enabled":true,
 "fiberPhase":"active","patchId":"remote-relay"}
```

它来自 desktop profile 的 patch 插入行（`profiles\desktop\cordis.patch.yml` 的 `- insert: - id: remote-relay name: dsh-remote-relay`），不是 bundle 依赖。三点决定可见性：

1. **侧栏「插件」页的卡片只来自 `listBundles()`**：客户端 `dsh-client-ui-plugin-manager/lib/client.js` 同时取 `listBundles()` 与 `listPlugins()`，但 `listPlugins()` 只用于给 bundle 的行补 enable/fiber 状态；页面分组固定为「已安装插件 / 内置插件配置 / 实验性插件」，全部以 **bundle** 为条目。`patchId` 存在只说明该行能启停（`setPluginEnabled`），不代表会出现在列表。`[已验证]`
2. **设置 →「内置插件」页会列它**：该页数据源是 `pluginInventory/list`，返回 `readPluginInventory()` 的全部 Loader 条目（`dsh-host-plugin-inventory/lib/index.js` L121-166），包含 relay。但界面把条目分成「会话插件（按 preset，默认展开）」与 **「全局插件」（`globalOpen ?? false`，默认折叠）**，relay 属于后者（`dsh-client-ui-settings-plugin-inventory/lib/client.js` L283-402、L652-657）。`[已验证]`
3. **没有 meta 时标题退化成短模块名**：`pluginText()` 用 `meta.title`，缺失时 `moduleShortName(moduleName)` 把 `dsh-remote-relay` 变成 `remote-relay`——即使出现在折叠分组里也不显眼。补 `package.json.description` 后只解决描述；**标题要写 locale 文件**（见 §2.3）。`[已验证]`

> 结论：不是「没注册成功」，而是「看的页面不对 + 分组默认折叠 + 标题降级」。要让用户自然看到，只能把 relay 变成 **bundle**（进插件页）。

---

## 2. Q1 详述：两个插件相关页面的数据源与过滤规则

### 2.1 侧栏「插件」页（`@deepseek-ai/dsh-client-ui-plugin-manager`）

- 客户端 bundle：`dsh/node_modules/@deepseek-ai/dsh-client-ui-plugin-manager/lib/client.js`（438 KB）；宿主服务：`@deepseek-ai/dsh-plugin-manager`。
- RPC：`pluginManager/listBundles`、`listPlugins`、`inspect`、`installBundle`、`waitForInstall`、`cancelInstall`、`removeBundle`、`setBundleEnabled`、`setPluginEnabled`、`updateBundle`、`listVersions`、`registries`、`readBundleDetail`、`listVersionExemptions`、`setVersionExemption`；另有 `pluginRegistryProbe/fastest`（`client-ui-plugin-manager` 自己的 host 半区）。`[已验证：scripts/remote-contract.json]`
- **卡片条目 = bundle**；`listBundles()` 的条目来自（`dsh-plugin-manager/lib/index.js` L2130+ `readBundles()`）：
  1. `dsh.profile.bundles` 选中的 bundle；
  2. profile `package.json` 的 dependencies（含只装未选的，`not-bundle` 错误）；
  3. DSH 安装自带的 bundle；
  4. 官方按需目录（`OFFICIAL_ON_DEMAND_CATALOG`，未装显示 `availability:"missing"` + `installTarget`）。
- 过滤/标记规则（README.zh.md L14/L32/L34/L38/L87 与客户端常量）：
  - 「已安装插件」= 可用/已装 bundle；「实验性插件（更多）」仅对 `EXPERIMENTAL_BUNDLES` 名单内的包显示「实验性」标签（**包名前缀不决定**）；
  - `listedByDefault:false` 的随包 bundle 默认从两个列表隐藏，启用/安装后才显示；
  - `readOnlyReason:"management-required"`（携带插件管理器自身的包，如 `dsh-base`、`dsh-web-app`、`sdk-app`）→ 行锁定、不可卸载；行级 `readOnlyReason:"unaddressable"` = patch 定位不到该行；
  - 列表超过 10 行出现筛选输入框（按本地化标题/描述/行 id/模块名过滤）；
  - 每个「配置页」控件只在**有客户端注册**时出现（§3）。
- 标题/描述/图标解析（`readPluginMeta`，`dsh-app-boot/lib/index.js` @125125 附近）：
  `locale/<lang>.json` 的 `meta.title`/`meta.description` → 包根 `package.json` 的 `name`/`description` → 完整模块名；图标取 `package.json` 顶层 `icon`（优先）或导出的 `./icon`（SVG/PNG/JPEG/WebP ≤256 KiB，必须在包目录内）；子路径插件不读 `package.json`。`[已验证]`
- bundle 详情页：locale `meta.detail` 指定的 Markdown，缺省回退包内 **README**（`pluginManager.readBundleDetail`）。→ 写好 README 即可在插件页看到「介绍文档」。

### 2.2 设置 →「内置插件」页（`@deepseek-ai/dsh-client-ui-settings-plugins` + `-plugin-inventory`）

- 外层只是个壳：`settings.section`（id `plugins`，order 15，标签「内置插件」）+ `settings.plugins.tab` 列表 slot（`dsh-client-ui-settings-plugins/lib/client.js` L198-266）。
- 唯一默认 tab 由 `dsh-client-ui-settings-plugin-inventory` 提供，数据源 `pluginInventory/list`：
  `{ entries:[{entryId, moduleName, enabled, fiberPhase, meta?, diagnostic?}], agentPresets?:[{id,rows:[...]}], managementAvailable?:true }`（`dsh-host-plugin-inventory/lib/index.js` L112-166）。**所有 Loader 非 group 条目**都在里面，包括 patch 插入的 relay。`[已验证]`
- 分组：会话插件（每个 preset，默认展开） / 全局插件（默认折叠）；失败条目前置；只读（无开关，只有状态标签：启用/停用/失败/条件）。`[已验证]`

### 2.3 让 relay「看起来是个正常插件」的最小元数据改动（T0）

```jsonc
// relay/package.json  （新增字段，不改运行逻辑）
{
  "exports": {
    ".": "./src/index.mjs",
    "./locale/*.json": "./locale/*.json",
    "./icon": "./icon.svg",
    "./package.json": "./package.json"
  },
  "files": ["package.json","README.zh.md","src/**/*.mjs","assets/**/*","locale/*.json","icon.svg","cordis.patch.yml"]
}
```
```jsonc
// relay/locale/en.json
{ "meta": { "title": "DSH Remote Relay", "description": "Expose the loopback DSH Remote service to your phone over TLS, with pairing and protocol self-checks." } }
// relay/locale/zh.json
{ "meta": { "title": "DSH 手机远程网关", "description": "通过 TLS 把本机回环的 DSH Remote 服务开放给手机，内置配对二维码与协议自检。" } }
```
- 注意：`exports` 一旦出现会**取代** `main` 解析，`.` 必须指向宿主入口（relay 当前 `main: src/index.mjs` ⇒ 用 `".": "./src/index.mjs"`）。
- 图标：`icon.svg`（原创或许可证允许，≤256 KiB）。
- 这一步完成后：设置→内置插件→全局插件里显示标题/描述/图标（仍需展开分组）；插件页**依旧不出现**。

---

## 3. Q2 详述：`Config` 与配置 UI

### 3.1 事实

- `Config`（Cordis/schemastery，或 zod）**只在激活时校验该行的 `config`**，并提供默认值与 `.volatile()` 语义；没有任何代码把它渲染成表单。`[已验证]`
  - skill 文档 `cordis-plugin-development/references/host-plugin.md`：「A plugin that declares `Config` validates the row's `config` at activation; query `Config.listConfigs` for an installed plugin's schema before writing its `config`」。
- 宿主侧**确实**有 schema 派生的 settings 机制：`@deepseek-ai/dsh-settings`（`settings/describe`/`mutate`/`update`/`replace`、`credentials/*`，返回 `{ns, autoGenerate, schema, value, secrets, revision}`），但 README 原文：「每个表单报告 `autoGenerate`（默认开启），供按 schema 生成页面的客户端使用；**目前没有已发布的客户端这样做**」。而且它只覆盖条目 Config 中显式 `.volatile()` 的字段。`[已验证]`
- 插件页的配置区是**三个客户端 slot**（`dsh-client-ui-plugin-manager/lib/client.js` L73-135、L4046-4074；README.zh.md L87）：
  - `plugins.item`（list）：官方插件配置卡片（`label` 排序）；
  - `plugins.bundle.config`（keyed，键 = bundle 包名）：bundle 详情页的配置表单；
  - `plugins.row.config`（keyed，键 = `<包名>#<row id>`）：某一行多一个「配置」控件，打开该行页面。
  - 页面用 `view:'page'`（带保存控件）与 `view:'summary'`（卡片摘要）两种视图渲染；表单保存经宿主 `configEditor` 写回 profile patch（`ctx.configEditor.edit(entry, current => ...)`）。`[已验证]`
- 配置持久化规则（官方文档）：**不要在插件里读/改 profile 的 `cordis.patch.yml`**；插件运行时改自己的配置要用 `ctx.get('configEditor').edit(ctx.fiber.entry, current => ({...current, field}))`；普通字段修改会**重启插件**，`.volatile()` 字段原地生效（用 `config.field.get()` 读）；**密钥类（token）应存 `ctx.credentials.modifyRecord()`，不要写配置**。`[已验证]`
- 内容安全：keyed slot 的 key 由页面统一分发；`plugins.row.config` 页只在有条目点名该行时存在。`[已验证]`

### 3.2 对 relay 的含义

- 仅加 `export const Config = ...` ⇒ **没有 UI**，只得到：激活时校验、默认值、`cordis_inspect_query Config.listConfigs` 可查、用户手写 patch 更安全。
- relay 目前的配置来源就是 profile patch 的 `config:` 块（`cfg.host/port/publicHost/publicPort/token/name/downloadDir/pairingFile`，见 `relay/src/index.mjs`）。**T1 阶段保留这条路即可**（README 写清字段表）。
- 若要 UI（T2），推荐顺序：
  - **B1（最省）**：`plugins.bundle.config`，key = `dsh-remote-relay`，在插件页 bundle 详情里出一个表单；保存走 `ctx.remote.settings.mutate('relay' 命名空间…)`（需要把字段声明为 `.volatile()` 并注册 settings 命名空间）或直接 POST 到 relay 自有端点由 host 半区 `configEditor.edit()`。
  - **B2**：`settings.section` 新增一个设置页（opencode 模板，见 §4），适合放二维码/配对信息（不需要保存配置也能只读展示）。

### 3.3 relay 建议的最小 `Config`（schemastery）

```js
import z from '@deepseek-ai/schemastery' // 由 profile 运行时解析（profiles/node_modules/@deepseek-ai/* 已有 junction 表）

export const Config = z.object({
  host: z.string().description('监听绑定地址；留空自动探测 LAN IPv4'),
  port: z.natural().min(1).max(65535).default(8443).description('TLS 监听端口'),
  publicHost: z.string().description('配对 URI 里公告的地址（VPS/隧道入口）；留空=host'),
  publicPort: z.natural().min(1).max(65535).description('公告端口；留空=port'),
  name: z.string().description('桌面名称，手机端显示'),
  downloadDir: z.string().description('APK 下载目录（可选）'),
  pairingFile: z.string().description('pairing.json 输出路径（可选，默认 ~/.dsh/remote/pairing.json）'),
  tls: z.object({ certFile: z.string(), keyFile: z.string() }).description('TLS 证书；留空用包内 certs/'),
})
```
> **token 不要放进 Config**（官方规则：密钥入 `ctx.credentials.modifyRecord()`）。relay 现状把 token 写 patch 属临时做法；bundle 化时应改为自动生成并持久化到 pairing 状态文件，或存 credentials 记录。`[建议，非已验证]`
> `host/port/publicHost/publicPort` 这类字段若做成 **`.volatile()`** 才能热改；否则保存后插件会重启（对 relay 可接受：重启=重开监听）。

---

## 4. Q3 详述：自定义设置页（客户端 UI 插件）

### 4.1 官方支持，且有现成模板 + 实证

- 官方 skill 文档：`dsh-agent-preset/skills/cordis-plugin-development/`
  - `references/ui-plugin.md`：`package.json` 加 `dsh.client`（`platform`/`immediately`/`inject`）+ `./client` 导出；`client.js` 用 `window.__ModuleLoader__.load({id, factory(require){...}})`；React 由浏览器模块表提供，**无需构建、无需安装 React**；`ctx.slots.inject(...) → ctx.slots.register(...)`。
  - `templates/decoration/`：最小 UI bundle 模板（package.json / cordis.patch.yml / index.js / client.js / locale / icon.svg）。
  - `references/practices.md`（UI 节）：**「Render plugin pages as React components in a slot. Do not serve an HTML page from the Host and embed it in an iframe」**；主题只允许 `--dsw-alias-*` token；禁止 import `@deepseek-ai/dsh-client-*` 包；文案走 Client locale。
- 实证：本机安装的第三方 `dsh-opencode-models`（github:Retr67/dsh-opencode-models）结构完全一致：
  ```jsonc
  { "exports": { ".": "./dsh/index.js", "./client": "./dsh/client.js", "./package.json": "./package.json" },
    "dsh": { "bundle": { "patch": "./cordis.patch.yml" },
             "client": { "inject": [], "platform": "web" } } }
  ```
  `cordis.patch.yml`：`- insert: - id: opencode-models / name: 'dsh-opencode-models'`；
  `dsh/client.js`（11 KB，手写 lazy-CJS）注册 `settings.section` 页面，驱动宿主 `/opencode-models/refresh|status` 端点。
  文件头还记录了三条踩坑约束：`settings.section` 的组件必须是 **React 函数组件**；`register` 必须带 `name` 字段与 slot 名一致；模块要 `export inject = ['slots']`。`[已验证]`

### 4.2 relay 的最小页面方案（两条）

| 方案 | slot / key | 内容 | 代价 |
|---|---|---|---|
| **插件页内配置**（推荐） | `plugins.bundle.config`，key `dsh-remote-relay` | 表单：绑定地址/端口/公告地址/桌面名；二维码 + "打开配对页"；保存按钮 | client.js ~200–400 行；host 半区加一个保存端点（或 `configEditor.edit`） |
| **设置页新导航页** | `settings.section`（id `remote-relay`，order 例如 20） | 只读面板：relay 状态、TLS 指纹、配对 URI/QR、"复制 Token"、打开 `/__relay/pair` | client.js ~150 行；host 半区已有 `/__relay/health`，只需补一个 `/__relay/info` JSON |

- 两种都不需要构建工具：手写 `client.js` + `exports`/`dsh.client` 即可（与 opencode 同款）。
- 若不想写客户端插件：**保持 T1/T3**——README + patch 配置 + relay 自有 `/__relay/pair` 页（已实现，含 QR、token、下载页）。
- 注意 `references/practices.md` 明确不建议 iframe 内嵌宿主 HTML 页；relay 的配对页应作为**独立页面**（新标签打开），而不是 iframe 塞进设置页。

---

## 5. Q4 详述：可安装/可分发形式

### 5.1 bundle vs plugin

| | 普通插件（relay 现状） | **bundle**（推荐） |
|---|---|---|
| 判定 | 无 `dsh.bundle`；`dsh.plugin:true` 只是标记 | `package.json.dsh.bundle.patch` 指向 YAML |
| 安装 | 只能手贴 junction + 手写 profile patch | `plugin_manager install_bundle` / GUI 添加插件 |
| 选中 | 无 | 写入 `dsh.profile.bundles`（`readProfilePlugins().enabled = bundles.includes(name)`） |
| 插件页 | 不出现（仅内置插件页） | 出现卡片/详情/行/启停/来源信息 |
| 升级 | 手改 | `updateBundle`（registry）/ 卸载重装（其它来源） |

`installBundle` 的硬约束（`dsh-plugin-manager/lib/index.js` L115143 附近 JSDoc）：
> "A run that fails, is cancelled, or **adds a package without a bundle patch** restores `package.json` and `pnpm-lock.yaml`"

⇒ **非 bundle 包从 GUI 安装会回滚**（错误码 `not-bundle`；`listBundles` 也把它显示为问题行）。`[已验证]`

### 5.2 relay 的 bundle 化改造（T1，最小）

```
relay/
  package.json          # 加 dsh.bundle.patch、exports(.、./locale/*.json、./icon、./package.json)、files
  cordis.patch.yml      # 新增：
                        #   - insert:
                        #       - id: remote-relay
                        #         name: 'dsh-remote-relay'
                        #         config: { port: 8443, ... }   # 默认配置放这里
  locale/en.json        # meta.title / meta.description
  locale/zh.json
  icon.svg
  src/index.mjs         # 现有宿主入口（可选加 export const Config）
  README.zh.md          # 插件页 bundle 详情文档（readBundleDetail 回退 README）
```
- **迁移注意**：现在 `profiles\desktop\cordis.patch.yml` 里有 `- insert: - id: remote-relay`。bundle patch 再插同名 id 会重复 → **安装 bundle 前应删掉 profile 里那条手写 insert**（或用不同 row id）。用户级配置仍可用 profile patch 覆盖：`- id: remote-relay  config: {...}`（patch 覆盖会**整体替换** config，需重述全部字段）。
- 不要手写 profile 的 `package.json` / `cordis.patch.yml`：官方 skill 明确要求用 `plugin_manager install_bundle`（宿主会做 pnpm 安装 + `dsh.profile.bundles` 选择 + HMR 重组）。`[已验证]`

### 5.3 安装形态（都支持）

`plugin-manager/lib/types/install-spec.js` `parseInstallSpec()`：`[已验证]`

| 形态 | 示例 | 说明 |
|---|---|---|
| registry | `dsh-remote-relay@0.2.0` | 需发 npm；插件页可检查更新 |
| git | `github:user/repo`、`git+https://…` | opencode 即此形态 |
| **本地目录（绝对路径）** | `D:\Code\VibeCoding\DSHRemote\relay` | 必须绝对路径；pnpm 记录为 link ⇒ **开发迭代友好，`files` 不过滤**；目录不能删 |
| **tarball** | `D:\…\dsh-remote-relay-0.2.0.tgz` | 打印/分发用；内容受 `files` 过滤；URL 亦支持 |
| 相对路径 | `./relay` | **拒绝**（"a local path must be absolute"） |

### 5.4 Desktop profile 的限制（关键）

- CLI：`dsh/lib/bin.js` `rejectElectronProfile()`：`error: profile "desktop" is managed exclusively by the Electron application` —— `dsh --profile desktop` 与 `dsh plugin --profile desktop add …` 都被拒；只有 Electron 自己的 carrier 以 `manageDesktopProfile=true` 调用才放行。`[已验证]`
- Desktop 主进程还自己管理该 profile：`lib/main.js` 的 `DesktopProjectManager`（`disableAllPlugins()`、`applyRelease()`、`createPluginProfile()`，profile 目录 `lock` 文件串行化）。所以**别在外部脚本里并发改 desktop profile**。`[已验证]`
- **可行入口**：在运行中的 DSH 内通过 GUI「插件 → 添加插件」，或当前会话可用的 `plugin_manager` 工具（`action: install_bundle`, `target: <绝对目录或 .tgz>`）。宿主内 `pluginManager` 自己写 profile 并触发 HMR；`application: applied` 才算生效，失败会回滚 manifest/lockfile。`[已验证]`

### 5.5 `scripts/package-relay-plugin.ps1` 建议（无需发布 npm）

产物（两种，建议都出）：

```
dist/dsh-remote-relay-<ver>/        # 可直接被 install_bundle 指向的目录（含 cordis.patch.yml/locale/icon/src）
dist/dsh-remote-relay-<ver>.tgz     # npm pack 等价物，分发给别人用
```
步骤：

1. 读 `relay/package.json` 的 `name`/`version`；
2. 校验必需文件存在：`package.json`、`cordis.patch.yml`、`src/index.mjs`、`locale/en.json`、`locale/zh.json`、`icon.svg`、`README.zh.md`；
3. 复制到 staging 目录（按 `files` 白名单，**排除** `certs/`（含私钥）、`.gitignore`、`assets/*.apk` 之类大文件，除非刻意分发）；
4. 用 `tar -czf` 或 `npm pack` 生成 tgz，并打印 tar 内文件清单（校验 `files` 是否漏项）；
5. 打印安装指引：
   - 在 DSH GUI：插件 → 添加插件 → 填 staging 目录绝对路径（或 tgz 路径）；
   - 或会话内：`plugin_manager {action:'install_bundle', target:'<abs staging dir>'}`
   - 首次安装后按提示重启（"replacing an installed package requires restart to load a fresh JavaScript module generation"）。

> 安全注意：relay 的 `certs/relay-key.pem` 是 TLS 私钥，**绝不能进分发 tarball**（当前 `files` 只含 `assets/**` 与 `src/**`，不含 `certs/`；打包脚本应显式排除并断言）。

---

## 6. Q5：缺口与上游扩展点

| 缺口 | 现状 | 影响 | 建议 |
|---|---|---|---|
| 非 bundle 行无法进入侧栏插件页 | 插件页条目 = `listBundles()` | 纯 patch/junction 用户没有「插件」入口 | bundle 化（本任务推荐）；上游可考虑「插件页列出不可管理行（只读区）」 |
| `Config` 无通用表单 | 宿主 `settings/describe` + `autoGenerate` 已就绪；**无客户端消费** | 只能手改 patch，或自带 UI | 上游补齐「按 settings descriptor 渲染通用表单」（已有 schema/secrets/revision/mutate 全套 RPC，工作量在客户端）；本任务用 `plugins.bundle.config` 客户端半区替代 |
| 无「设置页贡献」以外的轻量入口 | 需 `dsh.client` + client.js | 纯文档/链接型入口也要写客户端代码 | 可复用 `settings.section` 模板（opencode） |
| Desktop 禁止 CLI | `rejectElectronProfile` | 不能用 `dsh plugin add` 脚本化安装 | 用 `install_bundle`；或在 `dsh --profile web` 等其他 profile 上用 CLI |
| 插件页配置面板保存路径 | 需 `configEditor.edit()` 或自有端点 | 直接写 profile patch 是反模式 | 用 configEditor；密钥入 credentials |

---

## 7. 附录：证据与探针

**运行时探针（本机，只读）**

```powershell
# 行级信息（含 patchId / readOnlyReason / fiberPhase）
plugin_manager { action: "list_plugins", offset: 198 }   # 可见 include:remote-relay / patchId remote-relay / active
# bundle 级信息（插件页数据源）—— 26 个 bundle，dshmarket / dsh-opencode-models 为 profile 安装的可卸载 bundle
plugin_manager { action: "list_bundles" }
```

**asar 内关键路径**

| 路径 | 要点 |
|---|---|
| `dsh/node_modules/@deepseek-ai/dsh-client-ui-plugin-manager/lib/client.js` | 插件页：`listBundles`+`listPlugins`（L3650 起）、三配置 slot（L73-135、L4046-4074）、详情/行页渲染、locale 文案（L137+、L455+） |
| `dsh/node_modules/@deepseek-ai/dsh-client-ui-plugin-manager/README.zh.md` | 显示规则、配置页、安装/卸载、实验性标记、来源信息（L14/L32/L34/L38/L65/L85/L87/L103） |
| `dsh/node_modules/@deepseek-ai/dsh-plugin-manager/lib/index.js` | `listPlugins` L2098-2117、`listBundles/readBundles` L2123+、`installBundle`（bundle patch 硬约束）、`setPluginEnabled` L2665+ |
| `dsh/node_modules/@deepseek-ai/dsh-plugin-manager/lib/types/install-spec.js` | 安装 spec 形态（绝对路径/tarball/git/registry，相对路径拒绝） |
| `dsh/node_modules/@deepseek-ai/dsh-host-plugin-inventory/lib/index.js` | `readPluginInventory`：`{entries, meta, agentPresets, managementAvailable}` |
| `dsh/node_modules/@deepseek-ai/dsh-app-boot/lib/index.js` | `readPluginMeta`（locale meta.title/description → package.json；icon 规则） |
| `dsh/node_modules/@deepseek-ai/dsh-client-ui-settings-plugins/lib/client.js` | `settings.section` id `plugins` + `settings.plugins.tab` |
| `dsh/node_modules/@deepseek-ai/dsh-client-ui-settings-plugin-inventory/lib/client.js` | 内置插件页分组/默认折叠/状态标签（L283-402、L652-657） |
| `dsh/node_modules/@deepseek-ai/dsh-settings/README.zh.md` | `Config` 派生表单 / `.volatile()` / `autoGenerate` 无客户端 |
| `dsh/node_modules/@deepseek-ai/dsh-config-editor/README.zh.md` | `ctx.configEditor.edit()` 保存语义 |
| `dsh/node_modules/@deepseek-ai/dsh/lib/bin.js` | `rejectElectronProfile`：`profile "desktop" is managed exclusively by the Electron application` |
| `lib/main.js` | `DesktopProjectManager`（disableAllPlugins/applyRelease/createPluginProfile + profile `lock`） |
| `dsh/node_modules/@deepseek-ai/dsh-agent-preset/skills/cordis-plugin-development/` | `SKILL.md`、`references/{host-plugin,ui-plugin,practices,user-actions,verification}.md`、`templates/{decoration,mcp}/` |
| 本机 `profiles\desktop\node_modules\dsh-opencode-models`（第三方实证） | `dsh.bundle` + `dsh.client` + 手写 `dsh/client.js` 注册 `settings.section` |
| 本机 `relay/` | 现状：`dsh.plugin:true`、无 bundle、无 client、配置来自 patch（host/port/publicHost/publicPort/token/name/downloadDir/pairingFile/tls） |

**结论一句话**：relay 想「看起来/用起来像插件」，就必须从「patch 插入的裸行」升级为 **可安装 bundle**（这一步同时解决可见性、可安装性、详情文档）；配置界面的最小增量是 **`dsh.client` 手写客户端半区**（官方模板同款，无构建），而 `Config` schema 本身只提供校验与默认值，不提供 UI。
