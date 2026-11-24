# Natter Plugin Specification v1（NPS v1）

> **状态**：Draft · Phase 1、Discord transport、DeepSeek AI responder 与第二上游/消费者（钉钉 + 星火）核心已实现（manifest / Host API / contribution / 依赖图 / 配置 / 秘密 / 权限 / 激活回滚 / 诊断 / 消息与 interaction 映射 / IChatClient responder / 多 transport 验证）  
> **日期**：2026-11-15  
> **目标**：在接入 Discord 等具体上游之前，先固定 Natter 的插件包、manifest、宿主 API、contribution、依赖图、配置、权限、生命周期和诊断约定。  
> **参考对象**：DSH 的插件图 / 配置 schema / 版本约束 / HMR 与插件管理；RimWorld 的 packageId / 依赖声明 / `loadAfter` / `loadBefore` / 版本兼容。  
> **相关文档**：[NLMP 轻量消息协议](NLMP.md)、[当前插件开发指南](PLUGINS.md)、[架构设计](ARCHITECTURE.md)
>
> **WebConsole 插件**：设计见 [WebConsole Plugin Design](WEBCONSOLE_PLUGIN_DESIGN.md)。
>
> **实现说明**：Phase 1 已在 `Natter.Plugin.Abstractions` + `Natter.Infrastructure/Plugins/Nps` 中交付，示例插件已迁移到 NPS v1；Discord transport Phase 2 核心已交付；AI feature、插件市场与 out-of-process 宿主仍属路线图，见 §16 与 §18。

---

## 0. 摘要

Natter 的插件不是“往 DI 容器里塞任意服务”的类库，而是一个有身份、有版本、有依赖、有配置、有权限、有生命周期的**可组合单元**。

NPS v1 固定七个核心：

1. **Manifest 是插件的唯一事实来源**；
2. **Contribution 是类型化扩展点，裸 DI 不是**；
3. **依赖图决定加载顺序，失败可隔离**；
4. **Host API 版本决定兼容性**；
5. **配置与秘密分离，秘密不落 manifest**；
6. **权限是显式声明和宿主授予的**；
7. **激活是事务，失败可回滚**。

NPS v1 明确不解决：

- 热更新 / HMR；
- 插件市场；
- 进程内安全沙箱；
- 跨语言插件；
- 游戏式 XML/Defs Patch。

这些是后续版本的能力，不应阻塞 v1。

---

## 1. 术语与角色

| 术语 | 定义 |
| --- | --- |
| Host | 运行 Natter 的进程，负责发现、加载、组合、启动和停止插件 |
| Plugin | 一个带 manifest 的独立包，至少包含一个入口程序集和可选资源 |
| Transport plugin | 接入聊天平台的上游插件，例如 Discord、Satori、Slack |
| Feature plugin | 与平台无关的功能插件，例如 AI responder、记忆、工具 |
| Contribution | 插件向宿主声明的一个扩展点实例，如 `chat.upstream` |
| Host API | 宿主暴露给插件的稳定接口集合，有独立 semver |
| Plugin-local services | 插件自己的 DI 服务，不等于宿主根容器 |
| Activation | 插件从配置完成到贡献可用、资源启动的过程 |
| Legacy | 当前 `IPluginModule` / `plugin.json` 形态，NPS v1 兼容但逐步迁移 |

---

## 2. 设计原则

1. **Manifest first**：插件元数据以 manifest 为准，代码只负责实现；不从程序集反射猜 id/version/依赖。
2. **Typed contributions**：插件不能直接拿宿主根 `IServiceCollection` 或 `IServiceProvider`；只能声明类型化贡献。
3. **Deterministic graph**：依赖、顺序、冲突、循环必须可计算、可诊断、可重复。
4. **Stable host API**：host API 独立版本；破坏性变更走 major；插件声明兼容范围。
5. **Config + secrets**：config 可校验、可展示、可版本化；secret 只引用名字，不进入 manifest 或日志。
6. **Transactional activation**：先 staging，全部校验通过再 commit；失败回滚并隔离。
7. **Least privilege**：插件声明权限；宿主授予；不把“信任”建立在插件自觉上。
8. **Observable**：加载图、贡献、配置、权限、激活错误都能用 CLI 查看。
9. **Restart-first**：v1 只保证重启生效；不承诺热重载。
10. **Small API**：宁可少一个扩展点，也不要让插件访问宿主内部。

---

## 3. 插件包布局

```text
plugins/
└── natter.transport.messaging.discord/
    ├── plugin.json                 # NPS v1 manifest
    ├── config.schema.json          # JSON Schema（可选但推荐）
    ├── config.json                 # 用户配置（宿主/用户维护，可不存在）
    ├── lib/
    │   ├── Natter.Plugin.Discord.dll
    │   ├── Natter.Plugin.Discord.deps.json
    │   └── ...私有依赖
    ├── README.md
    └── LICENSE
```

规则：

- `plugin.json` MUST 位于插件目录根部。
- `entry.assembly` MUST 是插件目录内的相对路径；v1 推荐放在 `lib/`。
- 插件私有依赖 MUST 放在插件目录内；框架共享程序集由宿主提供。
- 入口程序集放在 `lib/` 时 SHOULD 同时包含 `<AssemblyName>.deps.json`，让 `AssemblyDependencyResolver` 看到依赖图；无 deps.json 的目录加载仅作为兼容兜底并产生诊断。
- 共享程序集按显式契约白名单（`PluginContractAssemblies`）判断；不要按 `Natter.*` / `Microsoft.Extensions.*` 前缀共享 adapter/provider。
- 插件目录 MUST NOT 包含 token、keystore、私钥或用户隐私数据。
- `config.json` SHOULD 由宿主或用户维护，插件不应在运行时回写 manifest。
- 插件目录名 SHOULD 与 manifest `id` 一致；不一致不阻断加载，但会产生诊断 warning。

---

## 4. Manifest

> 机器可读 JSON Schema：[plugin.schema.json](plugin.schema.json)。运行时实际校验见 `src/Natter.Plugin.Abstractions/Manifest/PluginManifestReader.cs`。

### 4.1 顶层字段

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `schemaVersion` | integer | 是 | 固定为 `1` |
| `id` | string | 是 | 全局唯一插件 ID，建议反向 DNS 或 `natter.*` |
| `name` | string | 是 | 显示名 |
| `version` | string | 是 | 插件版本，SemVer 2.0 |
| `kind` | enum | 是 | `transport` / `feature` / `tool` |
| `hostApi` | string | 是 | 宿主 API 兼容范围，如 `>=0.1.0 <1.0.0` |
| `entry` | object | 是 | 入口程序集与类型 |
| `description` | string? | 否 | 一句话说明 |
| `author` | string? | 否 | 作者/组织 |
| `license` | string? | 否 | SPDX 标识 |
| `homepage` | string? | 否 | 项目主页 |
| `dependsOn` | array | 否 | 必需插件依赖 |
| `optionalDependsOn` | array | 否 | 可选插件依赖 |
| `loadAfter` | string[] | 否 | 顺序约束，不表达必需依赖 |
| `loadBefore` | string[] | 否 | 顺序约束 |
| `conflicts` | array | 否 | 冲突插件 ID/版本范围 |
| `permissions` | string[] | 否 | 申请的宿主能力 |
| `secrets` | array | 否 | 需要的秘密名称 |
| `configSchema` | string? | 否 | 相对插件目录的 JSON Schema 路径 |
| `contributions` | array | 否 | 声明贡献点 |
| `platforms` | string[] | 否 | `windows` / `linux` / `macos` / `android` |
| `capabilities` | string[] | 否 | 兼容旧 manifest 的展示标签，不是权限 |

启用/禁用状态由宿主 profile 维护，MUST NOT 写入 manifest。legacy `plugin.json` 中的 `enabled` 只作为兼容读取，NPS v1 插件不应依赖它。

`id` 规则：

- MUST 匹配 `^[a-z0-9]+(?:[._-][a-z0-9]+)+$`；
- MUST 全局唯一；
- 建议使用 `natter.transport.messaging.discord`、`natter.feature.ai.openai` 这种命名；
- 禁止使用空格、路径分隔符、大小写混用作为身份。

### 4.2 Discord transport manifest 示例

```json
{
  "schemaVersion": 1,
  "id": "natter.transport.messaging.discord",
  "name": "Discord Transport",
  "version": "0.1.0",
  "kind": "transport",
  "hostApi": ">=0.1.0 <1.0.0",

  "entry": {
    "assembly": "lib/Natter.Plugin.Discord.dll",
    "type": "Natter.Plugin.Discord.DiscordPlugin"
  },

  "description": "Discord upstream for Natter.",
  "author": "Natter",
  "license": "MIT",

  "permissions": [
    "network",
    "secrets:DISCORD_BOT_TOKEN",
    "chat.read",
    "chat.send",
    "chat.edit",
    "chat.delete",
    "chat.commands",
    "chat.interactions",
    "cli.commands"
  ],

  "secrets": [
    {
      "name": "DISCORD_BOT_TOKEN",
      "required": true
    }
  ],

  "configSchema": "config.schema.json",

  "contributions": [
    {
      "type": "chat.upstream",
      "id": "discord"
    },
    {
      "type": "cli.command",
      "id": "discord.status"
    }
  ],

  "platforms": ["windows", "linux", "macos"]
}
```

