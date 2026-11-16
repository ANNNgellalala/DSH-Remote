# 桌面端自动审批（relay 0.7.1+ / App 1.8.3+）

手机 App 关闭后，审批不再无人应答：规则保存在桌面 relay 里，由宿主进程内的
`approval/request` 监听器直接以 **`allowed-once`** 结算。手机只负责编辑规则和查看审计。

```
手机设置页 ── GET/PUT /__relay/auto-approve ──► relay API ──► ~/.dsh/remote/auto-approve.json
                                                            │
审批发生 ──► ApprovalService.decide()                       │ 规则热加载
             └─► waterfall: relay listener (prepend) ◄──────┘
                   ├─ 命中：return 'allowed-once'（桌面/手机都不弹）
                   └─ 不命中：next() → api-remotes → 桌面 UI / 手机
```

## 1. 开关与树形范围

设置页「自动审批（桌面端值守）」是一个三级勾选树：

```
☐ 全部工作区
  ☑ 工作区 A
      ☑ 会话 A1
      ☑ 会话 A2
  ◪ 工作区 B（部分选中）
      ☑ 会话 B1
      ☐ 会话 B2
```

- **上级勾选 → 下级全部勾选；上级取消 → 下级全部取消。**
- **全部工作区**：勾选表示所有工作区/会话（含以后新增的工作区）都自动允许；再次点击（含从部分状态点击）会清空全部范围。
- **工作区**：勾选表示该工作区下所有会话（含以后新建的会话）都自动允许；其子会话可再单独取消，
  取消后该工作区变成“部分选中”，只自动允许仍勾选的会话。
- **会话**：勾选即只覆盖该会话；其所有代理、子代理、孙代理都自动审批（沿
  `session.header.parentSession` 向上回溯，最多 8 层）。
- 父勾选框支持三态：全部子项选中=勾选，部分选中=半选，全部未选=未选。
- 级联/回填通过 `SetSelected` 显式发送 `IsSelected` 的 PropertyChanged；缺少该通知会出现“父级已勾选、
  子级仍显示未选中”的界面（v1.8.3 修复，并有对应回归测试）。
- 范围为空且开关打开时，不会自动允许任何请求（fail-closed）。
- 没有有效期：规则持续生效，直到手机/桌面侧关闭或修改。

保存映射：

| 树状态 | 桌面规则 |
|---|---|
| 全部工作区勾选 | `all=true`（忽略列表） |
| 工作区完全勾选 | 写入 `workspaces[]`（覆盖该工作区以后新建的会话） |
| 工作区部分勾选 | 不写该工作区，勾选的会话写入 `sessions[]` |
| 全部取消 | `enabled` 保持，但两个列表为空 → 不自动放行 |

## 2. 安全边界

- 只返回 `allowed-once`，**永不自动拒绝**，也**永不代替回答提问**（`user-questions/request` 完全不碰）。
- 匹配器异常、规则文件损坏、请求已取消 → 一律 `next()` 退回人工审批。
- 每次自动放行写入 relay 日志，并保留最近 50 条内存审计（手机设置页显示最近 3 条）。
- 规则文件原子写、`0600`；JSON 损坏时自动停用（不会沿用旧规则继续放行）。
- 手机端旧的全量「自动允许审批」开关已删除；自动审批只剩这一条路径，避免范围被手机端绕过。

## 3. 规则文件

`%USERPROFILE%\.dsh\remote\auto-approve.json`（可用 relay 配置 `autoApprove.file` 覆盖）：

```jsonc
{
  "schemaVersion": 2,
  "enabled": true,
  "all": false,                   // true = 全部工作区（含以后新增），忽略列表
  "workspaces": [                 // 完全勾选的工作区（其所有会话，含以后新建）
    { "id": "ws-…", "path": "D:\\Code\\DSHRemote", "title": "DSHRemote" }
  ],
  "sessions": [                   // 部分勾选工作区里被单独勾选的会话
    { "id": "session-…", "title": "…", "cwd": "D:\\Code\\X", "parentSessionId": null }
  ],
  "revision": 12,
  "updatedAt": "2026-11-14T06:00:00.000Z",
  "updatedBy": "phone"
}
```

