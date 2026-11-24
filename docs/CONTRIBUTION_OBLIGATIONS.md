# NPS 贡献类型义务清单

> **目的**：把 11 个贡献类型的义务从「注释里的惯例」变成**可检查的契约**。Satori 那个 bug 的本质不是 Satori 的错——是 `chat.upstream` 契约没规定必须分片，于是它可以「合法地」不分片。
> **基线**：`e93527a`（2026-11-24）。**方法**：只读源码 + 既有测试；每条「当前强制」都在源码里核过。
> **维护**：新增贡献类型必须在此登记一行；把某条义务机制化后，回填第 5 列。

---

## 0. 一句话结论

11 个类型里：
- **4 个**在契约层写了义务注释（`prompt.fragment`、`memory.store`、`embedding.provider`、`rerank.provider`）；
- **11/11** 有「manifest 必须声明」强制；**10/11** 有「权限必须授予」（`port.adapter` 缺，本轮已改成**显式为空**而非默认漏掉）；
- **真正做完整的一条**是 `memory.store` 的导出导入版本策略（策略类 + 两端调用 + 测试 + 错误码）——它证明「声明义务」与「有保障」可以同时成立；
- **其余业务义务（分片、幂等、耗时、生命周期语义）仍靠惯例**，详见 §2 与 §3。

一句话：**约束层（声明/权限）覆盖得不错；行为层（做了没有、做对没有）几乎空白。**

---

## 1. 对照表（11 行 × 5 列）

| 类型 | ① 实现方必须保证 | ② 可依赖宿主 | ③ 失败/降级语义 | ④ 声明但未履行 | ⑤ **当前靠什么强制** |
| --- | --- | --- | --- | --- | --- |
| `chat.upstream` | **声明上限时**必须按上限分片；未声明则**显式放弃**且可观测；内部幂等；source 匹配 | 宿主做事件去重、命令校验、能力降级 | 未连接 → `unavailable` ack；映射失败 → `unsupported` | **超长消息被平台整条拒收**（Satori 实例，已修 `e90abd6` + 告警）；重复发送 | manifest + `chat.read`/`chat.send`；分片：**源码棘轮 + 条件义务文档**（§4.2）；放弃留痕：仅 Satori 有 |
| `chat.responder` | 返回非空文本或明确失败；尊重并发 gate | 宿主串联多个 responder（priority 顺序）、做预算裁剪 | 抛异常 → 宿主回退文案；空响应 → `EmptyResponseText` | 用户看到「AI 不可用」而实际是自己的实现坏了 | manifest 声明 + `chat.read`/`chat.send`；**响应质量：无** |
| `chat.event-subscriber` | 快速返回（热路径）；自行处理异常 | 宿主提供事件流并做订阅隔离 | 抛异常 → 只影响该订阅者（Rx 隔离） | 慢订阅者拖慢事件流 | manifest 声明 + `chat.read`；**耗时：无** |
| `cli.command` | 退出码语义正确；输出可 JSON 化；不写审计正文 | 宿主做参数解析、Console 重定向、输出截断 | 抛异常 → 非零退出码 | 自动化脚本误判成功 | manifest 声明 + `cli.commands` |
| `port.adapter` | 实现接口语义；线程安全（Singleton）；不越过端口边界 | 宿主提供端口契约与 DI 容器 | 解析失败 → 回退到内置实现并记诊断 | 静默替换宿主端口行为 | **仅 manifest 声明**（唯一不做权限检查的类型，见 §3.5） |
| `prompt.provider` | 返回完整 PromptBundle；不注入未经配额检查的内容 | 宿主做编译、排序、配额、hash | 抛异常 → 该 provider 被跳过 | 片段超配额被静默裁剪 | manifest 声明 + `chat.read`；**配额：宿主侧强制** ✓ |
| `prompt.fragment` | 片段 id 用局部名（宿主加前缀）；不冒充 `HostBuiltin` | 宿主聚合、清理、命名空间、拒绝 HostBuiltin 冒充 ✓ | 违规 id → 拒绝注册 | 与宿主内置片段冲突 | manifest 声明 + `chat.read` + **宿主命名空间/冒充检查** ✓ |
| `context.assembler` | 遵守预算；不越权读未授予的数据 | 宿主提供 pipeline、预算、诊断 | 抛异常 → 回退到默认 assembler | 上下文体量失控 | manifest 声明 + `chat.read`；**预算：部分**（宿主裁剪 ✓，assembler 自控无强制） |
| `memory.store` | 实现 v2 五类契约（`IMemoryEntryStore`）；导入时校验 `schemaVersion` | 宿主提供 store 解析、迁移诊断 | store 不可用 → 空召回并记 `migrationWarning` | 旧宿主盲目导入新格式数据 | manifest + `storage`；**导入版本：宿主+实现+测试三层强制 ✓**（`EnsureReadable`，§3.4） |
| `embedding.provider` | 声明 `MaxBatchSize`/`MaxInputTokens` 并据此分批；**声明 network 与 secrets 权限** | 宿主提供端口、缓存、分批工具 | 超时 → 可识别错误码，不无限重试 | 超批 → provider 直接报错 | manifest 声明 + **仅 `network`**（`secrets:` 只在注释里，见 §3.3） |
| `rerank.provider` | 同上；`MaxCandidates` 决定候选上限 | 同上 | 同上 | 同上 | 同上 |

