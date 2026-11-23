# 2026-11-23 · 脱敏收尾评估（task-67 后续：#2 / #3 + 引用方向）

> **性质**：评估，**未改代码**（除 task-67 已提交内容）
> **基线**：`HEAD=01ce1d5`
> **范围**：`AdminLogBuffer` / `JsonlAdminAuditSink` 落盘脱敏；钉钉/Spark/Satori 的 `LastError` 拼接；`Chat.Discord → Application` 引用方向

---

## 结论速览

| 项 | 结论 | 风险 |
| --- | --- | --- |
| **#2 日志/审计落盘** | **不需要改**（现有实现是「写前脱敏」，不依赖上游） | 残留：`AdminLogBuffer.Add` 是 public，未来可能被绕过 |
| **#3 平台 `LastError` 拼接** | **建议改**（5 处 1 行改动；Satori/DingTalk/Spark 有真实泄漏面，Discord 两处已安全） | 低（纯字符串，不动错误码/健康判定） |
| **引用方向 `Chat.Discord → Application`** | **与 B-02 不是同一类**：实现层→核心层，方向正确、无环；B-02 是契约层（插件 ABI 面）反向依赖实现层 | 无（可选优化：把核心移到 `Natter.Abstractions` 撤掉这条引用） |

---

## #2 AdminLogBuffer / JsonlAdminAuditSink：不需要改

### 判定依据（谁在写之前脱敏）

| 落盘点 | 调用链 | 脱敏发生在哪里 |
| --- | --- | --- |
| `AdminLogBuffer.Add` | 全仓唯一写入方是 `AdminLogBufferLoggerProvider.Log`（`AdminLogBuffer.cs:72` 的 `Add` 只被 `AdminLogBufferLoggerProvider.cs:64` 调用） | **写前**：`_redactor.Redact(message)`，且异常文本在同一表达式里拼接后一起脱敏（`message + " \| " + ex.GetType().Name + ": " + ex.Message`） |
| `JsonlAdminAuditSink` | `RecordAsync` → `Serialize` → `File.AppendAllTextAsync` | **写前**：`Serialize` 对 `Actor/Target/Result/Code/Detail` 逐个 `_redactor.Redact(...)`，`Detail` 再截到 2048 |

`AdminLogBuffer` 自身只做控制字符清理与长度截断（`Sanitize`/`Truncate`），**不脱敏**；但这不影响判定，因为它不是入口——所有进入 buffer 的文本已经过 `IAdminRedactor`，而该实现（C-03 后）委托共享核心（PEM/字面/key=value/URL 凭据/Bot 头/上限/超时）。

### 什么情况下会漏（为什么仍值得留一条注释/收窄）

- `AdminLogBuffer.Add` 是 **public**，且 buffer 是 DI 单例；任何新调用者（例如某个插件或未来的 `ILogger` 旁路）直接 `Add` 就会把明文写进内存环形缓冲，随后被 `/api/logs` 原样返回。当前**没有**这样的调用者。
- `HostAdminLogReader` 只读不写，且读时不再脱敏 → 一旦上游绕过，泄漏直达 WebConsole。

### 建议（可选，1 行、零行为变化）

```text
AdminLogBuffer.Add  → internal（同时保留 XML 注释：调用方必须已脱敏）
```

不推荐在 `Add` 里再脱敏一次：那是热路径（每条日志），且会与 provider 的脱敏叠加成双份正则开销；「入口唯一 + 写前脱敏」已是正确的结构。

---

## #3 钉钉 / Spark / Satori 的 `LastError`：建议改

### 关键事实：下游兜底很弱，且 Spark 完全没有兜底

```text
平台 upstream.LastError（原始错误文本）
  ├─→ RuntimeHealth.Snapshot：Sanitize(仅一条规则)  → /health · --json
  │      regex = (sk-…|ak-…|Bearer …)  ← 不含 key=value / URL 凭据 / PEM / Bot 头
  ├─→ WebConsole：读 /health JSON 的 lastError/lastSendError/lastDisconnectError 直接展示
  └─→ Spark 另有一条完全独立、无脱敏的输出：SparkPlugin.cs:172 `lastError: {snapshot.Metrics.LastError}`
```

`RuntimeHealth.Sanitize` 属于 engineer 正在处理的 #1；即使 #1 换成共享核心，`LastError` 在**写入内存的那一刻**仍是明文，会被其他消费者（Spark CLI、任何新增的 health/诊断消费者）绕开。因此赋值点脱敏是纵深防御，而不是重复劳动。

### 逐点清单

