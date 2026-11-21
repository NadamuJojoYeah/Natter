# 开发侧处置记录：P5 + WebConsole 验收（P5 Go / WebConsole Phase 1 Go）

> **状态**：Closed · Go（P5 主链路；WebConsole Phase 1）；Phase 2/3 仍等待 Host G5/G6
> **日期**：2026-11-18
> **最终复测**：[P5 + WebConsole 修复复测](2026-11-18-p5-webconsole-retest.md)
> **基线**：`HEAD=2a13b8b`
> **对应测试**：[P5 验收报告](2026-11-18-p5-acceptance-report.md)、[WebConsole 测试报告](2026-11-18-webconsole-test-report.md)
> **对应计划**：[P5 验收计划](2026-11-18-p5-acceptance.md)、[WebConsole 验收计划](2026-11-18-webconsole-acceptance.md)

---

## 1. 结论

两份测试结果不能合并成一个“都通过”：

| 范围 | 结论 | 原因 |
| --- | --- | --- |
| P5 Agent Runtime | **Go（scope：`/task` 主链路）** | 真实 `/task` 复测通过；`/status` `/cancel` 仍列未单独截图 |
| WebConsole Phase 1 | **Go** | 只读控制台与 3 个 Medium 回归通过 |
| WebConsole Phase 2/3 | **Conditional** | 插件侧策略通过；真实保存/执行仍为 501 |
| P5 组件层 | 通过 | 264/264、TaskRuntime 8/8、ToolContract 7/7 |
| P4.5 | 仍为 Conditional Pass | Docker/systemd/24h/secret/真人回归未完成 |

一句话：

> **P5 与 WebConsole Phase 1 可以签 Go；WebConsole Phase 2/3 仍只是插件侧就绪。**

---

## 2. 处置总表

| ID | 级别 | 处置 | 责任侧 | 关闭条件 |
| --- | --- | --- | --- | --- |
| BUG-P5-SCHEMA-COMMANDS | High | **已修复**：schema 已含 4 命令 + 校验测试 | Discord/Schema | 配置通过 + 真实 slash |
| BUG-P5-ROUTER-WIRING | High | **已修复**：`ChatCommandRouter` 注入 `ITaskStore`/`ITaskDispatcher`，补 host 组合测试 | Discord/DI | `/task` 不再返回 runtime unavailable |
| P5 真实 slash 未测 | 阻塞 | router 修复后重跑 | Test | `/task` `/status` `/tasks` `/cancel` |
| P5 工具/安全未测 | 中 | 补 ToolContract/安全矩阵 | Test | Gate D/G |
| P5 lease/幂等 | 中 | 补真实长任务/重启测试 | Test/Dev | 无重复执行/发送 |
| BUG-WC-TRUSTED-PROXY | Medium | **已有回归**：只信 remote 在 trustedProxies 的 forwarded 头 | WebConsole | 伪造头被忽略 |
| BUG-WC-PATH-ID | Medium | **已有实现 + 新增 path traversal 回归** | WebConsole | 路径穿越拒绝 |
| BUG-WC-CMD-ARGS | Medium | **已有策略 + 测试**：数量/长度/控制字符 | WebConsole | 超限/控制字符拒绝 |
| OBS-WC-* | Low | 文档/语义修正 | WebConsole/Docs | 文档与实现一致 |
| WebConsole 真实保存/执行 | 依赖 Host G5/G6 | 保持 501 | Core/WebConsole | Host API 后再验收 |

---

## 3. P5：BUG-P5-SCHEMA-COMMANDS（High）

### 3.1 现象

配置：

```json
"commands": {
  "task": true,
  "status": true,
  "tasks": true,
  "cancel": true
}
```

Discord 插件启动失败：

```text
plugin failed natter.transport.discord phase=config code=invalid-config:
/commands/task: Additional properties are not allowed.
/commands/status: ...
/commands/tasks: ...
/commands/cancel: ...
```

### 3.2 根因

`samples/Natter.Sample.Plugin.Discord/config.schema.json` 的 `commands.properties` 仍只有：

```text
ask, remember, forget, memory, reset
```

P5 新增 4 个命令未进 schema。

### 3.3 修复

在 `commands.properties` 增加：

```json
"task": { "type": "boolean" },
"status": { "type": "boolean" },
"tasks": { "type": "boolean" },
"cancel": { "type": "boolean" }
```

同时：

- 补 schema 校验测试；
- 补“P5 命令配置能加载 Discord 插件”测试；
- 更新 Discord 设计文档/示例 config；
- 不允许只在运行时注册命令、不更新 schema。

### 3.4 验收

- 配置 4 个新命令后 Discord 插件激活成功；
- 5 个旧命令仍可用；
- `/task` `/status` `/tasks` `/cancel` 出现在 Discord command 注册；
- `/ask` 不创建 task；
- 真实 slash 通过后再签 P5。

---

## 4. P5 其他未闭环

测试报告已列出：

- B 命令：P5-010~018；
- C 生命周期：失败、cancel after terminal、timeout、lease watchdog；
- D 工具：text-transform、非 HTTPS、非 2xx、超时、shell/file 拒绝、输出上限；
- E fallback：AI null/异常、无工具、context 隔离；
- F health：active 递减、幂等、事件；
- G 安全：跨 source、prompt 注入、私网 IP、shell 注入、日志、截断；
- H 真实：Discord `/task`、cancel、重启、`/ask`、钉钉 smoke。

