# Windows 工作区 ACL 体验优化方案（2026-11-11）

> 范围：设计，不含代码实现。目标是把"工作区随机写不进去、agent 反复申请审批跑 icacls"变成"启动即体检、一键修复、不再复发"。
> 关联：[permission-audit.md](permission-audit.md)（类型 C 的根因在此展开）。

---

## 0. 结论先行

工作区 ACL 的体验问题**不是"缺 Low 标签"这么简单**，而是三个事实叠加：

1. **DSH 打 Low 标签需要 `WRITE_OWNER`**，而 Windows 只给 owner 隐式 `READ_CONTROL + WRITE_DAC`（`sandbox-windows-acl/src/acl.ts:368-370` 写明："The directory must be owned by the caller AND grant WRITE_OWNER — a Full-control workspace satisfies both"）。
2. **当前 workspace 根只有一条不可继承的 `用户:(F)`**（`icacls` 实测），于是**新建的顶级目录完全没有用户 ACE**，只有 `Authenticated Users:(I)(M)` —— 有写权、没有 `WRITE_OWNER`。这类目录一旦需要打标签/再授权，必然 `Access is denied`（Win32 5）。
3. 因此过去的修复是"agent 临场用完全权限跑 icacls"，**每次只修当前报错的那几个目录、ACE 还写成不可继承**，于是下次新建目录又坏，循环往复：4 次修复审批、145 分钟等待、19 次 `grantWrite Win32 5`、1957 个文件打标签失败。

**解法一句话**：把 ACL 当成 workspace 的**受管属性**——① 根上补一条可继承的用户完全控制 ACE（治本）→ ② 全量体检修复一次（清历史欠账）→ ③ relay 插件在宿主进程内常驻体检/增量自愈（不再走审批）→ ④ 手机端可见、可一键修复、可回滚。

> **2026-11-11 需求变更**：用户要求"已有的不用修复，只保证以后新开启的会话有权限，且覆盖所有工作区"。因此**主线改为 §11 的"会话准入自愈"**：不再做一次性全量修复，而是在新会话首次工具调用前，对该会话的工作区做一次最小准入准备（根级 `WRITE_OWNER`），其余交给 DSH 自己的授权；每个工作区只准备一次并缓存。§3 的 L1/L2 保留为兜底与背景能力。

---

## 1. 机制：DSH 在 Windows 上到底需要什么

`grantWrite()`（`sandbox-windows-acl/src/acl.ts:353-413`）对**每个可写根目录**做一次"三合一"编辑：

| 编辑 | 内容 | 谁能做 |
|---|---|---|
| capability ACE | `S-1-4-<hash>:(OI)(CI)(W,D,DC)` —— 受限令牌认这个 SID | 需要目标对象 **WRITE_DAC** |
| world 删除拒绝 | `Everyone:(CI)(DENY)(DC)`（只对容器继承，防止越权删子项） | 同上 |
| Low 完整性标签 | `Mandatory Label\Low:(OI)(CI)(NW)` —— 低完整性进程才能写入 | 需要目标对象 **WRITE_OWNER**（标签在 SACL 里） |

`workspace-write` 的可写根 = workspace + 系统临时目录（`sandbox/src/roots.ts:52-55`）；DSH 只对**根**调用 `grantWrite`，靠 `OI|CI` 继承覆盖整棵子树。

幂等优化：若"精确 ACE + deny + 标签"三者都已存在，**跳过写入**（`acl.ts:386-395`，注释说明否则会"eager inheritance，大工作区要几分钟"）。

**推论**：DSH 的授权模型假设 workspace 是一棵"ACL 干净、可继承"的树。一旦某个对象：

- 缺用户 `WRITE_OWNER`（→ 打标签失败，`SetNamedSecurityInfoW failed (Win32 5): grantWrite(...)`）；
- 有 protected DACL / 显式 ACL 阻断继承（→ 该子树拿不到 capability ACE 与 Low 标签）；
- 是非 owner / 非 NTFS / 重解析点（→ 直接不可修）；

那么 `workspace-write` 对这个对象就是"永久写不进去"，直到有人用完全权限手工修。

### 实测证据（2026-11-11 18:40，本机）

```
# workspace 根：用户 ACE 不可继承
D:\Code\VibeCoding\DSHRemote  DESKTOP\user:(F)          ← 没有 (OI)(CI)
                              S-1-4-840176450-129811360:(OI)(CI)(W,D,DC)
                              Everyone:(CI)(DENY)(DC)
                              Mandatory Label\Low:(OI)(CI)(NW)

# 在根下新建目录（受限进程创建）：完全没有用户 ACE → 没有 WRITE_OWNER
D:\...\_acl-probe-dir        Everyone:(I)(CI)(DENY)(DC)
                             S-1-4-840176450-129811360:(I)(OI)(CI)(W,D,DC)
                             BUILTIN\Authenticated Users:(I)(M)   ← 只有 Modify，无 WRITE_OWNER
                             Mandatory Label\Low:(I)(OI)(CI)(NW)

# 在已修复目录 _work 下新建目录：继承到 (I)(OI)(CI)(F) → 可继续修
D:\...\_work\_acl-probe2     DESKTOP\user:(I)(OI)(CI)(F)        ← 健康
                             S-1-4-...:(I)(OI)(CI)(W,D,DC)
                             Mandatory Label\Low:(I)(OI)(CI)(NW)
```