---

## 2. 「当前靠什么强制」的统计

| 强制层 | 覆盖类型数 | 说明 |
| --- | --- | --- |
| manifest 声明（`Require`） | **11 / 11** | 声明了没注册 → `FindMissingContributions` 报缺失 |
| 权限校验（`RequirePermissions`） | **10 / 11** | 缺 `port.adapter`（本轮改为**显式空清单 + 注释**，未知类型抛错） |
| 宿主侧语义强制（命名空间/冒充/配额） | **2 / 11** | `prompt.fragment`（命名空间 + HostBuiltin 冒充）、`prompt.provider`（配额） |
| 测试强制 | **3 / 11** | `chat.upstream` 分片（源码棘轮 + 条件义务）、`memory.store` 版本与契约测试、所有类型的**权限策略完整性**（§4.1） |
| **业务义务仍无强制** | **6 / 11** | `chat.responder`、`chat.event-subscriber`、`cli.command`、`port.adapter`、`context.assembler`、`embedding.provider`/`rerank.provider` 的 secrets 一半 |

> 结论：**「有声明义务」和「有保障」是两件事**。11 个类型全部有「manifest 声明」这一层，所以很容易形成「已经有契约了」的错觉，而真正的业务义务（分片、幂等、版本、生命周期、耗时）大部分没有强制。

---

## 3. 逐个类型详述（只写有信息量的部分）

### 3.1 `chat.upstream` — 分片义务是**条件的**，不是「必须有上限」

```csharp
// src/Natter.Chat/Ports/IChatUpstream.cs（改造前）
/// <summary>Executes one command and returns an ack. Adapters apply idempotency internally.</summary>
```

**幂等写成了义务，分片一个字没提** —— 这是 Satori「合法地」不分片的直接原因。

但这轮查证发现：**不能简单要求「必须声明上限」**。原因是协议形态不同：

| 适配器 | 上限来源 | 上限是否协议常量 |
| --- | --- | --- |
| Discord | Discord 平台 2000（默认 1900 留余量） | 是 |
| DingTalk | 钉钉 2000 | 是 |
| **Satori** | **取决于网关背后的平台**（Discord 2000 / QQ / Telegram 4096），由 `Satori-Platform` header 标明 | **否——聚合协议，没有统一上限** |

所以正确契约为**条件义务**：