| 位置 | 拼的是什么 | 现状 | 泄漏情形 | 建议 |
| --- | --- | --- | --- | --- |
| `src/Natter.Chat.Satori/SatoriChatUpstream.cs:298` | 映射异常 `exception.Message` | **未脱敏** | 反序列化/映射异常常带原始帧片段；若含 URL/查询参数则整段进 `LastError` | `SecretRedaction.Apply(ex.Message, Discord 风格 profile)` |
| `src/Natter.Chat.Satori/SatoriChatUpstream.cs:313` | WebSocket `OnError(error.Message)` | **未脱敏** | **最高风险**：Satori ws 地址通常是 `wss://host/v1/events?token=…`，连接异常消息会带完整 URL → 现规则不覆盖 `token=` | 同上（该 profile 需启用 URL 凭据 + key=value） |
| `src/Natter.Chat.DingTalk/DingTalkChatUpstream.cs:213` | `transportEvent.ErrorMessage` | **未脱敏**，且同时进 `PublishStatus(Error, 原文)` 的状态 detail | stream/webhook 连接错误可含 webhook URL（`access_token=`）或网关地址 | 脱敏后同时用于 `LastError` 与 status detail |
| `src/Natter.Feature.Ai.Spark/SparkMetrics.cs:37` | `$"{kind}: {message}"`（来自 `SparkChatClient.cs:78` 的 `ex.Message`） | **未脱敏** | provider 错误体/请求 id/回显片段可能进 message；`SparkPlugin.cs:172` 的 `lastError:` 输出**无任何下游兜底** | 在 `RecordError` 内脱敏（单点，所有 Spark 错误都过这里） |
| `src/Natter.Chat.Discord/DiscordChatUpstream.cs:981,1060` | `SetError("mapping_failed", ex.Message)` | **未脱敏** | 映射异常文本；`SetError` 内部 `LastError = $"{code}: {message}"` | `DiscordLogRedaction.Redact(ex.Message)` 后再传入 |
| `DiscordChatUpstream.cs:191` | `Redact(ex.Message)` | 已脱敏 | — | 不动 |
| `DiscordChatUpstream.cs:549-551/557/574` | `incomingReason`（已 `DiscordLogRedaction.Redact`） | **已脱敏** | — | 不动 |
| `DiscordChatUpstream.cs:597` | `self_id_mismatch` 里的 `SelfId` | 非密钥 | — | 不动 |

### 改动风险

- **低**：全部是"把 `x` 换成 `Apply(x)`"，不改变错误码、不改变 health 判定（`degraded` 只看连接状态/插件失败，不看文本）、不改变日志字段名。
- 需要同步的既有断言：若某测试断言 `LastError` 原文（含 token/URL），会变红——这类断言本身就应该改成"断言已脱敏"。
- 归属建议：与 #1（`RuntimeHealth.Sanitize` → 共享核心）**同一批**交给 engineer；三者（RuntimeHealth / 平台 LastError / Spark 输出）都在「诊断文本离开进程前」这条线上，拆开容易再漏一侧（正是 S2 的 `OBS-WC-P3-REDACT-KV` 教训）。

---

## 引用方向复核：`Chat.Discord → Application` 与 B-02 不同类

### 现状

```text
Natter.Application      → Natter.Domain, Natter.Chat, Natter.Abstractions
Natter.Chat.Discord     → Natter.Chat, Natter.Abstractions, Natter.Application   ← task-67 新增
Natter.Plugin.Abstractions → Natter.Abstractions, Natter.Chat, Natter.Application, Natter.Cli.Abstractions
```

### 为什么不是同一类问题

| 维度 | B-02（`Plugin.Abstractions → Application`） | 本次（`Chat.Discord → Application`） |
| --- | --- | --- |
| 被依赖方的角色 | `Plugin.Abstractions` 是**对外插件 ABI/契约层** | `Chat.Discord` 是 `src/` 下的**适配器实现**，不被任何插件/外部程序集引用 |
| 依赖方向 | **契约 → 实现**（反向），把 `Application`（含 `MemoryScoring`、`PromptComposer`）拖进每个插件的引用面 | **实现 → 核心**（正向），与 `Natter.Infrastructure → Application` 同向 |
| ALC / 插件加载影响 | 每个插件程序集都会间接加载 `Application`，共享面变宽、ABI 一旦发布难改 | 插件 ALC 不变（Discord 适配器由宿主加载，本来就在同一宿主进程） |
| 是否可替换 | 需搬契约（动 ABI，高风险） | 只需把 `SecretRedaction` 换个落点（1 文件 + 1 行 csproj），零 ABI 影响 |
| 无环性 | 无环 | 无环（`Application` 不引用 `Chat.Discord`，grep + csproj 均确认） |

### 可选的更干净落点（不影响本轮结论）

`Chat.Discord` 只是为了一个 `SecretRedaction` 类而依赖整个 `Natter.Application`。若把该文件移到 **`Natter.Abstractions`**（Discord 与 Plugin.Abstractions 都已引用它），就能撤掉这条新引用：

```text
移动 src/Natter.Application/Redaction/SecretRedaction.cs → src/Natter.Abstractions/Redaction/
撤掉 Chat.Discord.csproj 里新加的 Natter.Application 引用
Admin/WebConsole 侧改 using（它们经 Plugin.Abstractions/Application 仍可见）
```

代价：一次文件搬迁 + 3 处 using；好处：`Chat.Discord` 恢复"只依赖 Chat/Abstractions"的窄引用面，也顺带减少 B-02 未来的搬迁对象。**建议排进 B-02 那一批**（同一类"契约/工具落错层"的整理），不单独做。
