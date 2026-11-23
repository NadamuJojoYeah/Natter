# 2026-11-23 · C-03 脱敏实现收敛

> **执行**：qa（task-67）
> **基线**：`HEAD=3b80da0`（框架 1074 / WebConsole 375 / ConversationConsole 81 / PromptLibrary 36 / StateVitals 17 / AiEmbedding 11 全绿）
> **交付**：共享核心 `src/Natter.Application/Redaction/SecretRedaction.cs`、三处委托改造、共享夹具
> `tests/Natter.Framework.Tests/SecretRedactionConsistencyTests.cs`、WebConsole 委托断言 `tests/Natter.Plugin.WebConsole.Tests/RedactionDelegationTests.cs`

---

## 1. 收敛结果：1 个共享核心 + 3 个委托点

| 位置 | 之前 | 现在 |
| --- | --- | --- |
| `Natter.Application/Redaction/SecretRedaction.cs` | —（新增） | **共享核心**：PEM 块（含未闭合）、字面密钥（`sk-`/`ak-`/`Bearer`）、赋值对（key=value / "key": "value"）、URL 凭据、Bot 头、`${secret:NAME}` 保护、已知密钥字面擦除；**64 KiB 扫描上限 + 每正则 1 s 超时** |
| `Natter.Application/Admin/AdminRedactor.cs` | 76 行自持正则 | 3 行委托（`SecretRedactionOptions.Admin`），保留 `[redacted]` 与 `RedactAndTruncate` |
| `Natter.Chat.Discord/DiscordLogRedaction.cs` | 43 行自持 3 条正则，**无上限、无超时** | 委托（`SecretRedactionOptions.Discord`），保留 `***` 标记；**新增 64 KiB 上限 + 1 s 超时** |
| `Natter.Chat.Discord/DiscordSocketTransport.cs` | 已 `=> DiscordLogRedaction.Redact(value, _token.Value)` | 不变（原本就委托，无需再删） |
| `Natter.Plugin.WebConsole/WebConsoleConfig.cs`（`RedactSecrets`） | 自持 5 条正则 + 占位符保护，**无上限** | 委托（`SecretRedactionOptions.Admin`），保留 `[redacted]`；**新增 64 KiB 上限 + 1 s 超时** |

`SecretRedactionOptions` 把「替换文本」做成配置：Admin/WebConsole 用 `[redacted]`，Discord 用 `***`，两者都是既有可观测行为，未改动。

### 1.1 保留的特化规则

| 实现 | 保留的差异 |
| --- | --- |
| `AdminRedactor` | 标记 `[redacted]`；输出面向审计 JSON/命令结果；`RedactAndTruncate(min, max)` 保持 |
| `DiscordLogRedaction` | 标记 `***`；`https://***@`、`Authorization: Bot ***`、`key=***` 三种输出形态；可用 `KnownSecret`（bot token）做字面擦除 |
| `WebConsoleRedaction.RedactSecrets` | 标记 `[redacted]`；`${secret:NAME}` 原样保留；另有 JSON `Sanitize`（按 key 判定）与 `SanitizeText`（300 字截断），二者**不属于**逐行脱敏，保持原状 |

### 1.2 提升为共享规则的两条（原本只有一侧有）

| 规则 | 之前 | 现在 |
| --- | --- | --- |
| URL 内嵌凭据 | 只有 Discord 脱敏；admin/console 明文 | 共享默认启用（admin 输出也脱敏） |
| `Authorization: Bot <token>` | 只有 Discord 的 `Bot ***` 规则；admin 的赋值规则只吃掉单词 `Bot`、**留下 token** | 共享默认启用；Bot 规则保留头前缀（`Authorization: Bot <marker>`），赋值规则不再吞掉 `Bot`/`Bearer` 这类方案词 |

---

## 2. 共享夹具（本任务核心交付）

`SecretRedactionConsistencyTests`：12 组输入 × 4 个实现（AdminRedactor / DiscordLogRedaction / 核心 Admin profile / 核心 Discord profile）逐一断言「敏感子串不出现」，并断言各自标记与 `${secret:NAME}` 保留、超长输入被截断、恶意回溯输入不挂死。