历史日志侧同证：19 次 `SetNamedSecurityInfoW failed (Win32 5): grantWrite(...)`（10 个会话）；一次 `icacls /setintegritylevel /T` 对 7281 个对象成功、**1957 个 Access is denied**（正是缺 `WRITE_OWNER` 的那些）；4 次修复审批、其中一次等了 145 分钟。

### 为什么"官方修复脚本"也修不持久

DSH 随包分发的 `diagnose-windows-sandbox-acl.ps1` 有两个关键限制（读源码确认）：

1. **它只写 DACL 与继承保护，绝不写 owner/group/SACL**（注释："Only the DACL and its inheritance protection are written: never owner, group or SACL"）→ **它不能补 Low 完整性标签**（标签在 SACL 里），只检测不修复；
2. 它给当前用户补的 FullControl ACE **不带 `(OI)(CI)` 继承标志**（`AddAccessRule` 只传 SID + FullControl + Allow）→ 只修好"报错的那一个目录"，**该目录之后新建的子对象仍然缺 `WRITE_OWNER`**。

所以现状是：agent 跑 skill 脚本 → 修好当前报错路径 → 下一次新目录/新构建产物又坏 → 再报错、再审批。这正是"修了又坏"的闭环。

---

## 2. 设计目标与不变量

**目标**
- 新建/新引入的对象**出生即健康**（可继承），不依赖事后修补；
- 历史欠账**一次修清**；
- 修复**不再消耗审批、不再由模型临场执行**；
- 用户能看见状态、能一键修、能回滚。

**不变量（任何修复都必须满足）**
1. 只改 workspace 根之下的对象，绝不越界；
2. 只"加"必要权限，不删除既有 allow ACE（唯一的清理对象是遗留的 `S-1-15-2-*` AppContainer 包 ACE）；
3. 不改 owner、不动 SACL（除 Low 标签）、不删 DSH 预期的 `Everyone:(CI)(DENY)(DC)`；
4. 每次修改前备份 SDDL，修改后验证，失败可一键还原；
5. 幂等：重复执行不产生额外变化。

---

## 3. 方案：四层

### L0 基线修复（治本，一次性，两三条命令）

在 workspace 根补一条**可继承**的用户完全控制 ACE（用 SID，避免本地化账户名问题）：

```powershell
# 设计参考，不在本轮执行
icacls "<workspace>" /grant "*<用户SID>:(OI)(CI)F"
```

- 效果：此后根下新建的任何对象都继承 `(I)(OI)(CI)(F)` → 具备 `WRITE_OWNER` → 可被 DSH 打标签、可被后续修复；等价于标准 Windows 目录的默认语义，不扩大攻击面（用户本来就是 owner）。
- **必须带 `(OI)(CI)`**：现有历史修复和 DSH 自带 skill 脚本都写成"只作用于该对象"，这正是复发根源（见 §1 末）。这一条是它们没做、也做不了（脚本不写 SACL/继承标志）的补充。
- 保留根上已有的不可继承 `(F)`（冗余但无害），或统一替换为可继承版本。
- 验收：新建顶级目录后 `icacls` 出现 `(I)(OI)(CI)(F)`，且 `_acl-probe` 对照通过。

> 这一步是"不再复发"的关键。只做全量修复、不做 L0，等于把问题推迟到下次新建目录。

### L1 全量体检 + 修复（清历史欠账，一次性）

> **2026-11-11 需求变更**：用户确认"已有的不用修复，只保证以后新开启的会话有权限，且要覆盖所有工作区"。因此本节的一次性全量修复**不再是主线**，改为 §11 的"按工作区、按新会话准入自愈"；L1 保留为"准入准备遇到大量存量对象时"的后台兜底能力。

对 workspace 树做一次扫描，按对象状态分类处理：

| 对象状态 | 判定 | 动作 |
|---|---|---|
| 缺用户 F（或有效 `WRITE_OWNER`） | 无法打标签/再授权 | 补 `用户:(OI)(CI)F`（目录）/ `用户:F`（文件） |
| 缺 capability ACE | 受限进程写不进去 | 补 `S-1-4-<hash>:(OI)(CI)(W,D,DC)` |
| 有 capability ACE 但标签不是 Low | 低完整性进程无法写 | `icacls /setintegritylevel (OI)(CI)Low` |
| 有 `S-1-15-2-*` 包 ACE | 旧 AppContainer 残留，会与授权冲突 | 移除该 ACE（只移除这一类） |
| protected DACL / 继承被阻断 | 子树收不到继承 | 打开继承（`/inheritance:e`）后按上面补 |
| 非 owner / 非 NTFS / reparse point | 不可修 | 报告，建议移出 workspace 或换成本地 NTFS 路径 |