```text
声明了上限（MaxMessageLength 非 null） → 必须按该上限分片（硬义务，可验证）
未声明上限（显式 null）               → 视为「明确放弃该义务」，且必须可观测（日志/status）
```

Satori 的落地方式（`e90abd6` + gateway 后续提交）：

- 默认值 `SatoriCapabilityOptions.DefaultMaxMessageLength = 1900`（**不是 2000**：`content` 是转义后的元素串，mention/attachment 会增长，需留余量）；
- 显式 `null` 仍允许 = 「本部署的平台上限未知」；
- 构造时 + 每次 login features 刷新后都检查，`Interlocked.Exchange` 保证同一状态只告警一次，切回非 null 时**重新武装**；
- 告警文案直接说明后果（「超长回复会被整条拒收」）与修法（设 `MaxMessageLength`）。

> **文案陷阱（已修）**：`natter upstreams` 曾把 `null` 显示成 `unknown (adapter chunks)`——「adapter chunks」是假的，因为 null 恰好意味着**不分片**。已改为 `unknown (chunking disabled)`。这类文案会把缺陷读成特性，属于第 3 列「失败语义」的实质错误。

**当前强制**：
- 调用点棘轮：`Every_chat_upstream_that_sends_text_calls_the_chunker`（源码扫描，事后）；
- 分片上限的**有效值**没有任何编译期或启动期检查——但**放弃义务必须留痕**这一点，Satori 已经做到（日志），宿主尚未统一。

### 3.2 `Capabilities.MaxMessageLength` 可空：这是设计，不是漏洞

```csharp
// src/Natter.Chat/Model/Capabilities.cs:13
public int? MaxMessageLength { get; init; }        // 默认 null = 上限未知

// src/Natter.Chat/Degradation/ChatTextChunker.cs
if (maxMessageLength is not { } limit || limit <= 0)
    return [content];        // ← 不分片
```

`tests/Natter.Framework.Tests/NlmpModelTests.cs:38` 用 `Assert.Null(defaults.MaxMessageLength)` 锁住了这个默认值。

**对 task-74 的定性（修正）**：Satori 的修复**默认生效**（`SatoriOptions.MaxMessageLength` 默认 1900），「修了但不生效」**不成立**。
但存在一条**合法的绕过路径**：部署显式把 `MaxMessageLength` 设为 `null` → 分片关闭 → 超长消息被平台整条拒收。**这本身可接受**（聚合协议确实可能不知道上限），**不可接受的是静默发生**——Satori 已加告警，宿主侧还没有统一要求。

### 3.3 `embedding.provider` / `rerank.provider` — 注释比实现严

契约注释（`IContributionRegistry.AddEmbeddingProvider`）：

> providers must declare `network` **and a `secrets:` permission**

实现（`NpsPluginOptions.RequiredPermissions`）：

```csharp
PluginContributionTypes.EmbeddingProvider or PluginContributionTypes.RerankProvider =>
    [PluginPermissions.Network],      // ← 只有 network
```

**`secrets:` 是写在文档里的义务，没有任何代码检查。** 一个 embedding provider 可以只用环境变量、不声明 `secrets:`，照样激活。

### 3.4 `memory.store` — 版本义务**已经是机制**（本轮修正了自己的初判）

初次核对时我看的是 `MemoryExport` / `MemoryImportRequest` / `IMemoryStore`——**这三个都已 `[Obsolete("v1 memory store retired at P6.7")]`**，那条导出导入路径已退役，不该作为义务依据。

生产路径 v2（`IMemoryEntryStore`）**已经**把版本做成机制：

```csharp
// MemoryEntryContracts.cs
public const int Current = 2;                 // 本 build 写的版本
public const int MinimumReadable = 1;         // 还能读的最老版本
public static void EnsureReadable(int version)   // 不夹紧、不静默降级
    => ... throw new MemoryExportVersionException(version);
```

两个实现都在读取条目**之前**调用它：

