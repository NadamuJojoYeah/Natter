# P2 Runtime & Profile Acceptance Test Plan

> **状态**：Ready for test  
> **日期**：2026-11-15  
> **基线**：`HEAD=f114f7e`（P2 实现已提交；测试资产已归档）  
> **被测对象**：`serve`、RuntimeProfile、provider 路由、SQLite 会话、health、Tier 1/Tier 2 策略  
> **开发规格**：[P2 Runtime & Profile Foundation](../P2_RUNTIME_PROFILE.md)  
> **测试目标**：验证 P2 是否达到“可长驻、可配置、可路由、可恢复”的最低产品标准；不是验证更多平台功能。

---

## 0. 结论口径

P2 通过需要同时满足：

1. Tier 1（Discord + DeepSeek）真实链路通过；
2. Tier 2（DingTalk Stream + Spark）至少 smoke 通过；
3. 两个 upstream 同时在线时，profile 路由正确、状态隔离；
4. 重启后 SQLite 会话恢复；
5. `serve` 能优雅停止；
6. 没有阻塞级缺陷、没有密钥泄漏。

> **P2 不通过，不进入 P3 Context/Memory。**

---

## 1. 范围

### 1.1 In scope

- `natter serve` 长驻模式；
- `--config-dir` / `--state-dir` / `--profile`；
- `profiles.json` 加载、校验、默认 profile；
- Discord → DeepSeek、DingTalk → Spark 的 profile 路由；
- 多 upstream 并发在线；
- SQLite 会话持久化、重启恢复；
- `natter health` / `GET /health`；
- Tier 1 启动失败、Tier 2 禁用的策略；
- 密钥脱敏与配置/状态目录边界。

### 1.2 Out of scope

- P3 Context/Memory 插件；
- Task Worker / 工具调用；
- 钉钉卡片/图片/语音；
- Spark streaming/tools；
- 多实例/集群；
- Android 动态插件；
- 第三方插件市场；
- 跨平台身份合并。

---

## 2. 测试环境

| 项 | 要求 |
| --- | --- |
| OS | Linux 优先；如需 Windows/macOS 另记 |
| .NET | SDK 10.0.114+ |
| CLI | `src/Natter.Cli/bin/Debug/net10.0/natter` |
| 配置目录 | `artifacts/p2/<run>/config`（临时） |
| 状态目录 | `artifacts/p2/<run>/state`（临时） |
| 插件目录 | `artifacts/p2/<run>/plugins`（临时 staging） |
| 凭据 | `~/.config/natter/live.env`（600，仓库外） |
| 证据 | `artifacts/p2/<run>/evidence/`（日志/JSON/截图） |

凭据文件只允许包含：

```text
DEEPSEEK_API_KEY
DISCORD_BOT_TOKEN
DISCORD_BOT_USER_ID
DISCORD_TEST_GUILD_ID
DISCORD_TEST_CHANNEL_ID
DINGTALK_CLIENT_ID
DINGTALK_CLIENT_SECRET
DINGTALK_BOT_ID
SPARK_API_KEY
```

禁止把密钥写进 `profiles.json`、插件 `config.json`、测试记录或截图。

---

## 3. 测试前准备

### 3.1 构建与回归

```bash
dotnet build Natter.slnx
dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj
```

预期：

- build 成功（允许既有 Android/Java warning）；
- 框架测试 **203/203**（或当前 HEAD 的等价全绿）。

### 3.2 无凭据 skip

```bash
env -u NATTER_LIVE \
    -u DEEPSEEK_API_KEY \
    -u DISCORD_BOT_TOKEN \
    -u SPARK_API_KEY \
    -u DINGTALK_CLIENT_SECRET \
  dotnet test tests/Natter.Live.Tests/Natter.Live.Tests.csproj
```

预期：

- 全部 skip；
- 0 failed；
- 不因缺少凭据崩溃。

### 3.3 插件包

确认四个插件包存在：

```text
samples/Natter.Sample.Plugin.Discord/bin/Debug/net10.0
src/Natter.Feature.Ai.DeepSeek/bin/Debug/net10.0
samples/Natter.Sample.Plugin.DingTalk/bin/Debug/net10.0
src/Natter.Feature.Ai.Spark/bin/Debug/net10.0
```