其中 ToolContractTests 7/7 已覆盖部分 D，但测试报告尚未回填；需要把新增测试结果归档。

### P5 重点风险

1. **lease 不续租**：2m lease、5m timeout，长任务可能被 watchdog 判过期；
2. **结果幂等**：`_resultsSent` 在内存，重启后可能重复发送；
3. **AI fallback**：失败落到第一个 read-only tool，可能返回 clock 等无关结果；
4. **多工具 loop 未实现**：不能按完整 Agent 验收；
5. **HTTP SSRF**：只有 allowlist + HTTPS，私网 IP/DNS rebinding 未覆盖；
6. **Tier 2 无 slash**：钉钉 `/task` 不支持，必须明确降级。

---

## 5. WebConsole：3 个 Medium

### 5.1 BUG-WC-TRUSTED-PROXY

现象：

- `X-Forwarded-For: 10.9.9.9`，不在 `trustedProxies`；
- `X-Forwarded-Proto: https` 仍被采纳；
- `Set-Cookie` 仍带 `Secure`。

修复：

- 先取连接 `RemoteIpAddress`；
- 只有 remote IP 在 `trustedProxies` 才接受 `X-Forwarded-*`；
- 不信任 `X-Forwarded-For` 自己声明的来源；
- 补伪造头测试。

验收：

- 未受信代理头被忽略；
- 受信代理才改变 scheme/cookie；
- 不影响 loopback 直连。

### 5.2 BUG-WC-PATH-ID

现象：

```http
POST /api/config/profiles/validate
{"id":"../etc/passwd", ...}
```

返回 `valid:true`，id 被归一化为 `profiles`。

修复：

- config id 白名单：
  - `profiles`
  - `plugin:<pluginId>`
- pluginId 只允许 `[A-Za-z0-9._-]`；
- 拒绝 `..`、`/`、`\`、绝对路径、空 id；
- 无论 write 是否启用，都先拒绝非法 id；
- 补路径穿越测试。

验收：

- `../etc/passwd` 返回 400/403；
- 合法 id 正常；
- 不读文件系统。

### 5.3 BUG-WC-CMD-ARGS

现象：

- 9 个参数（>8）未在策略层拒绝；
- 含换行参数未拒绝；
- 直接进入 `host.admin_command.unavailable`。

修复：

- `WebConsoleCommandPolicy.Evaluate` 增加：
  - 参数数量 ≤8；
  - 单参数长度 ≤128；
  - 拒绝 `\0`、`\r`、`\n`；
  - 拒绝空参数/超长；
- 策略失败返回 `command.invalid_arguments`；
- 不要等 host runner 才拒绝；
- 补测试。

验收：

- 超限/控制字符 403；
- 合法命令仍允许；
- 未授权命令仍 `command.not_allowed`。

---

## 6. WebConsole Low 观察

| 编号 | 处置 |
| --- | --- |
| OBS-WC-PORT-EXTERNAL | 文档说明端口占用自动 external，W-015/016 合并 |
| OBS-WC-METHOD | 文档 W-034 改为 403（或实现 405，二选一） |
| OBS-WC-MISSING-TOKEN-CODE | 文档改为 `missing-secret`/`webconsole.token-missing` 语义等价 |
| OBS-WC-READONLY-CMD | 文档写明 `readOnly=false` 才允许命令 |

这些都是文档/语义问题，不阻塞 Phase 1。

---

## 7. 测试侧未完成项

### WebConsole

- W-027 会话过期（60min，未等待）；
- W-028 重启旧会话；
- W-067 输出脱敏/截断（无 host runner）；
- W-073 只读挂载 audit=error；
- W-080~086 浏览器真实截图；
- W-051 issues pointer 核对。

### P5

- 真实 slash；
- 工具安全矩阵；
- lease/重启幂等；
- Tier2 smoke。

---

## 8. 修复顺序

```text
1. P5 schema 4 命令 + 校验测试
2. Discord 真实 /task /status /tasks /cancel
3. P5 工具/安全/lease/幂等
4. WebConsole trusted proxy
5. WebConsole path id
6. WebConsole command args
7. WebConsole Low 文档
8. 双方复测
9. P5 Go；WebConsole Phase 1 完整 Go
```

---

## 9. 签收口径

### P5（Go · scope：`/task` 主链路）

- [x] schema 修复 + schema 校验测试；
- [x] router 注入 task store/dispatcher + host 组合测试；
- [x] Discord 真实 `/task`；
- [ ] `/status` `/tasks` `/cancel`（后续补测）；
- [ ] `/ask` 不创建 task（后续补测）；
- [ ] lease/重启/幂等；
- [ ] 工具 allowlist/安全；
- [ ] Tier2 降级；
- [x] 测试 Go（scope 限定）。

### WebConsole（Phase 1 Go）

- [x] Phase 1 只读控制台；
- [x] 3 Medium 回归（trusted proxy/path id/command args）；
- [x] Phase 2/3 仍明确 501；
- [ ] 真实保存/执行等 Host G5/G6 后另签；
- [x] Phase 1 测试 Go。

---

## 10. 一句话

**P5 `/task` 主链路与 WebConsole Phase 1 已 Go；`/status`/`/cancel` 补测、Host G5/G6 和 P4.5 运维收口仍待后续。**