输入矩阵覆盖：`DISCORD_TOKEN=…`、`token = "…"`、`Authorization: Bot …`、URL 凭据、完整 PEM、未闭合 PEM、`sk-live-…`、`client_secret=…`、`cookie: …`、`ConnectionString=…`、Unicode+制表符、换行分隔。

### 2.1 夹具发现的「原本会漏」的输入（最重要）

**A. 收敛前 Discord 实现（legacy 对照测试实测泄漏：夹具里保留了一份逐字复刻的旧实现）**

```text
legacy baseline leaked:
  quoted token        -> 'super-secret-value-42'      # 旧 value 字符类排除引号 → token = "…" 漏网
  full pem block      -> 'MIIEowIBAAKCAQEA'           # 旧实现没有 PEM 规则
  dangling pem block  -> 'AAAAB3NzaC1yc2E'            # 无 END 标记的 PEM
  literal sk key      -> 'sk-live-ABCDEFGHIJKLMNOP'   # 旧实现没有字面密钥规则
  cookie              -> 'abc123cookievalue'          # 旧 key 列表不含 cookie/credential/connection_string
```

**B. 收敛前的 Admin 实现（首轮夹具直接失败暴露）**

```text
AdminRedactor leaked 'MTIzNDU2Nzg5.abcdef.ghijkl' for 'authorization bot header'
  → "Authorization: [redacted] MTIzNDU2Nzg5.abcdef.ghijkl"   # 赋值规则只吃掉 "Bot"
AdminRedactor leaked 'p@ssw0rd-value' for 'url credentials'
  → "GET https://bot-user:p@ssw0rd-value@discord.com/api/v10/users failed"
```

**C. 双标记副作用（实现细节，已修）**

```text
Authorization: Bot *** ***   # Bot 规则先脱敏，随后赋值规则又把 "Bot" 当值替换一次
```

以上 7 类问题在收敛后全部被共享核心覆盖并由夹具锁定；legacy 对照测试断言「基线必须泄漏」，避免夹具退化成自我确认。

---

## 3. 回归

| 套件 | 结果 |
| --- | --- |
| Framework | **1097/1097**（基线 1074 + 17 共享夹具 + task-66/其他并行新增；连跑 2 次全绿） |
| WebConsole | **382/382**（基线 375 + 7 委托断言；既有输出格式未变） |
| ConversationConsole | **81/81** |
| PromptLibrary | **36/36** |
| StateVitals | **17/17** |
| AiEmbedding.OpenAi | **11/11** |

> `RedactionDelegationTests.cs`（7 用例）已验证：WebConsole 的 `RedactSecrets` 与共享核心逐输入一致、保留 `[redacted]` 与
> `${secret:NAME}`、超长输入同样被 64 KiB 截断。中途 task-66 的 `Chat/Context` 半成品曾让 `Natter.Application` 编译失败，
> 待其落定后重跑即为上表结果（未越界修改他人文件）。

---

## 4. 仍未覆盖的脱敏路径（建议后续）

| 路径 | 现状 | 建议 |
| --- | --- | --- |
| `Natter.Cli.Core/Commands/RuntimeHealth.Sanitize` | 自持一条 regex，**未收敛**（`src/Natter.Cli.Core` 不在本任务写作用域） | 改为调用 `SecretRedaction.Apply(..., Admin profile)`；它输出 `/health` 与 `--json`，与 admin 同样的泄漏面 |
| `Natter.Infrastructure/Admin/AdminLogBuffer` / `JsonlAdminAuditSink` | 依赖 `IAdminRedactor`（已收敛），但落盘前是否再走一遍需确认 | 若落盘前有独立字符串拼接，应同样过核心 |
| WebConsole `Sanitize`（JSON 按 key 判定） | 按 key 名脱敏（`***`/`[redacted]`），不走逐行规则 | 保持（按结构脱敏更准确），但与 `RedactSecrets` 的差异已在本文件说明 |
| 平台适配器（钉钉/Spark 等）日志 | 是否各自有过字符串拼接未统一 | 下一轮审查建议扫一遍 `LastError =` 赋值点 |