### 4.3 Entry

```json
"entry": {
  "assembly": "lib/Natter.Plugin.Discord.dll",
  "type": "Natter.Plugin.Discord.DiscordPlugin"
}
```

规则：

- `assembly` MUST 是相对路径，禁止 `..` 逃逸插件目录。
- `type` MUST 是程序集中的完整类型名。
- 类型 MUST 实现 `INatterPlugin`（或 legacy `IPluginModule`，见 §13）。
- 类型 MUST 有宿主可调用的公共构造函数；依赖通过插件容器注入，不建议无参构造 + 服务定位。
- 加载器 MUST 校验入口类型，不再扫描整个程序集猜插件类型。

### 4.4 依赖与顺序

```json
"dependsOn": [
  { "id": "natter.feature.memory", "version": ">=0.1.0 <1.0.0" }
],
"optionalDependsOn": [
  { "id": "natter.cli.diagnostics", "version": ">=0.1.0" }
],
"loadAfter": ["natter.feature.memory"],
"loadBefore": [],
"conflicts": [
  { "id": "natter.transport.messaging.discord.legacy", "version": "<0.2.0" }
]
```

> 示例中的插件 ID 只是占位；当前仓库没有 `natter.feature.memory` / `natter.cli.diagnostics` 这两个插件。核心兼容性由 `hostApi` 表达，不要声明不存在的 core 依赖。

语义：

- `dependsOn`：缺失或版本不匹配 → 插件状态 `Blocked`，不加载入口。
- `optionalDependsOn`：缺失不阻断加载；插件通过 `IPluginContext.Dependencies.IsLoaded(id)` 判断可选集成是否可用。
- `loadAfter` / `loadBefore`：只影响激活顺序，不产生“缺失即失败”。
- `conflicts`：目标存在且版本范围匹配 → 冲突插件激活失败，宿主报告双方。
- 依赖版本使用 SemVer 范围；v1 支持：
  - 精确版本：`1.2.3`
  - 比较：`>=1.2.0 <2.0.0`
  - 并集：`>=1.2.0 <2.0.0 || >=3.0.0 <4.0.0`
  - 预发布版本只有在范围中显式包含预发布标识时才匹配。
- 循环依赖、循环顺序约束 MUST 被拒绝，并报告完整环。
- 拓扑排序 MUST 确定性：依赖优先；同层按插件 `id` 的 ordinal 排序；禁止依赖运行时字典顺序。

### 4.5 Host API 兼容

`hostApi` 描述插件依赖的 Natter 宿主 API 版本，不是 Natter 应用版本，也不是 NLMP 版本。

```json
"hostApi": ">=0.1.0 <1.0.0"
```

规则：

- 宿主维护 `NatterPluginApi.Version`（独立 semver）。
- 宿主在加载前检查范围；不兼容 MUST 拒绝并给出清晰错误。
- host API minor 只允许新增可选 API；major 允许破坏性变更。
- 插件不得依赖宿主内部命名空间、内部类型或未声明的服务。
- v1 不提供 DSH 式 version exemption；后续可加显式豁免。

#### 4.5.1 程序集迁移：B-02a（2026-11-24）

本批把两类**插件可见契约**从 `Natter.Application` 移到 `Natter.Abstractions`。契约的**命名空间与类型名不变**，但定义程序集变了：

| 类型 | 旧程序集 | 新程序集 |
| --- | --- | --- |
| `Natter.Application.Redaction.SecretRedaction`、`SecretRedactionOptions`、`RedactedText` | `Natter.Application` | `Natter.Abstractions` |
| `Natter.Application.Chat.EmbeddingRequest`、`EmbeddingResult`、`IEmbeddingProvider`、`RerankCandidate`、`RerankResult`、`IRerankProvider`、`EmbeddingProviderException`、`RerankProviderException`、`EmbeddingErrorKind`、`IEmbeddingProviderCatalog`、`IRerankProviderCatalog` | `Natter.Application` | `Natter.Abstractions` |

对第三方插件的具体影响：

- **必须针对新宿主重新编译。** 这是二进制 ABI 变更：旧插件里的成员引用指向 `Natter.Application` 的旧定义，新宿主不再在该程序集提供这些类型，未重编译的插件会在加载/激活时失败。
- **源代码 `using` 不需要改**（命名空间未变）。若插件只用到上表类型，应把 `Natter.Application` 的 ProjectReference 换成 `Natter.Abstractions`；若还用到 Prompt/Memory 契约，则仍需 `Natter.Application`（B-02b 才会继续下沉）。
- `hostApi` 版本**不收紧**：这些类型的公开形状没有变化，只是所在程序集变化；兼容性由“重新编译”保证，而不是新增 API 范围约束。
- `Natter.Chat.Discord` 不再引用 `Natter.Application`；`Natter.Chat.DingTalk` 引用 `Natter.Abstractions`，并对 `UpstreamStatusPayload.Detail` 做共享脱敏（`[redacted]`）。
- `Natter.Plugin.AiEmbedding.OpenAi` 不再直接引用 `Natter.Application`。

B-02b（新建 `Natter.Contracts` / `Natter.Prompting`，搬 Prompt/Memory 契约）见 4.5.2。

#### 4.5.2 程序集迁移：B-02b（2026-11-24）

B-02b 让 `Natter.Plugin.Abstractions` **不再引用 `Natter.Application`**，并新增两个程序集：

| 程序集 | 作用 | 依赖 |
| --- | --- | --- |
| `Natter.Contracts` | 插件可见的 Prompt / Memory / Context / Profile 契约 | `Natter.Abstractions`、`Natter.Chat` |
| `Natter.Prompting` | `PromptComposer` / `PromptSafety` 实现，供宿主与 Prompt Library 共用 | `Natter.Contracts`、`Natter.Abstractions`、`Natter.Chat` |

搬迁的类型（命名空间均不变）：

- **Prompt/Context**：`PromptRequest`、`PromptBundle`、`PromptSection`、`IPromptProvider`、`ContextBudget`、`ContextRequest`、`ContextBundle`、`ContextDiagnostics`、`IContextAssembler`、`PromptPlan`、`PromptFragment`、`PromptFragmentContext`、`IPromptFragmentProvider`、`PromptFragmentSourceOptions/Result`、`IPromptFragmentSource`、`PromptFragmentContexts`、`PromptCompilerDefaults/Codes/Exception`、`ConversationScope`。
- **Memory/Profile**：`IMemoryEntryStore` 及其 v2 record/enum、`MemoryScope`、`MemoryType`、`MemoryEntryExport`、`IMemoryStore`（v1，保留兼容）、`RuntimeProfile` 及 `Profile*` 模型、`StoredMessage`、`ConversationKey`、`IChatResponder`。
- `PromptComposer` / `PromptSafety` 的实现位于 `Natter.Prompting`；Application 与 `Natter.Plugin.PromptLibrary` 均引用该程序集。
- `Sample.Memory` 改为自带 `SampleMemoryEntryStore`（约 200 行），不再引用 `Application` / `Chat`；`PromptLibrary` 不再引用 Application，也不再 `new` Application 里的宿主实现（改为引用 `Natter.Prompting`）。

对第三方插件的具体影响（五步迁移）：

1. **必须重新编译。** 契约类型的定义程序集从 `Natter.Application` 变为 `Natter.Contracts`，旧二进制里的类型引用无法解析。
2. 把只用到契约的 `Natter.Application` ProjectReference 改为 `Natter.Contracts`；如果用到 `PromptComposer`，再加 `Natter.Prompting`。
3. `using` 不变（命名空间仍是 `Natter.Application.Chat` / `Natter.Application.Redaction`）。
4. **不要把 `Natter.Contracts` / `Natter.Prompting` 的 DLL 放进插件目录作为私有依赖**：宿主把它们列为共享契约程序集（`PluginContractAssemblies`），保证类型标识一致；插件目录里同名的本地副本会被忽略或造成诊断混淆。
5. `hostApi` **不收紧数值**：本次没有改变公开 API 形状，也没有新增 API 版本常量；兼容性由“重新编译 + 共享契约程序集”保证。若未来需要让旧二进制显式失败，再提升 `hostApi` minor 并写进 manifest 迁移说明。

已知取舍：`Natter.Contracts` 里仍有少量**无 I/O 的 pure helper**（`MemoryDecay` 数学、`MemoryEntryMapping`、`ProfileContext` AsyncLocal、`PromptPluginFragmentRules` 正则），它们被 record 的便捷方法直接引用，拆出去会造成契约→实现的循环；当前它们不含持久化、catalog 或宿主服务，后续若要彻底纯化需把这些方法改为 Application/Prompting 的扩展方法。

### 4.6 权限

权限是 manifest 的声明，不是“代码能力”的证明。