```csharp
// SqliteMemoryEntryStore.cs:388 / InMemoryMemoryEntryStore.cs:220
export.EnsureReadable();
```

并有既有测试（`MemoryRetrievalTests.cs:682-690`：`IsReadable` / `IsTooNew` / 构造 too-new envelope）。

**结论**：`memory.store` 的版本义务**不需要本轮补**——它是 11 个类型里做得最完整的（版本策略类 + 两端调用 + 测试 + 明确的错误码 `memory.export_version_too_new`）。这也是本文 §0 说的「有声明义务 ≠ 有保障」的反例：**这里两者都有**。

### 3.5 `port.adapter` — 唯一不做权限检查的类型

```csharp
// AddPortAdapter（NpsContributionRegistry.cs:189）
Require(PluginContributionTypes.PortAdapter, id);
_services.Collection.TryAdd(...);          // ← 没有 RequirePermissions
```

其余 10 个类型都是 `Require` + `RequirePermissions` 两行。**结论**：一个零权限插件可以覆盖宿主端口（例如替换 `IChatResponder` 的实现）。这是本轮盘点发现的**第二处真实不一致**（第一处是 §3.3 的 secrets）。

### 3.6 `port.adapter` 的生命周期语义

10 个类型里 9 个硬编码 `ServiceLifetime.Singleton`（注册表 `Stage(...)` 调用点），只有 `port.adapter` 把 `lifetime` 开放给插件。

`Scoped` 在 `NpsPortAdapterRouter.cs:109` 有真实处理（`if (active.Lifetime == ServiceLifetime.Singleton)`）。所以**语义是存在的，缺的只是「什么时候必须用 Scoped」的规则**。这是文档缺口，不是机制缺口（见 §5 的「不机制化」决定）。

---

## 4. 本轮把哪些义务变成了机制

按「缺失后果最严重 + 机制化成本低」选了 **2 项**。

### 4.1 【已机制化】贡献类型的权限要求：从「静默默认零」变成「必须显式声明」

**缺口**：`NpsPermissionPolicy.RequiredPermissions` 原来的 switch 以 `_ => []` 收尾——**任何未列出的类型（含 `port.adapter`，以及未来的新类型）静默获得零权限要求**。这正是 `port.adapter` 至今不受权限检查的原因：不是决定过「不需要」，是没人列它。

**改动**：
1. 11 个类型全部显式列出；`port.adapter => []` 由「遗漏」变成**有注释的决定**（原因：覆盖宿主端口是装配行为，现有声明者只有随宿主分发的 bundled 示例）；
2. 未知类型**抛 `ArgumentOutOfRangeException`**，不再是 `[]`。

**效果**：新增贡献类型时，「忘记写权限策略」从**静默零权限**变成**测试/激活期报错**；同时把 §3.5 的不一致从「隐性」变成「显性」。

**负向验证**（成对）：
- `Every_contribution_type_declares_its_permission_policy`：遍历 `PluginContributionTypes.All`，每个都必须能取到策略（若有人删掉某一行，这里红）；
- `Unknown_contribution_type_throws_instead_of_defaulting_to_no_permissions`：喂 `"unknown.type"` 必须抛异常（若有人把 `_ => []` 加回去，这里红）。

**为什么这算「机制」而不是「文档」**：它改变了**添加新类型时必须做的动作**——从「什么都可以不做」变成「不写就炸」。

### 4.2 【已机制化】`chat.upstream`：分片义务的两个分支都变得可判定

**改动**（契约层，不改行为）：
1. `IChatUpstream.ExecuteAsync` / `AddChatUpstream` 的文档补齐**条件义务**：声明上限 → 必须按上限分片；显式 null → 放弃义务且必须可观测；
2. 新增契约测试 `Chunking_obligation_has_no_third_state`：对每个 `IChatUpstream` 实现，必须能归入两类之一——**调用 chunker**，或**在其能力配置里显式声明放弃**（`MaxMessageLength` 可空且注释/常量表明是放弃）。无法归类即失败。