```bash
natter --no-plugins plugins inspect <dir> --json
```

预期：

- `isValid=true`；
- DingTalk/Spark 的 manifest/config 与当前实现一致；
- 目录名 warning 不算失败，但 staging 时必须复制到 `<pluginId>/` 目录。

### 3.4 配置目录

`config/profiles.json` 示例：

```json
{
  "schemaVersion": 1,
  "defaultProfile": "tier1-default",
  "profiles": [
    {
      "id": "tier1-default",
      "supportTier": 1,
      "match": { "protocols": ["discord"], "platforms": ["discord"] },
      "ai": { "provider": "natter.feature.ai.deepseek#deepseek" },
      "history": { "scope": "conversation", "maxMessages": 20, "maxCharacters": 24000 }
    },
    {
      "id": "tier2-dingtalk-spark",
      "supportTier": 2,
      "match": { "protocols": ["dingtalk"], "platforms": ["dingtalk"] },
      "ai": { "provider": "natter.feature.ai.spark#spark" },
      "history": { "scope": "conversation", "maxMessages": 20, "maxCharacters": 24000 }
    }
  ]
}
```

插件配置放在：

```text
config/natter.transport.discord.json
config/natter.feature.ai.deepseek.json
config/natter.transport.dingtalk.json
config/natter.feature.ai.spark.json
```

只允许 secret 引用：

```json
{ "token": "${secret:DISCORD_BOT_TOKEN}" }
```

### 3.5 环境变量

运行 CLI 前需要把 `live.env` 导入进程环境（live 测试脚本会自动处理，CLI 不会自动 source）：

```bash
set -a
. ~/.config/natter/live.env
set +a
```

---

## 4. 测试用例

### Gate A：CLI / Host 基础

| ID | 用例 | 命令 | 预期 |
| --- | --- | --- | --- |
| P2-001 | 构建 | `dotnet build Natter.slnx` | 成功 |
| P2-002 | 框架回归 | `dotnet test tests/Natter.Framework.Tests` | 全绿 |
| P2-003 | 无凭据 live | 见 §3.2 | 全 skip，0 failed |
| P2-004 | `serve` 短跑 | `natter --no-plugins serve --duration 3 --json` | exit 0；输出 `mode=serve`、profiles |
| P2-005 | `health` | `natter --no-plugins health --json` | `status` 合理；profiles/plugins 有值 |
| P2-006 | health endpoint | `natter --no-plugins serve --duration 5 --health-port 5099` + `curl 127.0.0.1:5099/health` | 200，JSON 可解析 |
| P2-007 | 启动失败 | `serve` + 不存在的 profile | 非 0，错误可读 |
| P2-008 | 优雅停止 | `timeout -s INT 15s natter --no-plugins serve` | 10 秒内退出，无残留 |

### Gate B：Profile / 路由

| ID | 用例 | 方法 | 预期 |
| --- | --- | --- | --- |
| P2-010 | 默认 profile | 无 `profiles.json`，启动 | 使用 `tier1-default` |
| P2-011 | Discord 路由 | profiles 中 Discord→DeepSeek，DingTalk→Spark | Discord 消息走 DeepSeek |
| P2-012 | DingTalk 路由 | 同上 | DingTalk 消息走 Spark |
| P2-013 | `--profile` 强制覆盖 | profiles 同时有匹配项，`--profile tier2-dingtalk-spark` | **指定 profile 必须强制生效**，不能被 match 覆盖 |
| P2-014 | Tier 1 provider 缺失 | profiles 引用不存在的 Tier 1 provider | 启动失败，非 0 |
| P2-015 | Tier 2 provider 缺失 | 只加载 Discord+DeepSeek，profiles 含 DingTalk→Spark | Tier 2 profile 禁用/不路由；Tier 1 正常 |
| P2-016 | 未知 profile | `--profile missing` | 启动失败 |
| P2-017 | 重复 profile id | profiles 两个同 id | 启动失败 |
| P2-018 | 非法 provider ref | `provider="deepseek"` 没有 `#` | 启动失败 |

> P2-013 是重点：当前实现中 override 可能只在“没有 match”时生效；如果实测不强制覆盖，记为 **BUG-P2-OVERRIDE**。