v1 权限词汇：

| 权限 | 含义 |
| --- | --- |
| `network` | 发起出站 HTTP/WebSocket 连接 |
| `filesystem.read` | 读取插件目录外的文件 |
| `filesystem.write` | 写入插件目录外的文件 |
| `secrets:<NAME>` | 读取指定秘密 |
| `chat.read` | 读取 NLMP 入站消息/事件，以及 prompt/context 贡献所需的会话、记忆与摘要事实 |
| `chat.send` | 发送消息 |
| `chat.edit` | 编辑消息 |
| `chat.delete` | 删除消息 |
| `chat.commands` | 注册/处理平台命令（Discord slash 等） |
| `chat.interactions` | 处理按钮/Modal/选择器 |
| `ai.complete` | 调用宿主 AI 端口 |
| `storage` | 使用宿主持久化端口 |
| `cli.commands` | 贡献 CLI 命令 |

规则：

- 插件 SHOULD 只申请实际需要的权限。
- 宿主 MUST 在激活前授予/拒绝；未授予时插件不得获得对应 capability。
- `IPluginContext.Secrets` 只有在拥有 `secrets:<NAME>` 时才能读取该秘密。
- 权限拒绝产生结构化错误，不导致宿主崩溃；依赖该能力的插件进入 `Blocked` 或降级。
- 权限模型是**治理与可观测性**，不是安全沙箱；见 §15。

**贡献类型的最低权限**（S2-C2，2026-11-22）：

| Contribution | 最低权限 | 原因 |
| --- | --- | --- |
| `context.assembler` | `chat.read` | 组装上下文会读取会话/记忆/摘要事实 |
| `prompt.provider` | `chat.read` | 生成 prompt 需要会话与 profile 上下文 |
| `prompt.fragment` | `chat.read` | 片段提供者收到 profile/source/user/conversation 上下文 |
| `embedding.provider` | `network` + 至少一个 `secrets:*` | 调用外部 embedding API 并携带 key |
| `rerank.provider` | `network` + 至少一个 `secrets:*` | 调用外部 rerank API 并携带 key |

未声明对应权限时，`IContributionRegistry.AddContextAssembler` /
`AddPromptProvider` / `AddPromptFragmentProvider` 抛出权限错误，宿主把插件置为
`Failed`（`phase=permission`、`code=permission-denied`），并给出缺少的权限与 contribution。
零权限插件不能通过这几类贡献读取宿主会话数据。

### 4.7 秘密

```json
"secrets": [
  { "name": "DISCORD_BOT_TOKEN", "required": true }
]
```

规则：

- manifest MUST NOT 包含秘密值。
- 秘密通过宿主秘密提供者解析：环境变量、系统密钥库、外部 secret manager。
- 配置文件中使用 `${secret:NAME}` 引用，例如：

```json
{
  "token": "${secret:DISCORD_BOT_TOKEN}"
}
```

- 缺失 required secret → 插件激活失败，错误只显示 secret 名称，不显示值。
- 日志、`plugins doctor`、`plugins inspect`、异常消息 MUST 脱敏。
- 插件不得把秘密写入自己的目录、配置文件或消息正文。

### 4.8 配置 Schema

```json
"configSchema": "config.schema.json"
```

规则：

- 相对插件目录，禁止路径逃逸。
- 使用 JSON Schema Draft 2020-12 的子集：`type`、`properties`、`required`、`additionalProperties`、`enum`、`default`、`minimum`、`maximum`、`pattern`、`items`、`oneOf`。
- 宿主在激活前校验 `config.json`；无效配置 → 插件 `Failed`，错误定位到 JSON Pointer。
- `default` 只用于展示/补全，不在用户文件中静默写入。
- v1 不支持配置热更新；保存后重启生效。
- 配置中的秘密引用由宿主在交给插件前解析为 secret handle，不把明文传给 `IPluginConfig` 的普通字符串属性（设计目标；v1 可用 `SecretReference` 包装）。

Discord 示例：

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "additionalProperties": false,
  "required": ["token", "intents"],
  "properties": {
    "token": {
      "type": "string",
      "pattern": "^\\$\\{secret:[A-Z0-9_]+\\}$"
    },
    "intents": {
      "type": "array",
      "items": {
        "enum": ["Guilds", "GuildMessages", "DirectMessages", "MessageContent"]
      }
    },
    "guilds": {
      "type": "array",
      "items": { "type": "string" }
    },
    "channels": {
      "type": "array",
      "items": { "type": "string" }
    },
    "requireMention": {
      "type": "boolean",
      "default": true
    },
    "commands": {
      "type": "object",
      "additionalProperties": { "type": "boolean" }
    }
  }
}
```

### 4.9 Contributions

```json
"contributions": [
  { "type": "chat.upstream", "id": "discord" },
  { "type": "cli.command", "id": "discord.status" }
]
```

规则：

- manifest 中的 contribution 是**声明**，用于发现、冲突检测和诊断。
- 运行时 MUST 在 `ConfigureAsync` 中注册同类型、同 id 的 contribution；否则激活失败。
- 不得声明未实现的 contribution；不得注册未声明的 contribution（v1 可警告，后续版本应拒绝）。
- contribution id 在 `(type, id)` 内唯一；重复 MUST 拒绝。
- v1 contribution 类型见 §6。

---

## 5. Host API

### 5.1 版本

```csharp
public static class NatterPluginApi
{
    public const string Version = "0.1.0"; // 示例；以宿主实际暴露为准
}
```

宿主 API 版本与：

- Natter 应用版本；
- NLMP 模型版本；
- 插件自身版本；

分别独立。

### 5.2 插件入口

```csharp
public interface INatterPlugin
{
    ValueTask ConfigureAsync(
        IPluginContext context,
        CancellationToken cancellationToken = default);

    ValueTask ActivateAsync(
        IPluginContext context,
        CancellationToken cancellationToken = default);

    ValueTask DeactivateAsync(
        IPluginContext context,
        CancellationToken cancellationToken = default);
}
```

语义：

- `ConfigureAsync`：只做注册、校验、准备；MUST NOT 发起网络/数据库/消息发送等副作用。
- `ActivateAsync`：启动插件自身资源；宿主已创建插件容器。
- `DeactivateAsync`：停止资源；必须可重复调用、幂等。
- 实现 `IAsyncDisposable` 时，宿主在卸载时调用；不能替代 `DeactivateAsync`。
- 入口类型通过构造函数注入依赖；不建议服务定位。

### 5.3 插件上下文

```csharp
public interface IPluginContext
{
    PluginManifest Manifest { get; }
    IPluginConfig Config { get; }
    IPluginSecrets Secrets { get; }
    IPluginLog Log { get; }
    IPluginLifetime Lifetime { get; }

    IPluginServices Services { get; }
    IContributionRegistry Contributions { get; }
    IPluginDependencies Dependencies { get; }
}

public interface IPluginConfig
{
    JsonElement Raw { get; }
    T Get<T>() where T : class;
    bool TryGet<T>(out T value) where T : class;
}

public interface IPluginSecrets
{
    string GetRequired(string name);
    string? Get(string name);
}

public interface IPluginLog
{
    void Debug(string message, params object?[] args);
    void Information(string message, params object?[] args);
    void Warning(string message, params object?[] args);
    void Error(Exception? exception, string message, params object?[] args);
}

public interface IPluginLifetime
{
    CancellationToken Stopping { get; }
    IDisposable OnStop(Func<CancellationToken, ValueTask> callback);
}

public interface IPluginDependencies
{
    bool IsLoaded(string pluginId);
    string? GetVersion(string pluginId);
}
```

职责：

- `Config`：已校验的插件配置；
- `Secrets`：按权限读取秘密，返回值不得被日志记录；
- `Log`：结构化日志，自动带 `pluginId`，不直接暴露宿主 `ILoggerFactory`；
- `Lifetime`：宿主停止信号、注册清理回调；
- `Services`：**插件本地**服务集合，不是宿主根容器；
- `Contributions`：声明贡献；
- `Dependencies`：查询可选/必需依赖的加载状态。

### 5.4 插件本地服务

```csharp
public interface IPluginServices
{
    IServiceCollection Collection { get; }
}
```

规则：

- 插件内部服务 MUST 注册到插件本地集合，不进入宿主根集合。
- 宿主为每个插件创建独立的 provider/scope；插件只能解析自己的服务，以及宿主明确开放的合约。
- 插件贡献的工厂从插件本地 provider 解析，不直接解析宿主内部服务。
- v1 允许通过构造函数注入宿主公开合约（如 `IClock`、`IChatEventSource`、`IChatCommandDispatcher`）；这些合约由宿主显式列入白名单。
- 禁止 `context.Services` 直接覆盖宿主服务。

### 5.5 Contribution 注册

```csharp
public interface IContributionRegistry
{
    void AddChatUpstream<TUpstream>(string id)
        where TUpstream : class, IChatUpstream;

    void AddChatResponder<TResponder>(string id, int priority = 0)
        where TResponder : class, IChatResponder;