实现建议：**复用 DSH 随包分发的 `diagnose-windows-sandbox-acl.ps1`**（在安装包内 `dsh/node_modules/@deepseek-ai/dsh-sandbox-windows-acl/assets/diagnose-windows-sandbox-acl/`），它已处理 WRITE_DAC/WRITE_OWNER 缺失、包 ACE 清理、备份与回滚；但必须在它之上补三类它不做的修复：**（a）给用户 F 加 `(OI)(CI)` 继承标志；（b）补 Low 完整性标签（脚本只检测不写 SACL）；（c）补 capability ACE（必要时）**。

- 排除：`.git` 可保留（文件多但同构，修复无副作用）；跳过 reparse point；不跟随时可变/网络路径。
- 分批：按目录分批（每批 ≤2000 对象），每批写备份与校验；失败对象记录后继续（可配"遇错即停"）。
- 验证：修复完成后用**受限探针**实测（见 §5），不能只看 ACL 读回。

### L2 常驻体检 / 增量自愈（relay 插件，宿主进程内，无需审批）

> **2026-11-11 需求变更**：L2 的"常驻自愈"形态收缩为 §11 的"会话准入守卫"——只在**新会话的 workspace 第一次出现**时做一次准入准备，之后靠缓存 O(1) 放行；不再做周期性全量扫描。

**关键点**：relay 插件运行在 DSH 宿主进程里，用的是**用户自己的令牌，不受沙箱限制**，所以可以直接执行 ACL 检查与修复——**不需要审批、不占用回合**。这正是"体验优化"的核心：把修复从"模型遇到错误后申请完全权限"变成"后台自动做"。

触发时机（从便宜到贵）：

| 触发 | 检查范围 | 成本 |
|---|---|---|
| DSH 启动 / 插件加载 | 根 + 一级子目录 + 已知构建目录（`obj`/`bin`/`.git` 元数据） | <200ms |
| 新会话开始前 | 同上（只读） | <50ms |
| workspace 注册 / 首次扫描 | 全量一次 | 秒级～分钟级（分批） |
| 观测到失败信号 | 失败路径及其祖先链 | 按需 |
| 手机/管理页手动"体检" | 可选全量 | 按需 |

失败信号怎么拿：relay 已在宿主内，可订阅会话事件（`subagent`/`tool` 结果不直接可见），务实做法是**兜底轮询**：检测最近会话日志里是否出现 `grantWrite Win32 5` / `SANDBOX_UNAVAILABLE` / ACL 类 `Access is denied`（日志在本地，读取代价低），出现则触发一次定点体检。

**修复模式**（可配，默认 `detect`）：

| 模式 | 行为 |
|---|---|
| `off` | 不做任何检查 |
| `detect`（默认） | 只读体检，发现问题在手机/桌面提示，用户点"修复"才动手 |
| `repair-safe` | 自动修"可安全判定"的子集：补用户 F、补 capability ACE、补 Low 标签、清 `S-1-15-2-*`；遇不可修对象停下并报告 |
| `repair-all` | 同上 + 打开继承（`/inheritance:e`）等更强动作，仅限高级用户 |

建议默认 `detect` + 首次安装向导里引导跑一次 `repair-safe`。

### L3 兜底与不可修场景

- 非 owner / 无法取得 WRITE_DAC：报告"需人工/需管理员"。
- reparse point（junction/symlink）指向 workspace 外：不跟随、标记"沙箱写不到目标"，建议改用真实目录。
- OneDrive 占位文件/云同步目录、网络盘、FAT/exFAT：直接不建议作为 workspace，提示迁移。
- workspace 根本身不可写：明确说明"当前目录权限被组策略/安全软件锁定"，给出体检报告。

### L4 体验（手机 + 桌面）

- **设置页新增"Windows 权限体检"卡片**：当前状态（健康 / 需修复 N 项 / 需人工 M 项）、最近体检时间、一键"体检/修复"、历史与回滚入口。
- **会话内权限时间线**标注层级：这条失败是"ACL 层"，并给"一键修复"按钮（不必回桌面）。
- **启动静默体检**：健康就不打扰；异常弹一次卡片（不重复弹）。
- **修复不再经过 agent**：模型不再需要 `skill: diagnose-windows-sandbox-acl` 临场排障（该 skill 保留给人工排障）；会话里也不会再出现"跑 icacls 的审批"。

---

## 4. 可靠性设计（为什么这套能"可靠"）

| 维度 | 做法 |
|---|---|
| 幂等 | 按"精确 ACE/标签是否存在"判定，已存在即跳过；重复运行零变化 |
| 备份/回滚 | 每个对象的 SDDL 快照 + 一次性恢复脚本；批量修复产生可回滚记录（参考 DSH 自带脚本的 `-Restore` 机制）；回滚本身也做验证 |
| 边界 | 只处理 workspace 根及其后代；`-AllowRoot` 式边界检查（realpath 展开后再比较，防 junction 逃逸） |
| 并发 | 与 DSH 自身的 `grantWrite` 共用"路径锁"语义（DSH 有 per-path 锁）：修复只安排在启动/会话前，或对每个对象串行处理；避免与正在运行的构建同时改 ACL |
| 性能 | 全量分批；日常只查"根 + 一级 + 构建目录 + 最近修改目录"；用 SDDL 指纹缓存（根与一级目录的 ACL 哈希）跳过未变化子树 |
| 失败策略 | fail-closed：修复失败不停留在"半修"状态——记录并回滚该对象；连续 N 个失败则整体停止并报告 |
| 审计 | `~/.dsh/remote/acl-audit.jsonl`：时间、模式、扫描/修复/跳过/失败计数、失败原因、备份路径、耗时 |
| 版本解耦 | 不改 DSH 宿主；只补它需要的 ACL 前置条件。DSH 升级后语义不变（`WRITE_OWNER` 要求是其设计常量） |
| 安全 | 默认只读；修复需一次明确授权（或用户开启自动）；只加不删（除 `S-1-15-2-*`）；不扩大给 Everyone/Users 的写权限 |