旧 `schemaVersion:1`（`mode: all|workspaces|sessions`）文件在加载时自动映射为 v2：`mode:all → all:true`，
其余保留 workspaces/sessions 列表；下一次手机保存时写成 v2。

手动编辑该文件后 relay 会热加载（目录监听 + 300ms 去抖），无需重启 DSH。格式错误会停用自动审批，
并在 `health.autoApprove.error` / 手机设置页显示原因。

## 4. HTTP API（relay 自有路由）

```
GET  /__relay/auto-approve
PUT  /__relay/auto-approve      (POST 等价)
```

- 鉴权：`Authorization: Bearer <pairing.Token>`（LAN = launchToken，公网 = remoteToken）；
  经公网网关时还需带 `dsh-auth-*` Cookie。loopback 管理页在 LAN 监听器上免鉴权，网关内部
  明文代理**不**继承 loopback 信任。
- `PUT` body 为完整规则文档（`enabled` / `all` / `workspaces` / `sessions`）；relay 校验后
  原子写盘并返回新文档（含 `revision`、`stats`、`recent`）。
- 失败：`401` 未授权、`400` 校验失败、`405` 方法不支持；旧 relay 无该路由时返回 `404`，
  App 提示需要 relay 0.7.1+。

health（`GET /__relay/health`）新增：

```jsonc
"autoApprove": {
  "schemaVersion": 2, "enabled": true, "all": false,
  "workspaces": 1, "sessions": 0, "revision": 12,
  "updatedAt": "…", "stats": { "allowed": 3, "errors": 0, "lastAllowedAt": "…" },
  "error": null, "file": "C:\\Users\\me\\.dsh\\remote\\auto-approve.json"
}
```

## 5. 验收 / 排障

- **手机 App 杀掉后仍生效**：选中当前工作区 → 关闭 App → 在桌面触发一次 workspace 外写入审批 →
  桌面不出现等待提示、文件写出、设置页统计/审计增加。
- **未选范围仍弹**：换到未勾选的工作区/会话触发审批，桌面与手机正常等待。
- **子代理继承**：选中父会话，子代理的审批也被自动允许。
- **提问不受影响**：`ask_user_question` 仍会弹问答卡。
- 规则未生效时看：relay health 的 `autoApprove.enabled/error`、桌面日志
  `[remote-relay] auto-approve ...`；确认 relay ≥ 0.7.1（`health.relayVersion`）、手机连接的
  就是这台桌面、规则的 workspace id 与当前工作区一致。

## 6. 本机验证（2026-11-14）

- relay 0.7.1 已安装到 desktop profile：`health.relayVersion=0.7.1`、`health.autoApprove` 存在，
  `scripts/relay-smoke.ps1 -IncludeWs` PASS（含 auto-approve 行）。
- API 三路均实测：loopback `GET/PUT`、LAN `Bearer launchToken`、VPS 网关（Cookie + `Bearer remoteToken`）
  `GET` 返回 200；错误 token 401；非法 `workspaces` 400。
- 规则文件热加载/失败关闭实测：写入非法 JSON 后 health 立即 `enabled:false` + `error`，恢复文件后自动回到
  `enabled:false`（原规则）且 `error:null`。
- 单测：relay 115/115（其中 auto-approve 20）、Core 187/187（其中 AutoApproveClient 5）、App 95/95
  （其中 AutoApproveViewModel 9）。
- **注意**：本机 DSH 当前审批策略为 `never`（danger-full-access），不会产生审批请求，因此“真实审批命中后
  桌面不弹窗”的端到端观察需要在 `workspace-write` 会话里做；监听器顺序已由 relay 单测
  （prepend 先于远端转发器）覆盖。

## 7. 版本与文件

- relay 0.7.1：`relay/src/auto-approve.mjs`（规则/匹配/监听器/API）、`proxy.mjs`、`index.mjs`。
- App 1.8.3：`DshClient.AutoApprove.cs`、`AutoApproveViewModel.cs`、`SettingsView.axaml`；
  旧 `AutoApproveApprovals` 已删除。
- 测试：`relay/test/auto-approve.test.mjs`（20 条）、`AutoApproveClientTests`（5 条）、
  `AutoApproveViewModelTests`（9 条，覆盖树级联/半选/PropertyChanged/保存映射）。