    void AddChatEventSubscriber<TSubscriber>(string id)
        where TSubscriber : class, IChatEventSubscriber;

    void AddCliCommand(CliCommandDescriptor descriptor);

    void AddPortAdapter<TService, TImplementation>(
        string id,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where TService : class
        where TImplementation : class, TService;

    void AddEmbeddingProvider<TProvider>(string id)
        where TProvider : class, IEmbeddingProvider;

    void AddRerankProvider<TProvider>(string id)
        where TProvider : class, IRerankProvider;
}
```

说明：

- `IChatEventSubscriber` 是 NPS v1 提议的接口；如果当前版本没有，可先用 `IChatMessageHandler` 或插件自己的生命周期订阅，后续统一。
- `IChatEventSubscriber` 的最小形状：

```csharp
public interface IChatEventSubscriber
{
    ValueTask HandleAsync(
        ChatEvent chatEvent,
        CancellationToken cancellationToken = default);
}
```

- `AddPortAdapter` 是 legacy 兼容入口；新插件优先使用具体 contribution。
- 所有 contribution 在 staging 阶段注册；宿主校验通过后才 commit。
- 同一 `(type, id)` 重复注册必须失败，并指出双方插件。

---

## 6. Contribution 类型 v1

| type | 运行时接口 | 用途 | 典型插件 |
| --- | --- | --- | --- |
| `chat.upstream` | `IChatUpstream` | 接入聊天平台 | Discord、Satori |
| `chat.responder` | `IChatResponder` | 把入站事件转为出站命令 | AI、规则回复 |
| `chat.event-subscriber` | `IChatEventSubscriber` | 旁路消费 NLMP 事件 | 审计、统计 |
| `cli.command` | `ICliCommandHandler` + descriptor | 贡献 CLI 命令 | `discord status` |
| `port.adapter` | 端口接口 | 通用六边形适配器（兼容） | 自定义仓储/时钟 |
| `embedding.provider` | `IEmbeddingProvider` | 文本 → 向量（P6.2） | OpenAI 兼容、DashScope |
| `rerank.provider` | `IRerankProvider` | 查询 + 候选 → 重排（P6.2） | 搜索/召回插件 |

约束：

- 一个插件可以声明多个 contribution，但 MUST 在 manifest 中列出。
- `chat.responder` 按 `priority` 升序调用；同优先级按插件 id 排序；第一个返回非 null 命令的 responder 生效。v1 也可配置为全部执行，但必须显式配置，避免重复回复。
- `chat.upstream` 的 `ConnectAsync` / `DisconnectAsync` 由宿主统一驱动，不由插件自己随意启动；见 §7。
- `chat.event-subscriber` 只读，不得直接发送命令；需要回复时使用 `chat.responder`。

### 6.0 实现方义务（义务清单，2026-11-24）

上表只规定「贡献什么类型」；**类型义务**（实现方必须保证什么、可以依赖宿主什么、违反的后果、以及当前靠什么强制）见
[CONTRIBUTION_OBLIGATIONS.md](CONTRIBUTION_OBLIGATIONS.md)。那里有一张 11 行 × 5 列的表，第 5 列「当前靠什么强制」是重点——
它区分「有声明义务」和「有保障」，两者不是一回事。

两条已经机制化的义务（写插件前请先看）：

1. **`chat.upstream` 的分片义务是条件的**：声明了 `Capabilities.MaxMessageLength` 就 MUST 按该上限分片；
   声明 `null` 等于**显式放弃**该义务，且放弃必须可观测（告警或 health 字段）。**没有第三种状态**——
   「既不分片也不声明放弃」就是 Satori 曾经的样子，测试会红。
2. **权限策略必须显式声明**：`NpsPermissionPolicy.RequiredPermissions` 对未登记的类型**抛异常**而不是默认零权限。
   新增贡献类型时必须同时给出权限策略，否则激活期报错。

> 已知未闭合项：`port.adapter` 目前**不要求任何权限**（显式空清单，非遗漏）。收紧它会让现有 bundled 示例插件
> （`samples/Natter.Sample.Plugin.Greetings`）停止激活，因此需要一个独立的 `port.override` 权限并随信任级默认授予，
> 见义务文档 §5.4。

### 6.1 Embedding / Rerank 端口（P6.2，2026-11-22）

`embedding.provider` / `rerank.provider` 与 `chat.responder` 一样由插件实现、宿主持有目录，注册引用为
`owner#id`；Stop / Rollback / Dispose 会撤销目录项。`IContributionRegistry` 的默认实现抛
`NotSupportedException`，NPS registry 覆写并执行权限门控。

```csharp
public interface IEmbeddingProvider
{
    string Id { get; }
    int MaxBatchSize { get; }
    int MaxInputTokens { get; }
    ValueTask<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken ct = default);
}

public interface IRerankProvider
{
    string Id { get; }
    int MaxCandidates { get; }
    ValueTask<RerankResult> RerankAsync(string query, IReadOnlyList<RerankCandidate> candidates, CancellationToken ct = default);
}
```

- **能力元数据是契约**：宿主 `EmbeddingService` 按 `MaxBatchSize` 切批，按 `MaxInputTokens` 做保守截断
  （CJK 1.5 字符/token、其他 4 字符/token，保留 10% 余量），并记录被截断的输入下标；不得让调用方自行猜测。
- 结果顺序与输入一致；任一批失败整体抛错，不返回半成功结果，也不在失败前写缓存。
- `IEmbeddingCache` 键为 `(model, dimensions, SHA-256(text))`；本轮提供 `InMemoryEmbeddingCache`，
  SQLite 实现留给 RAG 插件自己的库。
- `RerankService` 在超过 `MaxCandidates` 时以 `rerank.too_many_candidates` 拒绝，不静默截断候选。
- 参考实现 `plugins/Natter.Plugin.AiEmbedding.OpenAi/` 走 OpenAI 兼容 `POST {endpoint}/embeddings`；
  测试使用 fake HTTP handler，不联网。DashScope compatible mode 的模型名/维度/批量/单条 token 上限
  必须在配置中显式填写；仓库未对真实 DashScope 做联网验证，不能把默认值当作官方承诺。

---

## 7. 依赖、加载与激活

### 7.1 状态机

```text
Discovered
  → Validated
  → Loaded
  → Configured
  → Activated
  → Stopped
  → Unloaded

任意阶段失败 → Failed
依赖失败     → Blocked
用户禁用     → Disabled
```

### 7.2 加载序列

1. 扫描插件目录，读取 manifest。
2. 校验 `schemaVersion`、字段、路径、入口、host API。
3. 解析依赖图与顺序约束，检测循环/冲突/重复 id。
4. 解析配置 schema、秘密引用、权限请求。
5. 为每个可加载插件创建 ALC，加载入口程序集。
6. 实例化插件。
7. 调用 `ConfigureAsync`，收集 staging contributions。
8. 校验 contribution 类型、id、权限、依赖、配置。
9. commit：将 contributions 放入宿主 registry，构建插件 provider。
10. 按拓扑顺序调用 `ActivateAsync`。
11. 宿主启动所有 `chat.upstream` contributions（`ConnectAsync`）。
12. 标记 `Activated`。

失败处理：

- 第 7–9 步失败：不启动任何该插件资源；回滚 staging；标记 `Failed`。
- 第 10 步失败：停止已启动的上游/插件，回滚 contributions；依赖它的插件标记 `Blocked`。
- 第 11 步失败：停止该上游，标记 `Failed`；其他上游继续；依赖它的 feature 插件降级或 `Blocked`。

### 7.3 停止序列

1. 停止所有 `chat.upstream`（`DisconnectAsync`），逆依赖顺序。
2. 调用插件 `DeactivateAsync`，逆激活顺序。
3. 释放插件 provider。
4. 卸载 ALC（best-effort）。
5. 标记 `Stopped` / `Unloaded`。

宿主 MUST NOT 只依赖 `Dispose` 来断开上游；必须显式 `DisconnectAsync`。

### 7.4 启用/禁用

- `plugins list` / `plugins enable` / `plugins disable` 是宿主状态，不修改 manifest。
- 禁用插件不加载入口，不注册 contribution。
- 禁用被依赖插件时，依赖方进入 `Blocked`，错误说明原因。
- v1 重启生效；热启用/禁用属于后续版本。

---

## 8. 配置与秘密

### 8.1 配置来源

优先级（从高到低）：

1. 宿主 CLI/环境显式覆盖；
2. 插件目录 `config.json`；
3. manifest/ schema 默认值。

v1 不引入远程配置中心；后续可通过宿主配置 provider 扩展。

### 8.2 校验

- 校验发生在加载入口之前。
- 错误必须包含：`pluginId`、JSON Pointer（如 `/intents/0`）、期望、实际。
- 无效配置只影响该插件，不影响其他插件。
- required secret 缺失在激活前报告，不打印值。

### 8.3 配置模型

`IPluginConfig` 的定义见 §5.3。

- 反序列化使用插件声明的类型；失败转换为配置错误。
- 秘密字段使用 `SecretReference` 或 `IPluginSecrets` 读取，避免普通字符串被日志打印。
- v1 不承诺配置热更新。

---

## 9. 权限与信任

### 9.1 信任等级

| 等级 | 例子 | 默认权限策略 |
| --- | --- | --- |
| `bundled` | 官方内置插件 | 可预授予 manifest 请求的权限 |
| `signed` | 已签名第三方 | 用户确认后授予 |
| `local` | 本地开发插件 | 开发模式可自动授予，生产需确认 |
| `untrusted` | 来源不明 | 默认不加载，或仅授予最小权限 |

v1 已实现 `bundled` / `signed` / `local` / `untrusted` 四档（信任判定见 §9.4）。

### 9.4 信任与完整性的传播（S1-12，2026-11-22）

插件 store 在安装时校验哈希清单（`package.sha256`）与签名（`package.sig`），把结果写进
`installed.json` 的 `trust`（`signed` / `unsigned`）与 `signerKeyId`。**激活阶段必须消费这份结果**，
否则"只信任签名包"的配置形同虚设。

#### 9.4.1 激活期的三道检查

1. **完整性复核**：每次启动对已安装目录重新计算 `package.sha256` 中每个文件的 SHA-256
   （跳过 `package.sha256` / `package.sig` 自身），并检测未声明的新文件与缺失文件。
   - 全部匹配 → `verified`
   - 有不符 → `tampered`（**拒绝加载**，码 `plugin.integrity-mismatch`）
   - 无哈希清单 / 无法读取 → `unverified`（显式记录，不当作可信）
2. **签名事实**：只有"安装记录为 `signed` + `signerKeyId` 非空 + 启动完整性 `verified`"才算
   `signed`；签名本身在安装时验证（启动期没有公钥就无法复验），因此动态宿主无法复验时必须记
   `unverified` 而不是假定可信。
3. **策略判定**（`PluginTrustPolicy`）：

| 候选状态 | `RequireSignedPlugins` | `FilePluginTrustLevel` | 结果 |
| --- | --- | --- | --- |
| `tampered` | 任意 | 任意 | **拒绝** `plugin.integrity-mismatch` |
| `signed`（已验证） | 任意 | 任意 | 加载为 `signed`（不降级） |
| `unverified` | `true` | 任意 | **拒绝** `plugin.unsigned-rejected` |
| `unverified` | `false` | `signed` | **拒绝** `plugin.signature-required` |
| `unverified` | `false` | `local`（默认） | 加载为 `local`，并打 `plugin.integrity-unverified` 警告 |
| `unverified` | `false` | `untrusted` | 加载为 `untrusted`（不授予权限） |

- `RequireSignedPlugins` 默认 **false**，保持既有部署行为；开启后任何无已验证签名的包都不加载。
- 纯目录（`--plugin-dir`、样例插件、staging 目录）没有 `package.sha256`，只能得到 `unverified`：
  默认按 `FilePluginTrustLevel` 加载并记录警告；`--plugin-dir` 场景因此**不会**因本机制启动失败。
- 宿主把判定标签写进 `PluginDescriptor.Trust`（`signed` / `local(unverified)` / `untrusted`），
  `plugins list` / `plugins doctor` 可见；拒绝时 `ErrorDetail.Phase = trust`，`Code` 见上表，
  消息只含插件 id、目录与首个不符文件名，不含文件内容。
- 配置入口：`NatterApplicationBuilder.ConfigurePluginTrust(o => o.RequireSignedPlugins = true)` 与
  `AddPluginDirectory(new PluginTrustRegistration(dir, signature, signerKeyId, pluginId, detail))`。
  CLI 开关留给 CLI 层按需接入（本版本未提供命令行参数）。

#### 9.4.2 签名格式、校验上限与兼容策略

安装期校验（`PluginPackageValidator`）的确定性规则：

| 项 | 规则 |
| --- | --- |
| 解压上限 | 条目数/单条目/总量/压缩比在**实际写入**时逐字节计数，超限立即中止提取（声明长度只用于快速预检） |
| `package.sig` | 硬上限 **64 KiB**，超限 `signature-too-large`；按长度读取，不做无界 `ReadAllBytes` |
| 哈希比较 | `CryptographicOperations.FixedTimeEquals`，大小写归一 |
| 原生镜像 | 按 magic 识别 ELF / Mach-O / PE-without-CLI，**不看扩展名**；`AllowNativeLibraries=false` 时一律 `native-library` |
| entry 路径 | 必须位于包内（`GetFullPath` + 前缀校验）；拒绝控制字符（含换行）、`..`、绝对路径、符号链接 |

**Canonical 文本 v2**（新签名使用）：

```text
nps-hash-v2
<文件数>
<路径字节长度>:<URL 转义路径>
<sha256>
…
```

长度前缀 + 转义消除了"`hash + 两空格 + 路径 + 换行`"拼接的边界歧义；配合入口拒绝控制字符，路径不可能再影响签名覆盖范围。

**兼容策略——默认兼容，可升级为严格**：

- 用旧（v1）canonical 文本签名的包**默认仍然通过校验**，但会产出 `signature-legacy-format` **warning**（进入 `PluginStore.Warnings`），提示发布方用 v2 重签；
- 需要强约束的部署设 `PluginStoreOptions.RejectLegacySignatureFormat = true`，同一包直接失败并返回 `signature-legacy-format`；
- 这样既不因格式升级让既有已签名包停摆，也不把旧格式静默当成新格式。

#### 9.4.3 已知边界

- 启动期能复验**内容完整性**，不能复验签名（无公钥）；签名只在安装时生效。
- `.staging` 目录中的尚未完成的安装不在候选列表中。
- 宿主仍然是进程内加载：完整性检查防的是"落地后被替换/篡改"，不是恶意代码隔离（见 §9.3）。

### 9.2 权限执行点

宿主必须在以下位置检查：

- `IPluginSecrets` 读取；
- `IChatUpstream` 的读取/发送/编辑/删除；
- `IChatCommandDispatcher` 调用；
- `network` 相关 HTTP/WebSocket 工厂；
- `ai.complete` 调用；
- `cli.command` 注册；
- `filesystem.*` 访问；
- `context.assembler` / `prompt.provider` / `prompt.fragment` 注册（要求 `chat.read`）。

### 9.3 进程内限制

.NET `AssemblyLoadContext` 只做加载隔离，不做安全隔离。

- manifest 权限是治理声明；
- 恶意插件仍可通过反射/进程权限绕过；
- 真正不可信第三方插件需要独立进程、容器或 OS 级沙箱；
- 不要把 `permissions` 宣传为安全边界。

---

### 9.5 Android 信任根（S2-C14，2026-11-22）

Android 动态插件宿主的 publisher trust anchors（`PluginStoreOptions.TrustedPublicKeys`）现在
**编译进 APK**：`src/Natter.Hosting.Android/trusted-keys.txt` 作为 embedded resource
（`Natter.Hosting.Android.trusted-keys.txt`）读取，运行时不再读取
`FilesDir/plugin-trusted-keys.txt`。这样能写应用私有目录的进程不能替换信任锚；要更换锚点必须
重新构建并签名 APK。

- Release/dynamic 构建：发布方在构建前把 Base64 SubjectPublicKeyInfo 写入该文件；仓库内文件只有
  注释，因此默认 APK 不信任任何动态插件（与"未配置信任锚"语义一致）。
- Debug 构建仍可用 `AllowUnsigned=true` 做 sideload 开发；生产不得开启。
- **已知限制（不假装解决）**：这是应用签名/APK 完整性边界的延伸，不是硬件根信任。已 root/解锁的
  设备仍可 patch APK、hook 进程或替换整个应用数据目录；v1 未集成 Play Integrity / Key Attestation，
  也没有独立的系统级 trust store。高保障部署应使用 embedded 插件模式、Play App Signing，并在设备
  侧校验 APK 签名/完整性；不要把动态插件信任当作安全隔离边界（见 §9.3）。
- 仍然保留的缓解：安装期签名校验、启动期 `package.sha256` 完整性复核、`RequireSignedPlugins`
  默认关闭但可在 Release 打开、篡改候选在 trust policy 中无条件拒绝。Android 动态宿主会把
  `InstalledPluginCandidate.Integrity` 映射为 `PluginSignatureState`，`Tampered` 在 trust policy
  中无条件拒绝（`plugin.integrity-mismatch`），与桌面加载路径一致。

---

## 10. 诊断与 CLI

v1 应提供：

```bash
natter plugins list --detail --json
natter plugins graph --json
natter plugins doctor <plugin-id>
natter plugins contributions --json
natter plugins permissions --json
natter plugins config validate <plugin-id>
```

建议输出：

- `plugins graph`：节点（id/version/kind/state）、边（dependsOn/loadAfter）、循环/冲突；
- `plugins contributions`：type/id/plugin/status；
- `plugins permissions`：requested/granted/denied；
- `plugins doctor`：manifest、hostApi、config、secrets、dependencies、activation error；
- `doctor`：插件总数、失败数、blocked 数、contribution 数、上游状态。

结构化错误：

```json
{
  "phase": "dependency",
  "code": "missing-dependency",
  "pluginId": "natter.transport.messaging.discord",
  "message": "Required plugin 'natter.feature.memory' >=0.1.0 <1.0.0 was not found.",
  "detail": {
    "dependency": "natter.feature.memory",
    "range": ">=0.1.0 <1.0.0"
  }
}
```

`phase` 取值：`manifest` / `dependency` / `load` / `configure` / `config` / `permission` / `activate` / `runtime`。

> 示例中的插件 ID 仅用于展示错误结构；当前仓库并没有这些插件。核心兼容性由 `hostApi` 表达，不要声明不存在的 core 依赖。

---

## 11. Discord transport plugin

> 完整的 Discord transport 设计见 [Discord Transport Plugin Design](DISCORD_PLUGIN_DESIGN.md)。
>
> **实现状态**：`natter.transport.messaging.discord` 示例插件与 `Natter.Chat.Discord` 已交付消息上游、typing、编辑/删除、slash interaction、allowlist/mention 过滤、分片与 `discord status`；附件上传、按钮/Modal、stream edit 尚未交付。

### 11.1 定位

Discord 是 `transport` 插件，不是 feature：

```text
Discord Gateway / REST
        │
DiscordChatUpstream（chat.upstream）
        │ NLMP
Chat Core / Orchestrator
        │
AI / memory / tools（feature plugins）
```

它 MUST NOT 包含 AI 逻辑、prompt、记忆或业务规则。

### 11.2 配置

```json
{
  "token": "${secret:DISCORD_BOT_TOKEN}",
  "selfId": "123456789012345678",
  "intents": ["Guilds", "GuildMessages", "DirectMessages", "MessageContent"],
  "guilds": ["123456789012345678"],
  "channels": ["987654321098765432"],
  "requireMention": true,
  "commands": {
    "ask": true
  }
}
```

- `intents` 可配置；slash-only 模式不请求 `MessageContent`。
- `guilds`/`channels` 是白名单；空列表表示不启用对应范围（由插件实现定义）。
- `requireMention` 决定 guild 消息是否必须 mention bot。
- `commands` 控制斜杠命令注册。

### 11.3 插件实现

```csharp
public sealed class DiscordPlugin : INatterPlugin
{
    public ValueTask ConfigureAsync(
        IPluginContext context,
        CancellationToken cancellationToken = default)
    {
        context.Services.Collection.AddSingleton<DiscordSocketClient>();
        context.Services.Collection.AddSingleton<DiscordChatUpstream>();

        context.Contributions.AddChatUpstream<DiscordChatUpstream>("discord");
        context.Contributions.AddCliCommand(DiscordCommands.Status);

        return ValueTask.CompletedTask;
    }

    public ValueTask ActivateAsync(
        IPluginContext context,
        CancellationToken cancellationToken = default)
    {
        // 只做非网络准备；连接由宿主调用 IChatUpstream.ConnectAsync
        return ValueTask.CompletedTask;
    }

    public ValueTask DeactivateAsync(
        IPluginContext context,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }
}
```

> 连接/断开由宿主统一调用 `IChatUpstream.ConnectAsync` / `DisconnectAsync`。插件不得在 `ConfigureAsync` 里连接。

### 11.4 DiscordChatUpstream 责任

- 从 `IPluginConfig` 读取配置，从 `IPluginSecrets` 读取 token；
- 创建 `DiscordSocketClient`，配置 intents；
- `ConnectAsync`：`LoginAsync` → `StartAsync` → 等待 `Ready`（超时）；
- `DisconnectAsync`：注销事件、`StopAsync`、`LogoutAsync`；
- 入站映射：`SocketMessage` / `SocketSlashCommand` → NLMP event；
- 出站映射：NLMP command → Discord send/update/delete/interaction；
- 过滤：bot/self、guild/channel allowlist、mention 规则；
- 分片：按平台上限切分，不硬编码单一长度；
- 安全：AI 输出 `AllowedMentions.None`，禁止 `@everyone`/`@here`；
- 交互：3 秒内 `DeferAsync`，结果 `FollowupAsync`；
- 限流：遵守 Discord 429/retry-after；
- 能力：通过 `IChatUpstream.GetCapabilitiesAsync` 返回 slash/threads/attachments/edit 等能力；
- 健康：`IsConnected`、`LastError`、guild 数、latency 暴露给 `discord status`。

### 11.5 测试

- 用 `IDiscordTransport` 抽象 Discord Gateway/REST；CI 不连真实 Discord。
- 用录制的网关事件/交互 fixture 做映射测试。
- 覆盖：消息、斜杠、编辑、删除、附件、mention、allowlist、3 秒 defer、分片、限流。
- 用 fake AI responder 做端到端：Discord event → NLMP → responder → command → fake Discord send。

---

## 12. Feature plugin 示例：AI responder

> AI provider / DeepSeek responder 的完整设计见 [Natter AI Provider Design](AI_PROVIDER_DESIGN.md)。
>
> **实现状态**：`natter.feature.ai.deepseek` 已交付 `IChatClient` + 自定义 DeepSeek Chat Completions adapter、普通消息与 slash responder、内存历史、per-conversation gate、幂等、错误降级与 `ai status`；v1 非思考、非流式、无 tools。

```json
{
  "schemaVersion": 1,
  "id": "natter.feature.ai.deepseek",
  "name": "DeepSeek AI Responder",
  "version": "0.1.0",
  "kind": "feature",
  "hostApi": ">=0.1.0 <1.0.0",
  "entry": {
    "assembly": "lib/Natter.Feature.Ai.DeepSeek.dll",
    "type": "Natter.Feature.Ai.DeepSeek.DeepSeekPlugin"
  },
  "permissions": [
    "network",
    "secrets:DEEPSEEK_API_KEY",
    "chat.read",
    "chat.send",
    "chat.interactions"
  ],
  "secrets": [
    { "name": "DEEPSEEK_API_KEY", "required": true }
  ],
  "configSchema": "config.schema.json",
  "contributions": [
    { "type": "chat.responder", "id": "deepseek", "priority": 10 }
  ]
}
```

关键点：

- AI 插件不依赖 Discord 插件，只依赖 `hostApi` 与 NLMP 契约；
- AI 插件不 import Discord.Net；
- Discord 插件也不 import AI SDK；
- 换模型、换 persona、加记忆工具，都不需要改 Discord transport；DeepSeek 适配与 M.E.AI 细节见 [AI Provider 设计](AI_PROVIDER_DESIGN.md)。

---

## 13. 与当前实现的迁移

当前仓库的插件形态是 bootstrap：

| 当前 | NPS v1 | 迁移策略 |
| --- | --- | --- |
| `plugin.json` 无 `schemaVersion` | `schemaVersion: 1` | 无版本视为 legacy，加载器兼容 |
| `entryAssembly` | `entry.assembly` + `entry.type` | 优先 entry；legacy 回落 |
| `IPluginModule.Metadata` | manifest | manifest 为事实来源；legacy metadata 可校验一致性 |
| `IPluginModule.Configure(IPluginRegistrationContext)` | `INatterPlugin.ConfigureAsync(IPluginContext)` | 提供 `LegacyPluginAdapter` |
| `context.Services` 根容器 | `context.Services.Collection` 插件本地 | legacy 暂时保留，标记 obsolete |
| `AddPortAdapter` | `AddPortAdapter` contribution | 保留兼容 |
| `IPluginLifecycle` | `INatterPlugin.ActivateAsync/DeactivateAsync` | legacy 仍由宿主调用 |
| 无依赖图 | `dependsOn`/`loadAfter` | 新增 loader 阶段 |
| 无配置 schema | `configSchema` | 新增配置解析 |
| 无权限 | `permissions` | 新增授权检查 |
| 无 contribution 清单 | `contributions` | 新增 registry |

兼容策略：

- v1 宿主 MUST 能加载 legacy 插件；
- legacy 插件使用 legacy 规则：manifest 缺字段时用 Metadata 补全；
- legacy `IPluginLifecycle` 与 `INatterPlugin` 不重复启动；
- 迁移完成后的示例插件使用 NPS v1；
- v1 内不删除 legacy API；到 Natter 1.0 再决定弃用。

---

## 14. 一致性测试

至少覆盖：

1. manifest schema 校验、未知字段、错误路径；
2. host API 不兼容拒绝；
3. 依赖缺失、版本不匹配、循环、冲突；
4. `loadAfter`/`loadBefore` 确定性排序；
5. 可选依赖缺失不阻断；
6. 重复插件 id / contribution id；
7. config schema 校验、default、JSON Pointer 错误；
8. secret 缺失、权限拒绝、日志脱敏；
9. `Configure` 无副作用；
10. 激活失败回滚、依赖方 Blocked；
11. 上游 Connect/Disconnect 由宿主驱动；
12. 停止顺序为逆依赖；
13. 禁用/启用（重启生效）；
14. legacy 插件适配；
15. Discord 事件/交互/分片/限流 fixture；
16. `plugins graph`/`contributions`/`permissions` JSON 结构；
17. 卸载后无残留订阅/定时器/连接；
18. 插件目录路径逃逸、入口类型非法。

---

## 15. 非目标与安全边界

v1 不做：

- 热更新 / HMR；
- 插件市场 / 远程安装；
- 插件签名（可预留 manifest 字段）；
- 进程内安全沙箱；
- 跨语言插件；
- UI 插件；
- RimWorld 式 XML Defs/Patches；
- 任意宿主 DI 访问。

安全说明：

- 进程内插件默认是**受信任代码**；
- `permissions` 是宿主治理与可观测性，不是强制隔离；
- 真正的第三方不可信插件需要 out-of-process plugin host；这应作为 v2 的独立设计；
- Discord token 必须通过 secret provider 注入，禁止进入 manifest、配置样例、日志、异常和 AI prompt。

---

## 16. 实施路线

### Phase 1：规范与内核（已实现）

- ✅ manifest v1 + JSON Schema 子集；
- ✅ `INatterPlugin` / `IPluginContext` / `IContributionRegistry`；
- ✅ 依赖图 + host API 检查 + 激活回滚；
- ✅ 配置 schema + secret provider + 权限策略；
- ✅ `plugins list --detail` / `plugins graph` / `plugins doctor` / `plugins contributions` / `plugins permissions` / `plugins config validate`；
- ✅ 迁移 Greetings/Metrics 示例（legacy 类保留用于 Android 嵌入兼容）。

### Phase 2：Discord transport（核心已交付）

> 详细设计见 [Discord Transport Plugin Design](DISCORD_PLUGIN_DESIGN.md)。

- ✅ `natter.transport.messaging.discord` 示例插件；
- ✅ Discord Gateway/REST 抽象 + Discord.Net transport；
- ✅ NLMP 消息/编辑/删除/interaction 映射；
- ✅ 能力、allowlist、分片、限流、安全；
- ✅ `discord status`；
- ✅ fake transport 测试；
- 待补：附件上传、按钮/Modal、stream edit、全局命令注册策略。

### Phase 3：AI / feature（核心已交付）

- ✅ DeepSeek `chat.responder` 插件；
- ✅ 内存会话历史（进程内，重启丢失）；
- ✅ `IChatClient` / fake client / fake HTTP 测试；
- ✅ thinking 默认关闭、reasoning_content 隔离、错误降级、usage 指标；
- 待补：真实 DeepSeek + 真实 Discord 联合验证、流式输出、tools、持久化会话、多 persona。

### Phase 4：管理能力（未交付）

- 启用/禁用；
- 重载/更新；
- 插件市场；
- 签名/来源；
- out-of-process 插件宿主。

---

## 17. 开放问题

- 插件本地 DI 容器的具体实现（child container / provider 隔离）；
- `chat.responder` 的优先级与多 responder 路由策略；
- 可选依赖缺失时的 contribution 降级规则；
- 配置热更新；
- 插件间服务调用是否允许；
- host API 版本豁免；
- 插件签名与信任链；
- out-of-process 插件协议；
- Android 插件加载策略：双发行形态基础实现已交付，见 [Android 插件宿主设计](ANDROID_PLUGIN_HOST.md)；dynamic 形态关闭 AOT/Trim，仅加载签名托管插件。
- 插件市场与 bundle（组合包）模型；
- AI provider contribution / host `ai.complete` port / 多 provider 路由。

---

## 18. 本仓库实现（Phase 1）

### 18.1 交付范围

| 路线阶段 | 状态 | 说明 |
| --- | --- | --- |
| Phase 1：规范与内核 | **已实现** | manifest v1、Host API、contribution、依赖图、配置 schema、秘密、权限、激活回滚、诊断 CLI、示例迁移 |
| Android 插件宿主 | **基础实现已交付** | embedded 默认；dynamic extended store/校验/安装/回退与构建配置见 [Android 插件宿主](ANDROID_PLUGIN_HOST.md)；管理 UI 与真机矩阵待补 |
| Phase 2：Discord transport | **核心已交付** | `Natter.Chat.Discord` + `samples/Natter.Sample.Plugin.Discord`；消息、typing、编辑/删除、slash interaction、状态命令；真实网关/真实凭据集成测试待补 |
| Phase 3：AI / feature | **核心已交付** | `Natter.Feature.Ai.DeepSeek` / `Natter.Feature.Ai.Spark`：M.E.AI `IChatClient`、shared responder/context/memory、gate、幂等、状态命令；流式/tools/持久化待补 |
| 第二上游/消费者验证 | **代码与 fake/HTTP 交叉验证已交付** | `Natter.Chat.DingTalk` + `Natter.Feature.Ai.Spark`；真实钉钉/星火凭据未接入 |
| Phase 4：管理能力 | 未交付 | 热禁用/重载、市场、签名、out-of-process 宿主 |

### 18.2 代码映射

| 规范章节 | 实现位置 |
| --- | --- |
| §4 Manifest / §4.4 依赖 / §4.5 Host API | `src/Natter.Plugin.Abstractions/Manifest/*`、`SemanticVersion.cs`、`VersionRange.cs` |
| §4.6 权限 / §4.7 秘密 / §4.8 配置 | `src/Natter.Plugin.Abstractions/Contributions/PluginPermissions.cs`、`Configuration/SecretProviders.cs`、`Configuration/PluginConfigSchema.cs` |
| §5 Host API / INatterPlugin / IPluginContext | `src/Natter.Plugin.Abstractions/Contributions/PluginContracts.cs`、`NatterPluginApi.cs` |
| §5.5 Contribution 注册 | `src/Natter.Infrastructure/Plugins/Nps/NpsContributionRegistry.cs` |
| §6 Contribution 类型 | `PluginContributionTypes.cs`；`chat.upstream` / `chat.responder` / `chat.event-subscriber` / `cli.command` / `port.adapter` |
| §7 依赖图与激活 | `src/Natter.Infrastructure/Plugins/Nps/NpsPluginGraph.cs`、`NpsPluginRuntime.cs` |
| §7 激活回滚 / 停止顺序 | `NpsPluginRuntime.RollbackCandidateAsync`、`StopCandidateAsync` |
| §8 配置加载 | `src/Natter.Infrastructure/Plugins/Nps/NpsPluginConfigLoader.cs` |
| §9 权限执行 | `NpsPermissionPolicy`、`NpsPluginSecrets`、contribution registry |
| §10 诊断 CLI | `src/Natter.Cli.Core/Commands/PluginRuntimeCommands.cs`、`PluginsListCommand.cs`、`DoctorCommand.cs` |
| §13 legacy 兼容 | `PluginLoader`（无 `schemaVersion`）、`LegacyPluginManifest`、NPS 入口指向 `IPluginModule` 的 adapter 路径 |
| 示例迁移 | `samples/Natter.Sample.Plugin.Greetings`、`samples/Natter.Sample.Plugin.Metrics`（NPS 类 + legacy 类供 Android 嵌入） |
| Android 动态宿主 | `src/Natter.Plugin.Store`、`AndroidPluginHost`、`AndroidKeystoreSecretProvider`、`DynamicPlugins` 构建；默认 embedded 已迁移 NPS |
| Discord transport | `src/Natter.Chat.Discord` + `samples/Natter.Sample.Plugin.Discord`；Discord.Net transport、NLMP 映射、slash interaction、fake transport 测试 |
| AI responder | `src/Natter.Feature.Ai.DeepSeek`：M.E.AI `IChatClient`、DeepSeek HTTP adapter、responder、history/gate、`ai status` |
| 共享 AI 边界 | `src/Natter.Feature.Ai`：`AiChatResponder`、`IAiContextAssembler`、`IAiConversationStore` |
| Host 会话 | `IConversationStore` + SQLite；插件凭 `storage` 权限注入 |
| Profile 路由 | `RuntimeProfile` / `ProfileAwareChatResponder`；provider 由 `pluginId#responderId` 决定 |
| P3 contribution | `prompt.provider` / `context.assembler` / `memory.store` 类型、NPS 注册/激活/回滚与 host catalog 已实现；`prompt.provider` / `prompt.fragment` / `context.assembler` 要求 `chat.read`；`Natter.Sample.Plugin.Memory` 为 `memory.store` 示例 |
| P6.2 embedding/rerank | `embedding.provider` / `rerank.provider` 贡献、`owner#id` 目录、Stop/Rollback/Dispose 撤销、`network`+`secrets:*` 门控、分批/截断/缓存/全或无失败工具；参考实现 `plugins/Natter.Plugin.AiEmbedding.OpenAi` |
| 第二上游/消费者 | `src/Natter.Chat.DingTalk`、`samples/Natter.Sample.Plugin.DingTalk`、`src/Natter.Feature.Ai.Spark` |

### 18.3 一致性测试映射（对应 §14）

| §14 项 | 测试 |
| --- | --- |
| 1 manifest schema/未知字段/路径 | `NpsManifestTests.Manifest_reader_*` |
| 2 host API 不兼容 | `NpsGraphTests.Host_api_mismatch_is_rejected` |
| 3 缺失/版本/循环/冲突 | `NpsGraphTests` |
| 4 loadAfter/loadBefore 排序 | `NpsGraphTests.Dependencies_and_order_constraints_produce_a_deterministic_order`、`Load_before_is_respected` |
| 5 可选依赖缺失 | `NpsGraphTests.Optional_dependency_missing_does_not_block` |
| 6 重复插件/贡献 id | `NpsRuntimeTests.Duplicate_plugin_ids_fail_both_descriptors`、`Duplicate_contribution_ids_fail_the_later_plugin` |
| 7 config schema/default/JSON Pointer | `NpsManifestTests.Config_schema_*`、`NpsRuntimeTests.Valid_config_*`、`Invalid_config_*` |
| 8 秘密缺失/权限/脱敏 | `NpsRuntimeTests.Missing_required_secret_*`、`Required_secret_*` |
| 9 Configure 无副作用 | 约定与代码审查项；`INatterPlugin.ConfigureAsync` 只提供注册/校验 API |
| 10 激活回滚/依赖方 Blocked | `NpsRuntimeTests.Activation_failure_*` |
| 11 上游 Connect/Disconnect 宿主驱动 | `NpsRuntimeTests.Chat_upstream_contribution_*` |
| 12 停止顺序逆依赖 | `NpsRuntimeTests.Stop_order_is_the_reverse_of_activation_order` |
| 13 禁用/启用 | `NpsRuntimeTests.Disabled_plugin_*`（启用/禁用 CLI 属 Phase 4） |
| 14 legacy 适配 | `NpsRuntimeTests.Legacy_manifest_*`、`Nps_manifest_can_point_at_a_legacy_plugin_module` |
| 15 Discord fixture | `DiscordTransportTests`、`DiscordNpsTests`、`DiscordConfigTests`（fake transport，不连接真实 Discord） |
| 19 AI responder | `DeepSeekChatClientTests`、`DeepSeekResponderTests`、`DeepSeekPluginTests`（fake `IChatClient` / fake HTTP，不调用真实 DeepSeek） |
| 16 graph/contributions/permissions JSON | `NpsCliTests.Plugin_graph_*` |
| 17 卸载无残留 | `NpsRuntimeTests.Chat_responder_and_event_subscriber_*`（停止后订阅失效）、端口回退测试 |
| 18 路径逃逸/非法入口类型 | `NpsManifestTests`、`NpsRuntimeTests.Missing_entry_type_*` |

### 18.4 实现取舍与边界

1. **入口构造函数**：v1 运行时可调用公共无参构造函数；插件内部服务通过插件本地容器做构造注入（`IPluginServices.Collection`），宿主公开合约由插件 provider 注入。带参数的入口构造函数留到后续版本。
2. **插件本地容器**：每个插件一个 `ServiceProvider`；目前是 singleton 范围，不模拟 per-scope 容器。插件不能访问宿主根 `IServiceCollection`/`IServiceProvider`。
3. **启用/禁用**：通过 `NpsPluginOptions.DisabledPlugins` 或 host profile 控制；`plugins enable/disable` CLI 属 Phase 4。
4. **权限执行点**：已覆盖 secrets、contribution 注册、CLI/聊天贡献；`network`/`filesystem.*`/`ai.complete`/`storage` 是治理声明，v1 不做进程内沙箱。
5. **端口适配器**：宿主 DI 中注册 late-bound 路由；插件激活时使用插件实现，失败/停止后回退宿主默认绑定。
6. **配置 default**：只在内存中的 effective config 补全，不写回 `config.json`。
7. **未知字段**：v1 加载器拒绝未知 manifest 字段，便于发现拼写错误。
8. **未实现**：Discord/AI/远程市场/HMR/跨进程插件宿主/配置热更新/启用禁用 CLI，均按 §16 后续阶段处理。
9. **上游连接时机**：每个插件按拓扑顺序 Activate 后立即由宿主执行其 `chat.upstream.ConnectAsync`；连接失败只影响该插件和尚未激活的依赖方，避免已激活依赖方被“事后阻塞”。
10. **插件 provider 的权限面**：`IChatCommandDispatcher` 对插件按 `chat.send/edit/delete/interactions` 包装；`IChatEventSource` 仅在授予 `chat.read` 时注入；`IChatUpstreamRegistry`/`IChatResponderRegistry` 不暴露给插件。
11. **包卫生**：加载 NPS 插件前检查插件目录中的 `.pfx/.p12/.key/.keystore/.jks/.pem`、`.env`、token/credentials 等文件并拒绝加载；这不替代 secret manager。

### 18.5 最小用法

```csharp
public sealed class MyPlugin : INatterPlugin
{
    public ValueTask ConfigureAsync(IPluginContext context, CancellationToken ct = default)
    {
        context.Services.Collection.AddSingleton<MyHandler>();
        context.Contributions.AddCliCommand(new CliCommandDescriptor
        {
            Name = "my command",
            HandlerFactory = services => services.GetRequiredService<MyHandler>(),
        });
        return ValueTask.CompletedTask;
    }

    public ValueTask ActivateAsync(IPluginContext context, CancellationToken ct = default)
        => ValueTask.CompletedTask;

    public ValueTask DeactivateAsync(IPluginContext context, CancellationToken ct = default)
        => ValueTask.CompletedTask;
}
```

```bash
natter plugins list --detail
natter plugins graph --json
natter plugins contributions --json
natter plugins permissions --json
natter plugins doctor natter.sample.port.greetings
natter plugins config validate natter.sample.port.greetings
```

## 19. 参考资料

- [NLMP 轻量消息协议](NLMP.md)
- [当前插件开发指南](PLUGINS.md)
- [架构设计](ARCHITECTURE.md)
- DSH：Cordis loader、插件配置 schema、peer dependency、HMR、plugin manager、version exemption
- RimWorld：`About.xml`、`packageId`、`modDependencies`、`loadAfter` / `loadBefore`、`supportedVersions`、Harmony/Defs/Patches

---

## 16. P6.7 破坏性变更：`memory.store` 从 `IMemoryStore` 改为 `IMemoryEntryStore`

> **影响面**：所有实现 `memory.store` 的第三方插件。
> **性质**：host API 破坏性变更。**二进制与源码均不兼容**，插件必须重新编译。

### 16.1 为什么改

v1 的 `IMemoryStore` 是单层扁平模型（`kind` 自由字符串 + `expires_at` 硬过期）。
v2 是五类分层（`Policy` / `Preference` / `Fact` / `Episodic` / `Trace`），核心是**每条记忆有自己的稳定度 S 与遗忘曲线**，以及**归档而不是删除**。
两者不是加字段能兼容的关系：`expires_at`（到点消失）与 `archived`（仍可检索）是互斥语义。

### 16.2 旧契约 → 新契约

| v1 | v2 |
| --- | --- |
| `AddMemoryStore<TStore>(id)` where `TStore : IMemoryStore` | where `TStore : IMemoryEntryStore` |
| `WriteAsync(MemoryWriteRequest)`（`kind: string`） | `WriteAsync(MemoryEntryWriteRequest)`（`MemoryType` 五枚举） |
| `SearchAsync(MemoryQuery)` | `SearchAsync(MemoryEntryQuery)`（多 `Types` 过滤、`IncludeArchived`） |
| `DeleteAsync(MemoryDeleteRequest)`（硬删） | `ArchiveAsync(source, conversation, user, id)`（归档，可恢复） |
| `ExportAsync/ImportAsync`（v1 信封） | 同签名（v2 信封，`MemoryExportVersions`） |
| — | `GetAsync` / `ReinforceRecallAsync`（新增） |

`MemoryItem` → `MemoryEntry`：`Text` → `Content`，`MemoryId` → `Id`，新增
`Type` / `ContentHash` / `StabilityDays` / `ReinforceCount` / `Archived` / `SupersedesId` / `LastRecalledAt`。

### 16.3 第三方迁移清单

```text
1. 实现 IMemoryEntryStore（7 个成员，见上表）
2. kind 字符串改成 MemoryType：policy / preference / fact / episodic / trace
3. 删除改为归档：DeleteAsync → ArchiveAsync；不要再实现物理删除
4. 导出格式升级到 MemoryExportVersions.Current（=2）；读旧导出用 MemoryExportBridge.Upgrade
5. 重新编译并对 `hostApi` 范围做版本约束（旧 host 仍只认 v1）
```

参考实现：`samples/Natter.Sample.Plugin.Memory/MemoryStorePlugin.cs`（JSON 文件 + v1 文件自动升级）。

### 16.4 v1 保留期

`IMemoryStore` / `MemoryItem` / `MemoryQuery` / `MemoryWriteRequest` / `MemoryDeleteRequest` / `MemoryExport*` **仍在**，标记
`[Obsolete("v1 memory store retired at P6.7; use IMemoryEntryStore")]`，并保留一个 release 供回滚。
宿主仍注册 v1 store（`ChatCompositionModule`），但**recall 路径已全部切到 v2**；不要在 v1 上做新的开发。