---

## 5. 验证方法（不只看 ACL，要实测）

1. **继承验证**：在 workspace 根新建目录 → `icacls` 必须出现 `(I)(OI)(CI)(F)`；在修复过的子目录新建 → 同样。
2. **受限写探针**：用受限令牌（`workspace-write`）在根、一级子目录、`obj/bin` 目录各写/删一个临时文件，全部成功才算修复完成（参考 DSH 的 token 机制；或直接跑一次 `sandbox-write-probe` 小工具）。
3. **失败指纹清零**：修复后连续 7 天，会话日志里 `SetNamedSecurityInfoW failed (Win32 5)`、`SANDBOX_UNAVAILABLE`、ACL 类 `Access is denied` 为 0。
4. **回滚验证**：任取一次批量修复，执行回滚，ACL 应逐对象与修复前一致。
5. **审批清零**：整个体检/修复流程产生 0 次 `approval/asked`。

---

## 6. 验收标准

| 指标 | 现状 | 目标 |
|---|---|---|
| 新建顶级目录具备用户 `(OI)(CI)F` | ❌（实测无用户 ACE） | ✅ 100% |
| `grantWrite Win32 5` / `SANDBOX_UNAVAILABLE` | 19 次 / 10 会话 | 连续 7 天 0 |
| ACL 类 `Access is denied` | 1957 文件/次 | 0 |
| ACL 修复消耗的审批 | 4 次（含 145 分钟等待） | 0（宿主进程内执行） |
| 体检耗时（root + 一级） | 无 | < 200ms |
| 全量修复（1 万对象） | 无 | < 5s，可分批 |
| 用户可感知 | 无（只能看 agent 报错） | 手机状态卡片 + 一键修复 + 历史/回滚 |

---

## 7. 落地形态（与已确认的 relay 扩展一致）

1. **新增 relay 模块** `relay/src/acl.mjs`（或独立 `dsh-remote-acl` 插件）：
   - `probe(scope)` 只读体检 → 返回结构化报告；
   - `repair(targets, mode)` 执行修复（调用随包脚本/生成 icacls 命令），写审计；
   - `restore(backupId)` 回滚；
   - 暴露 HTTP API（仅 loopback/已配对 owner 可调）给手机与管理页。
2. **脚本来源**：优先用 DSH 安装包内的 `diagnose-windows-sandbox-acl.ps1`（从 asar 提取到临时目录执行，或开发态直接引用 checkout），并在其上补"Low 标签 + capability ACE"两类修复；不可用时用内置的 icacls 生成器兜底。
3. **手机端**：设置页卡片 + 会话时间线入口；命令走 relay 的 owner API。
4. **与 DSH 的关系**：不改宿主、不依赖内部 API 的稳定性；DSH 升级只需回归一次"体检 → 探针"。

---

## 8. 风险与"不做"清单

- **不做**：给 Everyone/Users 加写权限；删除 DSH 的 capability ACE 或 `Everyone:(CI)(DENY)(DC)`；改 owner；对非 workspace 路径做修复；在构建进行中并发改 ACL。
- **风险**：ACL 是敏感面 → 默认只读 + 显式授权 + 备份回滚 + 审计；修复范围永远以 workspace 为边界。
- **风险**：全量扫描大仓库耗时 → 分批 + 增量 + 指纹缓存，首次在空闲时执行。
- **风险**：`repair-all`（打开继承）可能改变用户自定义 ACL 语义 → 仅高级选项，且必须先备份。

---

## 9. 待确认

1. 默认模式选 `detect`（只提示）还是首次安装自动跑一次 `repair-safe`？
2. 是否接受在 workspace 根加**可继承**的用户完全控制 ACE（L0）——这是"不复发"的前提。
3. 全量首次扫描安排在 DSH 启动后空闲时，还是仅由用户手动触发？

---

## 10. 实施顺序与"最小集"（回答"能否只做第 1 步"）

### 依赖关系

```
L0 基线（根加可继承 F）
   └─► L1 全量体检修复（修复时需要 WRITE_OWNER，所以必须先有 L0；否则修到新对象又会失败）
          └─► L2 常驻自愈（复用 L1 的修复引擎与判定规则）
                 └─► L3 兜底分类 / L4 手机状态卡片（消费 L2 的 probe 结果）
```

### 只做 L0 会怎样

