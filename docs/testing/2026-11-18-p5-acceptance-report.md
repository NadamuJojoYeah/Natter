# 2026-11-18 · P5 Agent Task Runtime 验收（测试侧，执行中）

> **开发侧处置**：[P5 + WebConsole 联合处置记录](2026-11-18-p5-webconsole-response.md)（P5 No-Go；schema 修复后复测）。

> **状态**：自动化通过；真实 slash/工具细节待补  
> **基线**：`HEAD=2a13b8b`  
> **证据**：`artifacts/p5/2026-11-18-1200/`（`build.log`、`framework.log`、`live-skip.log`、`task-tests.log`）

## 1. Gate A（前置）

| 项 | 结果 |
| --- | --- |
| build | **通过** |
| 框架回归 | **254/254** |
| 无凭据 live | **40 skip / 0 failed** |
| WebConsole 回归 | **123/123** |

## 2. 组件级（TaskRuntimeTests）

**8/8 通过**，覆盖：

- 分发/运行/只发送一次结果（P5-020 / P5-030 / P5-055 部分）；
- cancel before terminal（P5-022）；
- lease + 乐观锁（P5-025 / P5-026）；
- recovery：Pending 重排、Running 过期（P5-028 / P5-029）；
- SQLite task store 持久化（P5-060 / P5-061）；
- AI worker 走 host responder、不直接发送（P5-050 / P5-055）；
- HTTP 工具 allowlist/网络策略（P5-042 / P5-043 / P5-047）；
- clock 工具只读（P5-040）。

## 2.0 工具契约（新增补充测试）

`ToolContractTests` **7/7 通过**：

| 用例 | 结果 |
| --- | --- |
| P5-041 text-transform | `HELLO (chars=5)` |
| P5-043 host allowlist | `host_not_allowed` |
| P5-044 非 HTTPS | `invalid_url` |
| P5-045 非 2xx | `http_error` + `HTTP 404` |
| P5-046 取消/超时 | `OperationCanceledException` |
| P5-048 shell/file write | `ReadOnlyToolPolicy` 拒绝（Shell/FileWrite/Network 未开均拒绝） |
| P5-049 输出上限 | 200KB body → 输出 ≤1024 字节 |

## 2.1 真实宿主（P5 实例）

| 项 | 结果 |
| --- | --- |
| 启动 | 通过：`healthy`、7 plugins、双 upstream connected（代理） |
| health task 字段（P5-063） | **通过**：`tasks={created:0,completed:0,failed:0,cancelled:0,active:0}` |
| state 权限（P5-062） | **通过**：state 700、`natter.db` 600 |

## 2.2 BUG-P5-SCHEMA-COMMANDS（High，阻塞真实 slash）

配置 `task/status/tasks/cancel` 后 Discord 插件启动失败：

```text
plugin failed natter.transport.discord phase=config code=invalid-config:
/commands/task: Additional properties are not allowed.
/commands/status: ...
/commands/tasks: ...
/commands/cancel: ...
```

`config.schema.json` 的 `commands.properties` 仍只有 5 个命令（ask/remember/forget/memory/reset），P5 新增的 4 个命令未加入 schema → 无法配置/注册。
这与 P4.5 前的 `BUG-P4-SCHEMA-PATTERN` 属同类问题：**schema 未随命令清单更新**。

影响：P5-010~018、P5-080~084 全部阻塞（无法真实发 `/task`）。
证据：`artifacts/p5/2026-11-18-1200/evidence/serve.log`。
建议：schema `commands.properties` 增加 `task/status/tasks/cancel`，并补一条「P5 命令配置通过校验」测试。

## 3. 未测（需真实宿主/人工）

| Gate | 缺口 |
| --- | --- |
| B 命令 | P5-010 `/task`、P5-011 空 prompt、P5-012 关闭 runtime、P5-013 `/status`、P5-014 `/tasks`、P5-015 `/cancel`、P5-016 非创建者、P5-017 未知、P5-018 `/ask` 回归 |
| C 生命周期 | P5-021 worker 失败、P5-023 cancel after terminal、P5-024 超时、P5-027 lease 过期 watchdog 实跑 |
| D 工具 | P5-041 text-transform、P5-044 非 HTTPS、P5-045 非 2xx、P5-046 超时、P5-048 shell/file 拒绝、P5-049 输出上限 |
| E fallback | P5-051 AI null、P5-052 AI 异常、P5-053 无工具、P5-054 context 隔离 |
| F health | P5-062 state 权限（可查）、P5-063 metrics、P5-064 active 递减、P5-065 结果幂等（重启重放）、P5-066 事件 |
| G 安全 | P5-070~076（跨 source、prompt 注入、私有 IP、shell 注入、日志、截断） |
| H 真实 | P5-080~084（Discord `/task`、cancel、重启、`/ask`、钉钉 smoke） |

## 4. 结论（暂定）

- 组件层与前置通过；不出具 Go/No-Go。
- 需要：Discord 真实 `/task` 链路被 `BUG-P5-SCHEMA-COMMANDS`（High）阻塞；修复 schema 后重测。
- 组件/health/权限通过；工具细节与安全用例仍待补。
- 真实验收建议在重启会话清理残留后进行。