### Gate C：SQLite 会话

| ID | 用例 | 方法 | 预期 |
| --- | --- | --- | --- |
| P2-020 | 建库 | `serve` 启动 | `<state>/natter.db` 创建 |
| P2-021 | 写入 | 真实或 fake 回复一轮 | `sessions`/`messages` 有记录 |
| P2-022 | 重启恢复 | 停止 → 重启 → 查询 | 历史仍在 |
| P2-023 | source 隔离 | Discord 与 DingTalk 各一轮 | `source_key` 不同，消息不串 |
| P2-024 | history scope | profile `scope=user` | 不同 actor key 分开 |
| P2-025 | 重复 event id | 重放同一事件 | 不重复写 user/assistant |
| P2-026 | provider 切换 | 同一 source 切换 provider | 历史仍共享，不丢 |
| P2-027 | 清理 | `ClearAsync` 或等价命令 | session/messages 删除 |

可用 `sqlite3` 取证：

```bash
sqlite3 artifacts/p2/<run>/state/natter.db \
  "select source_key, conversation_id, actor_id, version from sessions;"
sqlite3 artifacts/p2/<run>/state/natter.db \
  "select session_id, role, substr(text,1,40) from messages order by message_id desc limit 10;"
```

### Gate D：Tier 1 真实链路

只加载 Discord + DeepSeek：

```bash
natter \
  --plugin-dir artifacts/p2/<run>/plugins \
  --config-dir artifacts/p2/<run>/config \
  --state-dir artifacts/p2/<run>/state \
  serve --duration 300 --health-port 5099
```

| ID | 用例 | 预期 |
| --- | --- | --- |
| P2-030 | 插件加载 | Discord/DeepSeek `Activated` |
| P2-031 | upstream 连接 | `health` 显示 Discord `connected=true` |
| P2-032 | DM 回复 | 真实 DM 收到 DeepSeek 回复 |
| P2-033 | guild @mention | 频道 @bot 收到回复 |
| P2-034 | 重启历史 | 停止/重启后，同一会话继续 |
| P2-035 | 无 Tier 2 | 未装 DingTalk/Spark 不影响启动 |
| P2-036 | 日志 | 有 profileId/providerRef/eventId；无密钥/正文泄漏 |

### Gate E：Tier 2 真实链路

加载四个插件：

```bash
natter \
  --plugin-dir artifacts/p2/<run>/plugins \
  --config-dir artifacts/p2/<run>/config \
  --state-dir artifacts/p2/<run>/state \
  serve --duration 300 --health-port 5099
```

| ID | 用例 | 预期 |
| --- | --- | --- |
| P2-040 | DingTalk Stream 连接 | `connected=true` |
| P2-041 | 钉钉群 @bot | Spark 回复，无重复 |
| P2-042 | 插件加载 | 四个插件均 `Activated` 或 Tier 2 被明确禁用 |
| P2-043 | 能力降级 | 钉钉无 edit/delete/typing/slash；文本回复正常 |
| P2-044 | health | providers 列出 DeepSeek/Spark 可用性 |

### Gate F：并发 / 隔离 / 故障

| ID | 用例 | 方法 | 预期 |
| --- | --- | --- | --- |
| P2-050 | 双 upstream 在线 | Discord + DingTalk 同时连接 | 两个 `connected=true` |
| P2-051 | 并发消息 | 两边同时发 | Discord→DeepSeek，DingTalk→Spark，不串 |
| P2-052 | provider 故障 | 临时使用错误 Spark key/不可达 | DingTalk 降级/失败，不影响 Discord |
| P2-053 | upstream 断线 | 停 DingTalk/断网 | Discord 继续；DingTalk 状态 degraded |
| P2-054 | SQLite 重启 | 双 source 写入后重启 | 两边历史各自恢复 |
| P2-055 | 重复投递 | 重放同一钉钉 msgId | 只回复一次 |
| P2-056 | 长跑 | 30–60 分钟 | 无重复回复、无内存/连接失控 |

### Gate G：安全