- **能止住的**：根下新建目录/构建产物"出生即缺 `WRITE_OWNER`"的循环——这是当前 `grantWrite Win32 5` 的主因，也是"越修越坏"的来源。
- **止不住的**：
  1. **存量欠账不修**：已经缺 capability ACE / Low 标签、或 protected DACL 阻断继承的子树，L0 只对**新建**对象生效，不会回头修它们（我们只抽样确认了 10 个目录健康，没有全树扫描）；
  2. **不可见/不可验证**：修没修好、什么时候坏的，系统和你都不知道，只能等 agent 再撞一次墙；
  3. **不可回滚**：手工跑 icacls 没有备份机制，改错无法还原；
  4. **仍需执行一次**：L0 本身就是一次 ACL 修改（谁来跑、何时跑）。
- 结论：**L0 是"必须做且最划算"的一步，但单独做不构成"可靠"**——它把"持续变坏"变成"不再变坏"，却没处理"现在已经坏的部分"。

### 推荐的最小上线集

| 组合 | 覆盖 | 工作量 | 结论 |
|---|---|---|---|
| 只做 L0 | 防复发，不治存量、不可见 | 分钟级 | ❌ 不够，但**必须先做** |
| **L0 + L1**（推荐） | 防复发 + 清存量（一次维护窗口） | 半天（含探针验证） | ✅ **最小可用**：之后 ACL 应长期稳定 |
| L0 + L1 + L4-最小 | 再加"手机可见：健康/异常 + 一键体检" | +半天 | ✅ 推荐，能"看得见"才算可运维 |
| 全量 L0–L4 | 再加常驻自愈/兜底/一键修复/回滚 | +1–2 天 | 多 workspace、经常新建工程、希望零运维时值得 |

**建议**：先做 **L0 + L1**（同一个维护窗口内，一次做完，做完立刻用受限写探针验证），再观察一周；如果期间不再出现 `grantWrite Win32 5` / `SANDBOX_UNAVAILABLE`，就把 L2 降级为"轻量只读体检 + 异常提示"，不必上自动修复；如果仍有漂移（多 workspace、外部工具频繁改 ACL），再做 L2 的自愈。

### 先后顺序不能反的原因

- 先 L1 后 L0：L1 修到"新建出来的中间目录"时，那些目录仍缺 `WRITE_OWNER` → 修复本身会失败（正是 1957 个文件 Access denied 的复现）。
- 先 L2 后 L1：自愈逻辑要复用 L1 的判定与修复实现，否则等于写两套。
- L3 可以随时补（成本低），但它只影响"修不了时的说明质量"，不阻塞主线。

---

## 11. 需求变更后的主线：会话准入自愈（只保新会话、覆盖所有工作区）

> 新需求（2026-11-11）：**已有的不用专门修复；保证以后新开启的会话有权限；新会话可能在任意工作区，必须对所有工作区生效。**
> 结论：**可做，且不需要一次性迁移。** 把方案从"一次性全量修复"改成一个**按会话、按工作区触发的准入守卫（Session Admission Guard）**。

### 11.1 核心思路

不复制 DSH 的授权逻辑，只**保证 DSH 自己能授权成功**：

> DSH 的 `grantWrite` 失败只有两个业务原因：目标目录缺 `WRITE_DAC`/`WRITE_OWNER`（`acl.ts:368-370`）。我们只需在新会话的工具真正执行前，确保该会话工作区根**具备 `WRITE_OWNER`**（即根上有一条可继承的用户完全控制 ACE），剩下的 capability ACE 与 Low 标签交给 DSH 自己的 `grantWrite` 去写 —— 它本来就做这件事，只是以前没权限做成。

这样做的三个好处：
1. **不需要复刻 DSH 内部的 capability SID 派生算法**（那是内部包，不稳定）；
2. **天然覆盖所有工作区**：只看会话的 cwd，没有任何路径白名单；
3. **修复量最小**：每个工作区只需一次根级 ACL 写，不遍历、不逐对象改。

### 11.2 架构：两个钩子 + 一份缓存

宿主插件（relay，进程内、用户令牌、不受沙箱限制、**无需审批**）注册：

| 钩子 | 时机 | 动作 |
|---|---|---|
| `ctx.on('session/created', session)` | 新会话创建（含 subagent 子会话） | 读 `session` 的 cwd（`SessionHeader.cwd`），异步启动该工作区的"准入准备"（预热，不阻塞创建） |
| `ctx.on('tools/pre-execute', async (exec, next), { prepend: true })` | 每次工具执行前 | 取 `exec.agent` 对应会话的 cwd → `await ensureReady(cwd)`（有超时上限）→ `return next()`。缓存命中时 <10ms |
| `ctx.workingDirectory.ensure(agent, signal)` | 需要解析 cwd 时 | 与 auto-review 相同的取工作目录方式，保证与实际执行一致 |

**保证语义**：`tools/pre-execute` 的 await 在工具 body（含沙箱 init → `grantWrite`）之前完成，所以"新会话第一次工具调用"时该工作区已经具备 `WRITE_OWNER`，DSH 的授权会成功。

### 11.3 准入准备做什么（按成本递增、按需短路）