**为什么不做「强制非空」**（见 §5.1）：聚合协议下非空就是逼适配器编假值，反而制造第二类谎言。

**负向验证**：把一个适配器的默认上限改成 `null` 且不做任何放弃声明 → 测试红；改动只在测试夹具里做，不触碰 gateway 的 `Natter.Chat.Satori` 工作区。

## 5. 我认为**不应该**机制化的义务（附理由）

### 5.1 `Capabilities.MaxMessageLength` 改成非空（候选 A 的激进版本）

**不做，而且要明确反对**。理由：

1. **对聚合协议，这个值客观不存在**。Satori 网关后面可能是 Discord(2000)/QQ/Telegram(4096)，`Satori-Platform` header 才决定真实上限。强制非空 = 逼适配器**编一个假值**——用一个新的谎言（假上限）替换旧的缺陷（静默不分片）。
2. `null` 在契约里有真实语义：「本部署上限未知」。改成 `int` 会把「未知」压成 0，而 0 在 `ChatTextChunker` 里等于「不限制」，语义反而更糊。
3. 真要「不可能忘记」，代价是 ABI 变更（`Capabilities.MaxMessageLength` + `SatoriCapabilityOptions` 跟随调整）+ 迁移清单 + 打破 `NlmpModelTests:38` 锁定的默认值——而收益已被 §4.2 的条件义务覆盖。
4. **正确的机制形状是「显式放弃 + 可观测」**，Satori 已经做成范式（默认 1900 / 显式 null 会告警 / 状态翻转重新武装）。下一步应该把这个范式抬到**宿主统一要求**（见 §6），而不是改类型。

### 5.2 `port.adapter` 的生命周期义务（候选 C）

**只写文档，不机制化**（已写进 §3.6 + PLUGIN_SPEC）。理由：9/10 类型已硬编码 Singleton，`Scoped` 的实际语义由 router 实现且已有代码路径；「什么时候该用 Scoped」是**设计选择**而非可判定的事实，写成测试只能assert「用了 Scoped」而不能 assert「用对了」。**规则上机器判不了，就别装成能判。**

### 5.3 `chat.responder` 的「响应质量」、`chat.event-subscriber` 的「快速返回」

**不机制化**。质量与耗时没有契约可言的阈值（不同 provider 差异巨大），强行设限会变成假约束。这两个义务的合理归宿是**可观测性**（`health` 里的 p50/p95 与错误率），不是准入检查。

### 5.4 `port.adapter` 补权限检查（§3.5 的不一致）

**这轮只记录，不改**——因为补上检查会改变现有插件的激活结果。实际盘点：
`samples/Natter.Sample.Plugin.Greetings` 声明了 `port.adapter`，但权限只有 `cli.commands`；
`Metrics` 只声明 `cli.command`（不涉及）。也就是说**一旦要求权限，Greetings 会立即激活失败**。
所以这是一个需要你拍板的**行为变更**，两种走法：
（a）为 `port.adapter` 定义独立权限词（如 `port.override`），并在 bundled/sample 信任级默认授予——补上检查且不破坏现有插件；
（b）直接要求 `chat.read`——语义不对（覆盖端口与读会话无关）。
**建议 (a)**。这轮不动，等你决定。

---

## 6. 与既有文档的关系

- 契约的**规范面**（manifest 字段、权限闭集、激活流程）见 [PLUGIN_SPEC.md](PLUGIN_SPEC.md)；本文只负责**义务与强制**这一层。
- 本文 §4 的机制化项在 `PLUGIN_SPEC.md` 的「贡献类型最低权限」表后补了一节「实现方义务」指向本文。
- 新增贡献类型时：先在此表加一行（含第 5 列），再写契约——**第 5 列留空即视为「无强制」，需要在评审里说明为什么可以接受**。