| ID | 用例 | 预期 |
| --- | --- | --- |
| P2-060 | 密钥不落日志 | `--verbose` 日志不含 token/key/secret |
| P2-061 | profiles 无明文 | `profiles.json` 只有 provider/ref，不含密钥 |
| P2-062 | 插件 config | 只含 `${secret:...}` |
| P2-063 | state 权限 | state 目录不可被其他用户读取（按部署要求） |
| P2-064 | 错误脱敏 | 401/429/5xx 错误不含 key/prompt 正文 |

---

## 5. 重点风险清单

测试时必须重点确认：

1. **`--profile` 是否强制覆盖**  
   当前实现可能只在“无 match”时使用 override；产品语义应是强制指定。

2. **Tier 2 provider 缺失时的行为**  
   是禁用、返回 null，还是 fallback 到 DeepSeek？必须是明确策略，不能悄悄走 Tier 1。

3. **`ReloadAsync` 是否保留 override**  
   如果支持热重载，重新加载 profiles 后 `--profile` 不能丢失。

4. **health 的 “healthy” 语义**  
   没有 upstream 时 `All(...)` 可能为空真；记录实际输出，不要只看字符串。

5. **SQLite 长跑**  
   单连接/事务/WAL 是否稳定；并发写入是否出现 `database is locked`。

6. **Tier 1/Tier 2 配置隔离**  
   加载全部四个插件时，不能出现 DingTalk 被 DeepSeek 回答、Discord 被 Spark 回答。

---

## 6. 缺陷分级

| 级别 | 定义 | 例子 |
| --- | --- | --- |
| Blocker | Tier 1 无法启动/无法回复/数据损坏 | Discord+DeepSeek 起不来、SQLite 丢历史 |
| High | 路由错误、跨 source 泄漏、Tier 策略错误 | DingTalk 被 DeepSeek 回答、profile 覆盖失效 |
| Medium | health/诊断/错误文案问题 | health 字段错误、日志缺 profileId |
| Low | 文档/格式/非阻塞 warning | 目录名 warning |

Tier 2 的 Blocker 可以降为 High/Medium，但不能静默忽略；需记录 issue 并明确是否阻塞 P2 签收。

---

## 7. 证据要求

每个 Gate 至少保留：

- 命令与退出码；
- `health --json`；
- `serve` 启动日志（脱敏）；
- 真实平台截图（Discord/钉钉，群名可打码）；
- SQLite 查询结果；
- 失败时的完整错误（脱敏）；
- `git rev-parse HEAD`。

证据目录：

```text
artifacts/p2/<YYYY-MM-DD-HHmm>/
├── config/
├── state/
├── plugins/
├── evidence/
│   ├── build.log
│   ├── health.json
│   ├── serve.log
│   ├── sqlite.txt
│   └── screenshots/
```

不要把 `state/`、`plugins/`、`.env` 提交到仓库。

---

## 8. 签收标准

- [ ] P2-001 ~ P2-008 全绿；
- [ ] P2-010 ~ P2-018 全绿；
- [ ] P2-020 ~ P2-027 全绿；
- [ ] P2-030 ~ P2-036 Tier 1 真实通过；
- [ ] P2-040 ~ P2-044 Tier 2 真实 smoke 通过；
- [ ] P2-050 ~ P2-056 并发/隔离通过；
- [ ] P2-060 ~ P2-064 安全通过；
- [ ] 无 Blocker；
- [ ] 无跨 source 路由/记忆泄漏；
- [ ] 重启可恢复；
- [ ] 无密钥进入仓库/日志/截图；
- [ ] 测试记录归档到 `docs/testing/`；
- [ ] 测试负责人给出 Go/No-Go。

---

## 9. 测试记录模板

新增：

```text
docs/testing/YYYY-MM-DD-p2-acceptance.md
```

至少包含：

- 基线 SHA；
- 环境；
- 每个 Gate 的命令/结果；
- 失败用例的 BUG 编号与证据；
- 是否满足签收标准；
- 临时改动（如有）与回退；
- 最终 Go/No-Go。

---

## 10. 结论

P2 的验收重点不是“多接一个平台”，而是：

> **一个进程同时跑多个 upstream，profile 决定 provider，状态可恢复，Tier 1 不被 Tier 2 拖累。**

先按 Gate A→G 顺序执行；Gate D 通过前，不进入 Tier 2；Gate F 通过前，不签收 P2。