```
ensureReady(cwd):
  1. canonical 化（realpath，处理 junction 逃逸）
  2. 排除判定：非 NTFS（网络盘/FAT）、根是 reparse point、系统目录 → 标记 skip + 提示
  3. 缓存命中？（路径 + 根 ACL 关键指纹 + 准备版本） → 直接返回（<10ms）
  4. 根健康检查（只读 1 次）：
       - 有可继承的用户完全控制 ACE？
       - 有 capability ACE？有 Low 标签？无遗留 S-1-15-2-*？
  5. 不健康 → 最小修复（只做一件必需的事）：
       icacls "<cwd>" /grant "*<用户SID>:(OI)(CI)F"
     （若根缺 WRITE_DAC，先补；若发现 protected DACL 阻断的子项，按需对那一支打开继承）
  6. 写缓存、写审计
  7. 返回 ready / needs-manual(原因)
```

- **第 5 步是唯一会改动 ACL 的动作**，而且是最小集：一条可继承的用户完全控制 ACE。
- 不在准入阶段主动补 capability ACE / Low 标签——下一步工具执行时 DSH 的 `grantWrite` 会写（它现在有权限了）；如果 DSH 因其他原因失败，再由"失败信号触发"的定点修复补（§11.6）。
- 不在准入阶段做全树逐对象遍历——只有 Windows 传播 ACE 这一层开销（见 §11.4）。

### 11.4 唯一的代价：Windows 会把这条 ACE 传播到该工作区已有对象

给根加可继承 ACE 时，Windows 会自动把 ACE 传播到该目录**已有**的子对象（这正是 DSH 注释里说的 "eager inheritance，大工作区几分钟"）。

- 对"新工作区/新项目"：树很小，毫秒级。
- 对"已有大仓库"：首次准入可能耗时（几秒到几分钟），但**只发生一次**（缓存后每次会话 <10ms）。
- 这个传播**顺带把存量对象的 `WRITE_OWNER` 补齐了**——这正是用户说"已有不用专门修复"的部分：我们不去主动做存量修复，但修根时它的传播副作用已经把"该工作区"治好了，且只发生在被新会话实际用到的那个工作区。
- 若用户严格不想让存量对象被改动：可选"严格模式"——只给根加 ACE、不传播（Windows 没有"设了继承但不传播"的开关，只能用底层 API 分两步：先给根加非继承 F，再用低层接口把 ACE 改成继承而不触发传播；代价是新旧对象行为不一致，**不推荐**）。默认走传播模式。

### 11.5 覆盖所有工作区的规则

- **无白名单**：任何 cwd 都走同一套逻辑（包括 `D:\Code\...`、`C:\Users\...\Downloads` 等）。
- **排除**：网络盘/UNC、FAT/exFAT、根为 reparse point（junction/symlink）、系统盘保护目录（`C:\Windows`、`Program Files`）→ 标记 `skip`，在该会话首次工具调用前给一条明确提示，不硬改。
- **子会话/委派**：`session/created` 同样触发，但缓存命中，成本可忽略（同一 workspace 只准备一次）。
- **会话 resume（重开旧会话）**：不依赖 `session/created` 也能保证——`tools/pre-execute` 的守卫对每次调用生效，缓存未命中就即时准备。

### 11.6 失败信号触发的定点修复（只修当前会话这次）

若首次工具调用仍失败（`grantWrite Win32 5` / `SANDBOX_UNAVAILABLE` / ACL 类 `Access is denied`）：

1. 从失败路径向上找"第一个不可写祖先"，只对这一条链做定点修复（补 F + 必要时开继承），而不是全树；
2. 修复后提示"已修复，可重试"；
3. 若链上出现"非 owner / 无法取得 WRITE_DAC / reparse" → 标记该工作区 `needs-manual`，后续会话直接给提示，不再反复尝试。

失败信号来源：宿主插件可在工具结果/会话事件里捕获（DSH 的 `tools/post-execute` 或会话事件），无需模型配合。

### 11.7 缓存与失效

`~/.dsh/remote/acl-admission.json`：

```json
{
  "D:\\Code\\VibeCoding\\DSHRemote": {
    "fingerprint": "<根 ACL 关键项哈希>",
    "preparedAt": "2026-11-11T12:00:00Z",
    "version": 1,
    "result": "ready"
  }
}
```

- 失效条件：根 ACL 变更（指纹不符）、用户手动"重新体检"、插件版本升级带了新的准备逻辑；
- 会话侧不需要额外状态：每次工具调用前查缓存是常数时间。

### 11.8 与"已有不修"的边界（已确认）

> **决策（2026-11-11）**：采用 **模式 A** —— 默认允许"根 ACE 传播、顺带补齐该工作区存量"。
> 理由：Windows 加可继承 ACE 时必然传播到已有子对象；这个副作用恰好把"被新会话实际用到的那个工作区"的存量 `WRITE_OWNER` 补齐，从而真正满足"保证新会话有权限"，同时不主动扫描/触碰未被使用的工作区。

| 模式 | 行为 | 状态 |
|---|---|---|
| **A 传播补齐（默认）** | 为新会话用到的工作区做根级准备；Windows 传播顺带补齐该工作区存量对象 | ✅ **已确认** |
| B 严格不碰存量 | 只补根、不传播（需底层两步写，新旧对象行为不一致） | 保留为高级开关，不推荐 |
| C 只告警不修 | 等于现状 | ❌ 无法满足需求 |

配套约束：
- 传播只发生在**被新会话首次使用**的工作区，未使用的工作区 0 触碰；
- 传播完成后写缓存，同一工作区后续会话不再触发；
- 审计记录：路径、传播对象数、耗时、失败数，可在手机端查看。

### 11.9 验收标准（替换 §6 中的全量指标）

| 指标 | 目标 |
|---|---|
| 新会话首次工具调用前，其 workspace 根具备可继承用户 F | 100%（缓存命中 <10ms） |
| 新会话首个需要写工作区的工具调用 | 不再出现 `grantWrite Win32 5` / `SANDBOX_UNAVAILABLE` / ACL 类 `Access is denied` |
| 覆盖工作区 | 任意本地 NTFS 路径；排除项明确提示 |
| 准入准备耗时 | 新/小工作区 <1s；大工作区首次 ≤ 可配上限（默认 30s，超时转后台并提示） |
| 存量传播 | 被新会话使用过的工作区：存量对象 `WRITE_OWNER` 补齐；未使用工作区 0 次触碰 |
| 审批消耗 | 0（宿主进程内执行） |
| 对未被新会话使用的工作区 | 0 次触碰 |

### 11.10 落地（相对 §7 的增量）

- `relay/src/acl-guard.mjs`：`ensureReady(cwd)`、`probe(root)`、`repairRoot(root)`、`repairChain(failingPath)`、缓存与审计；
- 注册 `session/created`（预热）+ `tools/pre-execute`（等待）+ 失败信号钩子；
- `inject` 增加 `tools` / `sessions`（确保 waterfall 在服务就绪后注册，与 auto-review 同款写法）；
- 不需要新的系统服务/计划任务；插件加载即生效；
- 手机端只展示状态（§4 的 L4），不参与修复路径。

### 11.10a 实现状态（2026-11-11，v0.5.0）

已落地：

| 文件 | 内容 |
|---|---|
| `relay/src/acl-guard.mjs` | 探测（PowerShell 取 SDDL）、判定（可继承用户 F 为就绪条件）、修复（`icacls /grant *SID:(OI)(CI)F`）、验证、持久化缓存、审计、`ensureReady`/`warm`/`noteInstrumentFailure`/`status`、并发去重、超时转后台、排除规则 |
| `relay/src/index.mjs` | `session/created` 预热、`tools/pre-execute` 等待、`tools/post-execute` 失败指纹复查；`collectResultText`；`aclGuard` 配置 |
| `relay/test/acl-guard.test.mjs` | 23 个单测（SDDL 解析、排除、缓存、并发、超时、失败记忆、reparse、状态） |
| `relay/test/acl-guard.smoke.mjs` | 端到端（非受限执行）：复现"用户 ACE 不可继承"缺陷 → 守卫修复 → 子对象继承 → 缓存/审计，`RESULT PASS` |
| `relay/package.json` | 0.5.0；`test` 纳入 acl-guard；`smoke:acl`；`check` 覆盖新文件 |
| `scripts/package-relay-plugin.ps1` | 打包前执行 acl-guard 单测 |
| `relay/README.zh.md` | `aclGuard` 配置与验证说明 |

**验证记录**：`npm test` 42/42 通过；`npm run check` 通过；在 `%TEMP%` 上执行的端到端 smoke 全项 PASS（含"已存在的子目录继承到用户完全控制"与"审计记录 repaired"）。

**线上验证（2026-11-11 20:39，relay 0.5.0 已装入桌面 DSH）**：

| 对象 | 结果 |
|---|---|
| 安装 | `install-relay-bundle.ps1` → relay 0.5.0，self-check OK，gateway online，`pairing.relayVersion=0.5.0` |
| `D:\Code\VibeCoding\Arc.Avalonia` | `repaired`，耗时 **4754ms**，capability=true，packageAce=false |
| `D:\Code\VibeCoding\DSHRemote` | `repaired`，耗时 **7118ms**，capability=true，packageAce=false |
| 根 ACL（修复后） | 新增 `DESKTOP\user:(OI)(CI)(F)`；capability ACE、`Everyone:(CI)(DENY)(DC)`、Low 标签保持 |
| 新顶级目录 | 继承到 `(I)(OI)(CI)(F)` + `(I)(OI)(CI)(W,D,DC)` + Low 标签 → 出生即健康 |
| 缓存/审计 | `~/.dsh/remote/acl-admission.json`、`acl-audit.jsonl` 均按预期写入 |

下一轮待办：手机端状态卡片/一键体检（消费 `guard.status()`）、模式 B（不传播）、失败路径的精确祖先链定位（当前为整根复查）。

### 11.10b 手机端：状态卡片 + 一键体检（relay 0.5.1 / App 1.4.0）

**relay 侧（手机可达、令牌鉴权）**

| 方法 | 路径 | 鉴权 | 行为 |
|---|---|---|---|
| GET | `/__relay/acl/status` | `Authorization: Bearer <配对令牌>` **+ owner cookie**；本机 loopback 管理页免令牌 | 返回 `{ guard: { enabled, version, roots[], stats, options }, candidates[] }`，**不探测**（O(1)） |
| POST | `/__relay/acl/check` | 同上 | body `{ repair?: boolean, root?: string }`；对已知工作区逐个 `inspect`，`repair` 时对异常项 `ensureReady(force)` |

> **网关模式必须同时带 owner cookie**：公网网关是按 `dsh-auth` cookie 把请求路由到某台桌面的，缺 cookie 会直接返回 404 `unknown-session`（请求根本到不了 relay）；relay 自身再用 Bearer 鉴权/识别设备。App 的 `DshClient.Acl.cs` 两者都带。LAN 模式 cookie 无害。

- 令牌接受**两种**配对凭据（网关 remote token / LAN launch token），与手机实际配对方式一致；比较用 `timingSafeEqual`。
- 仅接受 relay 已知的工作区（guard 见过的 + `ctx.sessions` 里的 cwd），任意路径返回 `unknown-root`。
- 网关内部 loopback 转发**不**继承"本机免令牌"信任（`helpers='health'` 的代理必须带 Bearer），避免公网经网关无凭证调用。

**Core（手机协议层）**

- `IAclGuardClient` + `DshAclGuardStatus` / `DshAclRootStatus` / `DshAclCheckResult` / `DshAclRootCheck`（`Contracts.cs`）。
- `DshClient.Acl.cs`：复用已固定 TLS 的 `HttpClient`，携带 Bearer，解析 JSON；`404` 映射为"relay 版本过低（需要 0.5.0+）"，`401/403` 映射为配对失效/被拒，`{ok:false,error}` 映射为 `ResultInvalid`。

**App（设置页卡片）**

- `SettingsViewModel`：`AclAvailable` / `AclStatusText` / `AclDetailText`（前 3 个工作区+状态）/ `AclCheckedText` / `AclResultText` / `AclBusy`；`RefreshAclCommand`、`CheckAclCommand`（"一键体检并修复"）。
- `SettingsView.axaml`：新增「Windows 权限」卡片，按钮 + 结果摘要；未连接时提示"连接桌面后可体检"。
- `MainViewModel`：连接成功后 `Settings.AttachAclClient(client)`，断开/重建时 `DetachAclClient()`。

**验证**

- relay：`npm test` **53/53**（新增 `acl-api.test.mjs` 6 个：令牌鉴权/网关 loopback 不继承信任/check 透传/方法与前缀校验/旧版本回退）。
- Core：新增 `AclClientTests`（5 个：状态解析+Bearer、check 解析、旧 relay 404 文案、错误载荷、401 映射）。本机 VSTest testhost 因"打开父进程句柄被拒"无法启动（环境限制，非代码问题），用 `_scratch/acl-test-runner` 控制台直接调用 → **5/5 PASS**；xunit 测试保留给正常开发机/CI。
- 构建：`DshRemote.Core` / `DshRemote.App` / `DshRemote.App.Desktop` 均 0 warning 0 error；Android Release APK `dshremote-1.4.1-26-arm64-release.apk`（34.77 MB，sha256 `d8f28fb9…78e5e602`）已构建、签名并同步 VPS。<br>loopback 实测：`GET status` 返回 3 个已知工作区（均 ready）、`POST check` 全 PASS、未知路径 `unknown-root`；LAN 实测：无令牌/错误令牌 401、正确配对令牌 200。
- **真实网关链路实测**（`_scratch/acl-gateway-smoke.mjs`，模拟手机）：`/connect` 取得 owner cookie → **无 cookie 的 ACL 请求被网关 404 `unknown-session`** → cookie + Bearer 请求 200（3 个已知工作区全部 ready）→ 只读 `check` 全部 ready。这同时定位并验证了 1.4.0「读取失败: relay 版本过低」的根因（网关按 cookie 路由，缺 cookie 时请求到不了 relay）。

### 11.11 运行时序列（确认后的默认行为）

```
用户在新工作区 W 开会话
  └─ session/created → 读 session.header.cwd = W
        ├─ canonical 化、排除判定（网络盘/系统目录/reparse → skip + 提示）
        ├─ 缓存命中？ → 标记 ready，结束（<10ms）
        └─ 未命中 → 后台启动 prepare(W)：
              probe(root) → 需要？ repairRoot(root)（一条 icacls，带 (OI)(CI)）
              Windows 传播到 W 的存量对象（可能数秒~数十秒）
              写缓存 + 审计（对象数、耗时、失败数）

用户发第一条需要写 W 的指令
  └─ tools/pre-execute（prepend）→ await ensureReady(W)
        ├─ 已 ready → return next()
        ├─ 准备中 → 最多等 30s（可配）：
        │     ├─ 完成 → return next()（此时 grantWrite 必然成功）
        │     └─ 超时 → return next() 并提示"权限准备仍在后台进行；若本步失败可重试"
        └─ needs-manual（非 owner/reparse/网络盘）→ return next() 并给出明确原因

工具执行（DSH 沙箱 init → grantWrite 写 capability ACE + Low 标签）
  └─ 成功（因为根已有 WRITE_OWNER）
        └─ 若仍失败 → 失败信号触发 repairChain(失败路径祖先链) → 提示重试
```
