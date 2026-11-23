# 架构与一致性审查（2026-11-23）

> **基线**：`HEAD=9874f69`（2026-11-23 16:57），工作区干净。
> **范围**：架构与一致性（命名/语义、分层/依赖、重复/双轨、错误语义/可观测性）。**不是安全审查**——安全项已由 S1/S2/S3 批次收口，见 `CODE_REVIEW_2026-11-22.md` 与 `CODE_REVIEW_S3_TRIAGE.md`。
> **方法**：只读。全部结论来自 `src/`、`plugins/`、`samples/`、`scripts/`、`docs/` 的静态阅读与 `grep`；本轮**未改任何代码**，未部署、未重启、未 commit。
> **本文只含第一部分**（审查清单 + 整改建议 + 优先级）。第二、三部分（文档合并归档、命令实跑校验）待 lead 确认优先级后再做，预览见 §7。
> **判定口径**：`风险等级` 指「改动代价 × 不修的后果」，不是安全评级。

---

## 0. 结论摘要

| 类别 | 项数 | 进入整改清单 | 说明 |
| --- | --- | --- | --- |
| A 命名与语义一致性 | **6** | 6 | 2 项是硬伤（状态/语义撒谎 A-02、A-01），4 项是不一致 |
| B 分层与依赖 | **5** | 2 | 无环（已核）；B-01/B-02 待改，B-03/B-04/B-05 随 B-02 或延期 |
| C 重复与双轨 | **7** | 7 | 最大一块：记忆 v1/v2 双轨、5 份脱敏、配置默认值两套约定 |
| D 错误语义与可观测性 | **5** | 3 | D-03 已在 lead 侧修好且无同类；D-05 延期 |
| **合计** | **23** | **18** | 另 5 项（B-03/B-04/B-05/D-03/D-05）记为「记录并接受」或随依赖项一起做 |

一句话：**没有 P0 级架构缺陷**（无环、无跨层调用崩溃点、无安全回退）。真正该先做的是「消除会误导人的语义」——记忆双轨、`IsAvailable`、`ai status` 的可观测缺口。这三类都不改行为，但会持续制造错误判断。

已核实为**正面结论**（写在这里避免下轮重复怀疑）：
- **依赖图无环**（脚本对 `src/`+`plugins/`+`samples/` 全量 `ProjectReference` 做 DFS）。
- `Directory.EnumerateFiles` 惰性枚举报错（lead 修的 `PluginPackageInspector` 那类）**没有第二处启动级同类**，详见 D-03。
- 适配器分片统一走 `ChatTextChunker`（4 个调用点，无绕过）；S3-12/13 已修（`8e1fb04`）。

---

## 1. A 类 · 命名与语义一致性

| ID | 位置 | 现状 | 建议 | 风险 |
| --- | --- | --- | --- | --- |
| **A-01** | `Application/Chat/Context/ContextContracts.cs:175-185` | `MemoryQuery.DecayHalfLife` 名字是「半衰期」，但**代码注释自己承认语义已经要变**：v1 生效的是 `LegacyRetention`（`exp(-age/S)`，30 天保留 0.368），v2 将变成「保留率 0.9 的稳定度区间」。注释原文：*"The name is kept only because 61 call sites reference it; it is a naming debt to fix when v1 retires."* | 与 C-01/C-02 一起处理：v1 退役时同步改名 `StabilityWindow` 并一次性替换调用点。**不要**在语义迁移前改名（会让 v1 读者误解）。 | 中（当前 v1 名字与实现一致，但注释已预告未来撒谎） |
| **A-02** | `plugins/Natter.Plugin.ConversationConsole/ConversationConsoleQueryGateway.cs:52`；`ConversationConsoleRequestHandler.cs:148` | `IsAvailable => _query is not null`。宿主在无 `--state-dir` 时注入 `EmptyConversationQuery`（`ChatCompositionModule.cs:111-116`），于是 **`/health` 报 `query.available=true`，而该实例结构上只能返回空页**。名字问的是「有没有数据」，实现答的是「端口有没有被授予」。 | 拆成两个诚实的字段：`granted`（端口是否注入）与 `dataAvailable`（是否接了 SQLite 实现）。health 里两个都报。名字建议 `IsGranted`。 | **高**（生产健康检查会给出误导性绿灯；这类「假可用」比报错更难查） |
| **A-03** | `scripts/stage-tier1-plugins.sh:67-70`；`plugins/Natter.Plugin.PromptLibrary/plugin.json` | 插件 id 前缀不统一：4 个是 `natter.plugin.*`，PromptLibrary 是 **`natter.prompt.library`**。schema 的 id 正则 `^[a-z0-9]+(?:[._-][a-z0-9]+)+$` 允许它，所以不是非法，只是**族名不一致**。 | 定一条规范（建议：所有 NPS 插件 `natter.plugin.<name>`），把 PromptLibrary 改 id 并同步 staging 表与发布包；或明确写下「非 plugin 族允许自定义前缀」并说明理由。 | 低（无功能影响，影响可发现性与运维 grep） |
| **A-04** | `scripts/stage-tier1-plugins.sh:67-70`（第 1 列 vs 第 3 列） | 产物目录名是**短名**（`webconsole/`、`conversation-console/`、`prompt-library/`、`state-vitals/`），而 manifest id 是全 id（`natter.plugin.webconsole`…）。`src/Natter.Cli` 的示例插件同理（`plugins/greetings`、`plugins/metrics`）。 | NPS 按目录扫描 + 读 manifest，短名能用；但「目录名 ≠ id」让「按 id 找目录」的排查方式失效。建议目录名=id（`plugins/natter.plugin.webconsole/`），或在 PLUGIN_SPEC 里明确写「目录名任意，唯一标识是 manifest.id」。 | 低 |
| **A-05** | `ConversationConsoleQueryGateway.cs:53`（`Source`） | `Source` 属性在端口缺失时返回字面量 `"unavailable"`——**用数据字段承载状态**，下游任何 `Source` 渲染都会把状态当来源打印。 | 改 `string? Source`（缺失时 null），状态由 A-02 的两个布尔承载。 | 低（当前只用于 health 展示） |
| **A-06** | `Application/Chat/Context/ContextContracts.cs:105,113`（v1）vs `MemoryEntryContracts.cs:27`（v2） | 同一领域概念两套词汇：v1 = `MemoryItem`/`MemoryScopeType`，v2 = `MemoryEntry`/`MemoryType`。v1 **没有任何 `[Obsolete]` 或「将被替代」标记**（只在注释里散落提及）。 | 与 C-01 一起：给 v1 打 `[Obsolete("superseded by IMemoryEntryStore (P6.7)")]`，让编译期就把双轨暴露给下一个人；或明确写「v1 是长期保留的兼容层」并删掉注释里的迁移承诺。 | 中（新读者会以为两套都该用） |

---

## 2. B 类 · 分层与依赖

**依赖图（实测，`ProjectReference` 全集）**：`Abstractions/Cli.Abstractions` 为叶；`Chat`/`Domain` 只依赖 `Abstractions`；`Application` 依赖 `Abstractions+Domain+Chat`；`Infrastructure` 依赖 6 个（含 `Cli.Abstractions`）；`Cli.Core` 依赖 6 个（含 `Infrastructure+Plugin.Store`）。**无环**。

| ID | 位置 | 现状 | 建议 | 风险 |
| --- | --- | --- | --- | --- |
| **B-01** | `src/Natter.Infrastructure/Natter.Infrastructure.csproj:24` + `Plugins/Nps/{NpsPluginRuntime,NpsContributionRegistry,NpsCliCommandContributor,IPluginCliCommandSink}.cs` | **Infrastructure → Cli.Abstractions** 反向依赖（S3-04 已记录，仍未动）。基础设施层为了承载「插件 CLI 命令」而依赖宿主应用层的命令契约。 | 把「插件 CLI 贡献」契约上移到 `Natter.Plugin.Abstractions`（或独立 `Natter.Hosting.Contracts`）。S3 复核已判「需单独评审」，此处保留同一结论：**不与其他项混批**。 | 中（结构债；改动面跨 5 文件 + 契约搬迁） |
| **B-02** | `src/Natter.Plugin.Abstractions/Natter.Plugin.Abstractions.csproj:17` | **Plugin.Abstractions → Application**：Host API 契约依赖应用层。后果：任何插件程序集（即便只用 manifest/IContributionRegistry）都间接吃下 `Application`（含 `MemoryScoring`、`PromptComposer` 等实现），ALC 共享面随之变宽；`Plugin.Store` 也因依赖 Plugin.Abstractions 被拖进来。 | 把插件真正需要的契约（`ConversationScope`、`MemoryScope`、prompt fragment 契约等）下沉到 `Natter.Abstractions` 或新的 `Natter.Contracts`；`Application` 反向实现它们。这会动 ABI，**必须单独立项**。 | **高**（越晚改代价越高：插件 Manifest/ABI 一旦对外发布就锁死） |
| **B-03** | `plugins/Natter.Plugin.AiEmbedding.OpenAi/Natter.Plugin.AiEmbedding.OpenAi.csproj:19` | 5 个功能插件里**唯一**直接引用 `Natter.Application` 的（其余 4 个只引 `Plugin.Abstractions`，WebConsole/PromptLibrary 另引 `Cli.Abstractions`）。它需要 `EmbeddingRequest/EmbeddingResult` 契约，说明**这些端口契约被放错了层**。 | 与 B-02 同源：embedding/rerank 契约移到插件可见的契约程序集。短期可接受（该端口刚加），但别让第 6、7 个插件照抄。 | 中（正在扩散的模式） |
| **B-04** | `samples/Natter.Sample.Plugin.Memory/Natter.Sample.Plugin.Memory.csproj:10-11` | 示例插件直接引用 `Application` + `Chat`。示例是别人抄的模板，会把这个依赖方向固化。 | 示例改用 `Plugin.Abstractions` 暴露的 memory 契约（或降低示例复杂度）。 | 中（示范效应） |
| **B-05** | 6 个 `ICompositionModule`：`ApplicationCompositionModule`(2 注册)、`CoreCompositionModule`(8)、`AdminCompositionModule`(8)、`ChatCompositionModule`(**51**)、`CliCoreCompositionModule`(**43**)、`NatterApplicationBuilder`(6)；另 `ChatCompositionModule.cs:103-135,183` | DI 注册高度集中在两个巨型模块（51+43 行注册），且「有 state-dir → Sqlite，否则 InMemory」的分支**在 5 处逐字复制**（见 C-05）。谁负责注册哪个服务只能靠读代码。 | 按关注点拆分 `ChatCompositionModule`（conversation / memory / prompt / task / query 五个子模块）；把 state-dir 分支收敛成一个工厂方法。**不改接口**，纯重组，可增量做。 | 中（不改也能跑，但每加一个 store 都在复制同一个三元表达式） |

---

## 3. C 类 · 重复与双轨

| ID | 位置 | 现状 | 建议 | 风险 |
| --- | --- | --- | --- | --- |
| **C-01** | `ContextContracts.cs:209`（`IMemoryStore`，v1）vs `MemoryEntryContracts.cs:177`（`IMemoryEntryStore`，v2）；实现 `SqliteContextStores.cs` vs `SqliteMemoryEntryStore.cs` | **两套记忆存储并存**。关键事实：v2 的 `IMemoryEntryStore` 在 `src/` 里**只有自己 + 它自己的实现**引用；实际调用方只有 `Cli.Core/Commands/MemoryEvalCommand.cs:210`（评测工具）与测试。而且 `MemoryRetrievalTests.cs:422-433` 有一条**反向断言**：生产管线参数里**不允许**出现 `IMemoryEntryStore`（"otherwise reads and writes split across"）。也就是说 v2 目前是**只给评测用的旁路实现**，生产召回仍走 v1。 | 二选一，别停在中间：(a) v2 扶正 —— 迁移生产读路径并删 v1；(b) v2 明确降级为「评测专用」—— 移到 `Natter.Cli.Core`/测试夹具体系，从 `Application` 契约里移走，并在 P6 文档写明。当前状态最坏：**读者会以为 v2 是现行实现**。 | **高**（这是文档/认知错误的源头，也是 §7 记忆文档能合并的前提） |
| **C-02** | `Application/Chat/Context/MemoryDecay.cs:125`（`Retention`）vs `:173`（`LegacyRetention`）；选择点 `MemoryScoring.cs:112-114` | 同一衰减职责两条曲线共存，**模式由调用方隐式选择**（`Score()` 里写死 `LegacyRetention`，注释说 v2 换 `Retention`）。两条曲线数值差很大：30 天时 `exp(-age/S)`≈0.37 vs 幂律归一化后 0.9。 | 参数化「模式」而不是留两个函数（`DecayMode.Legacy \| PowerLaw`），让 `ai status`/`health` 能报当前模式；这样 A-01 改名时一处切换。 | 中（现在只有一条生效，但重名函数会被误用） |
| **C-03** | 5 份独立脱敏实现：`Application/Admin/AdminRedactor.cs:41`、`Chat.Discord/DiscordLogRedaction.cs:29`、`Chat.Discord/DiscordSocketTransport.cs:781`（私有）、`Plugin.WebConsole/WebConsoleConfig.cs:64` 与 `:149`（`RedactSecrets`） | 模式（`sk-`/`Bearer`/PEM/`${secret:}`/`key=value`）在 5 处各写一遍。S2 期间为了「key=value 形式未脱敏」这一条（OBS-WC-P3-REDACT-KV）就已经要跨两侧同时改。 | 收敛为一份 `SecretRedaction`（放进 `Natter.Abstractions` 或 `Plugin.Abstractions` 的共享工具），各处只保留「要不要脱敏」的策略差异。**这是安全面**，越早收敛越好。 | **高**（漏一处的后果是明文泄漏，且新增 provider/插件会再抄一份） |
| **C-04** | 各 `config.schema.json` | 配置默认值有**两套约定**：4 份 schema 声明了 `default`（WebConsole 16 处、PromptLibrary 15、ConversationConsole 9、StateVitals 15），6 份**一个都没有**（DeepSeek、Spark、AiEmbedding、Discord/DingTalk/Memory 示例）。而代码里都有默认值（如 `DeepSeekOptions.MaxOutputTokens = 4096`）。 | 统一约定：要么所有 schema 都写 `default`（与代码默认值同源生成），要么都不写并在文档里给「默认值表」。当前状态让「生效默认值」只能靠读 C#。 | 中（直接导致 D-01 的不可观测） |
| **C-05** | `Infrastructure/Chat/ChatCompositionModule.cs:107,116,130,134,183` | `Path.Combine(Path.GetFullPath(context.Options.StateDirectory), "natter.db")` **逐字重复 5 次**（conversation/query/memory/summary/task 五个 store）。改库文件名要改 5 处，漏一处就分裂成两个库。 | 提取 `ResolveDatabasePath(options)` 一处；五个注册共用。顺手把 `isEmpty(stateDir) ? InMemory : Sqlite` 的三元也抽成泛型工厂。 | 中（真实的「漏改就分裂存储」风险） |
| **C-06** | `Infrastructure/Chat/InMemoryChatUpstream.cs:173`、`Chat/Dispatch/ChatCommandDispatcher.cs:163`、`Chat.Discord/DiscordChatUpstream.cs:389`、`Chat.DingTalk/DingTalkChatUpstream.cs:176` | 分片调用点 4 处，**每个适配器都要自己记得调** `ChatTextChunker.Chunk`。新增适配器漏调 → 长消息被平台整条拒收（这正是 S3-12 那条的用户可见后果）。 | 在 `IChatUpstream` 的执行入口（dispatcher 或一个装饰器）统一分片一次，适配器只发单片；或加一个「适配器必须声明 MaxMessageLength 且实现由基类调用」的契约测试。 | 中（约定靠自觉，无编译期保障） |
| **C-07** | 同上两个文件 + `AiResponderOptions.cs:35,46` vs `DeepSeekOptions.cs:49,51` | 同一默认文案定义两份：`AiResponderOptions.FallbackText/EmptyResponseText` 与 `DeepSeekOptions.FallbackText/EmptyResponseText` 字面量完全相同，运行时靠 `DeepSeekPlugin.cs:56-57` 把后者拷进前者。改一处不会改另一处。 | provider 选项不再重复声明这两个文案，直接复用共享默认值（provider 只在需要时覆盖）。 | 低（当前值相同，属「同值不同源」） |

---

## 4. D 类 · 错误语义与可观测性

| ID | 位置 | 现状 | 建议 | 风险 |
| --- | --- | --- | --- | --- |
| **D-01** | `Feature.Ai.DeepSeek/DeepSeekPlugin.cs:158-210`（`ai status` 载荷） | `ai status` 输出 model/thinking/reasoningEffort/baseUrlHost/history 上限与全部计数，**唯独不输出** `maxOutputTokens`、`maxInputCharacters`、`temperature`、`timeoutSeconds`、`maxRetries`。而 `DeepSeekOptions.cs:19-24` 的注释记录过真实事故：`maxOutputTokens=1024` 导致长代码块被截断 → 三次重试 → 用户看到「AI 暂时不可用」，**真实原因是自己的 token 上限**。恰恰是这类参数在诊断里看不到。 | `ai status` 增加 `maxOutputTokens`/`maxInputCharacters`/`temperature`/`timeoutSeconds`/`maxRetries`（都是非敏感值）；`--json` 同步。这样「上限导致的假故障」一次就能定位。 | **高**（已经误诊过一次；诊断缺口会重复发生） |
| **D-02** | `Cli.Core/Commands/HealthHttpServer.cs:41`、`:113` | 两处裸 `catch { }`：`Dispose` 里 `_loop?.Wait(2s)` 的吞，与 500 响应写入的吞。前者会**掩盖 AcceptLoop 的真实异常**（loop 崩了没人知道）；后者掩盖响应写失败。 | 至少 `Debug`/`Warning` 记一条（不记 URL 参数）；`Dispose` 里的吞可以保留但注明「shutdown 期间不抛」。 | 中（健康端点的故障会被静默） |
| **D-03** | `Plugin.Store/PluginPackageValidator.cs:102,531`、`PluginPackageWriter.cs:38`、`InstalledPluginIntegrity.cs:119`、`Plugin.WebConsole/WebConsoleServer.cs:117`；对照已修版本 `Infrastructure/Plugins/Nps/PluginPackageInspector.cs:16-58` | 惰性枚举 + 假防御（lead 修好的那类）**同类复核结论：只剩这些点位仍用惰性枚举，但失败域都被限制在单包/单插件内**——校验/写入/完整性检查失败等于该包被拒（fail-closed，符合预期），WebConsole 静态资源枚举失败等于该插件激活失败（宿主存活）。**没有第二处「root 属主子目录 → 整个宿主启动即崩」的同类**（`AndroidPluginHost.cs:136` 已在 try 内）。 | 不改行为；建议把 `PluginPackageInspector` 那段注释提炼成 PLUGIN_SPEC 的一条规则（「对不可读目录必须 fail-open=跳过并继续扫」），避免下次又写出调用点包 try 的版本。 | 低（实测不构成启动级故障） |
| **D-04** | `DeepSeekPlugin.cs:135`、`SparkPlugin.cs:121` 等处 vs 各自 `config.schema.json` | 同一约束**校验两遍**：schema 声明 `maxOutputTokens: 1..384000`，代码里再 `if (options.MaxOutputTokens is < 1 or > 384000) throw`。两处数值目前一致，但**没有任何机制保证一致**——改一处就会产生「schema 放行、运行时炸」或反之。 | 让范围只从一处来（schema `maximum`）或加一条启动期自检测试逐字段比对 schema 与代码范围。 | 中（典型的「两份定义慢慢漂移」） |
| **D-05** | `DeepSeekOptions.cs:26`（`MaxInputCharacters=12000`）与 `AiResponderOptions.cs:39-46` | 输入侧上限与模型真实上下文窗口无关联校验：配成超过供应商上下文会在运行期被上游拒绝，配得过小则静默裁剪历史。启动期不做任何一致性检查（`Validate()` 只查范围）。 | 启动期输出一条「配置上限 vs 已知模型窗口」的对照（未知模型则告警），把「静默裁剪」变成启动期可见信息。 | 低（可观测性，非正确性） |

---

## 5. 优先级建议

### P0 · 先做（都是「消除误导」，多数不改行为）
1. **C-01 + A-06 记忆双轨定性**（决定后续所有记忆文档能否合并，也决定下一个人读哪套接口）
2. **A-02 `IsAvailable` 语义撒谎**（生产健康检查给假绿灯；改动 3 行 + 一个字段）
3. **D-01 `ai status` 补生效上限**（已经误诊过一次，纯增量字段）
4. **C-03 脱敏收敛**（安全面重复 5 份，新 provider 会继续抄）

### P1 · 需要设计决策（单独立项，不混批）
5. **B-02 Plugin.Abstractions → Application**（ABI 级，越晚越贵）
6. **B-01 Infrastructure → Cli.Abstractions**（S3-04 同结论）
7. **C-02 衰减模式参数化 + A-01 改名**（依赖 C-01 先定性）

### P2 · 顺手做（低风险小改）
8. C-05 数据库路径去重（5 处 → 1 处）、C-04 配置默认值约定、C-07 默认文案去重
9. **C-06 分片调用点收敛**（4 处 → dispatcher 一处，或加契约测试锁住「新适配器必须分片」）——当前靠自觉，漏调就是整条消息被平台拒收
10. D-04 schema 与代码范围一致性自检、D-02 健康端点日志
11. A-03/A-04/A-05 命名与目录规范（写进 PLUGIN_SPEC 即可，未必改代码）

### 记录并接受（不建议现在动）
- **B-03/B-04**（插件/示例越层）：与 B-02 同源，跟着 B-02 一起解决；在此之前不扩大。
- **D-03**：实测无启动级同类，仅建议把规则写进规范。
- **D-05**：可观测性，等到有第二个 provider 再一起做。
- **B-05**：DI 模块拆分是纯重构，收益随时间衰减，可与 C-05 同批做。

---

## 6. 最该先修的 5 项（我的排序，含理由）

| # | 项 | 为什么是它 |
| --- | --- | --- |
| 1 | **C-01 记忆 v1/v2 定性** | 它是本轮**唯一**同时污染文档（§7 里 6 份记忆文档合并的前提）、契约（两套接口）、认知（新读者会照 v2 写）的问题。不定性，后面所有记忆相关整改都是猜。 |
| 2 | **A-02 `IsAvailable` 撒谎** | 成本 3 行，收益是「健康检查不再给假绿灯」。这类问题一旦上线，排查成本远高于修复成本。 |
| 3 | **C-03 脱敏收敛 5→1** | 唯一**安全面**的重复项；且已被 OBS-WC-P3-REDACT-KV 证明「漏一处就明文泄漏」。收敛越晚，新增 provider/插件越多份拷贝。 |
| 4 | **D-01 `ai status` 补上限** | 有真实误诊前科（`maxOutputTokens=1024` → 伪装成「AI 不可用」）。纯增字段，零风险。 |
| 5 | **B-02 Plugin.Abstractions → Application** | 唯一的 **ABI 级**结构债：插件一旦对外发布，这个依赖就被锁死。现在动只需要搬契约；发布后动等于破坏性 ABI 变更。 |

---

## 7. 第二部分预览：34 份文档的判定草案（待你确认后再执行）

> **草案，不是交付**。执行前不移动、不删除、不改名任何文件。判定四项：保留 / 合并 / 归档 / 过期重写。
> **判定分布（草案）**：保留 **22** / 合并 **4** / 归档 **6** / 过期重写 **2** = 34 份（不含本次新增的本文）。

| # | 文档（行数 / 最后提交） | 判定 | 一句话理由 |
| --- | --- | --- | --- |
| 1 | `AI_PROVIDER_DESIGN.md` 855 / 11-15 | 保留 | 现行 provider 设计；需补一节 embedding/rerank 端口（P6 新增，本文未覆盖） |
| 2 | `ANDROID_PLUGIN_HOST.md` 671 / 11-15 | **过期重写** | 信任根已改 embedded resource、安装后完整性复验已加，文档仍是 11-15 的旧机制 |
| 3 | `ARCHITECTURE.md` 370 / 11-15 | **合并** → `HARNESS_ARCHITECTURE.md` | 两份架构总览，后者已更新；前者 11-15 未动 |
| 4 | `CODE_REVIEW_2026-11-22.md` 610 / 11-22 | **归档** | 一次性审查快照，结论已落地为代码与 S3 批次 |
| 5 | `CODE_REVIEW_S3_TRIAGE.md` 242 / 11-22 | **归档** | 同上；批次 A 已提交（`3cf65ee`）、批次 B 第一项已修 |
| 6 | `CONTEXT_MEMORY_PLUGIN.md` 597 / 11-15 | **合并** → `P6_MEMORY_SYSTEM.md` | P3 时代插件契约，已被 P6 记忆体系取代大半 |
| 7 | `CONVERSATION_CONSOLE.md` 319 / 11-22 | 保留 | 现行；状态行已准确写「P1 已实现（`ba76548`）· P2 待做」，无需改 |
| 8 | `DEPENDENCY_EVOLUTION_POLICY.md` 368 / 11-16 | 保留 | 现行政策，与代码一致 |
| 9 | `DEVELOPMENT.md` 806 / 11-22 | 保留 | 现行；§6.4 已在本轮 S3 校正 |
| 10 | `DISCORD_PLUGIN_DESIGN.md` 721 / 11-17 | 保留 | 现行设计 |
| 11 | `HARNESS_ARCHITECTURE.md` 792 / 11-18 | 保留 | 现行架构主文档（吸收 #3 后更长） |
| 12 | `HARNESS_RESPONSIBILITY.md` 277 / 11-22 | 保留 | 现行边界文档，S3 已校正 mandatory 段 |
| 13 | `LIVE_TEST_PLAN.md` 272 / 11-22 | 保留 | 现行；S3 已补 L6–L8 与计数口径 |
| 14 | `NLMP.md` 1179 / 11-14 | **过期重写** | 11-14 后 100+ 提交：`ContentFormat`、结构化 source 列、`MentionKind`、capabilities 语义均已变 |
| 15 | `OPERATIONS_RUNBOOK.md` 677 / 11-22 | 保留 | 现行运维手册（**第三部分命令校验重点对象**） |
| 16 | `P2_RUNTIME_PROFILE.md` 752 / 11-22 | 保留 | 现行；结论行本轮已校正 Closed·Go |
| 17 | `P3_CONTEXT_MEMORY.md` 788 / 11-15 | **归档** | P3 阶段记录；归档时在主文档留 20 行「P3 结论」摘要即可 |
| 18 | `P4_TIER1_PRODUCTIZATION.md` 508 / 11-22 | **归档** | P4 阶段已完成 |
| 19 | `P5_AGENT_TASK_RUNTIME.md` 591 / 11-21 | 保留 | 现行设计 |
| 20 | `P6_MEMORY_EVAL_RESULTS.md` 243 / 11-23 | 保留 | 数据型记录，长期有效（记忆三份之一） |
| 21 | `P6_MEMORY_OPEN_QUESTIONS.md` 512 / 11-22 | 保留（**改名** `P6_MEMORY_GAPS.md`） | 作为记忆三份之一「缺口与决策」，只留未决策项 |
| 22 | `P6_MEMORY_RAG.md` 468 / 11-22 | **合并** → `P6_MEMORY_SYSTEM.md` | 文件头已自标「部分被取代」；留现行部分 |
| 23 | `P6_MEMORY_SYSTEM.md` 677 / 11-22 | 保留（**升为记忆主文档**） | 记忆三份之一「现行设计」，作为合并入口 |
| 24 | `P6_RAG_PLUGIN_DESIGN.md` 689 / 11-22 | 保留 | 现行 RAG 插件设计；与 #23 边界需一句话说明（插件 vs 内核能力） |
| 25 | `PLUGINS.md` 293 / 11-15 | **合并** → `PLUGIN_SPEC.md` | 「清单/适配器覆盖/生命周期」与 NPS 规范大量重叠，且 11-15 未更新 |
| 26 | `PLUGIN_SPEC.md` 1289 / 11-22 | 保留 | 唯一权威 NPS 规范 |
| 27 | `PROMPT_COMPILER.md` 853 / 11-22 | 保留 | 现行；随 mandatory 迁移已更新 |
| 28 | `PUBLISHING.md` 318 / 11-15 | 保留 | 现行发布指南（**第三部分命令校验对象**） |
| 29 | `SECOND_ADAPTER_VALIDATION.md` 502 / 11-15 | **归档** | 11-15 验证记录，结论已进代码与测试 |
| 30 | `STATE_VITALS_DESIGN.md` 635 / 11-22 | 保留 | 现行设计 |
| 31 | `WEBCONSOLE_HOST_API_GAP.md` 292 / 11-21 | **归档** | G1–G9 已被 Phase 2/3 关闭；留着会让人以为仍是缺口 |
| 32 | `WEBCONSOLE_PLUGIN_DESIGN.md` 712 / 11-21 | 保留 | 现行设计 |
| 33 | `WEBCONSOLE_PLUGIN.md` 469 / 11-22 | 保留 | 现行使用说明 |
| 34 | `WEBCONSOLE_UI_DIRECTION.md` 285 / 11-22 | 保留 | 现行 UI 方向（P1 五项已落地） |

### 7.1 记忆文档收敛：6 份 → 3 份

| 目标文件 | 来源 | 内容边界 |
| --- | --- | --- |
| `P6_MEMORY_SYSTEM.md`（现行设计） | 本体 + `CONTEXT_MEMORY_PLUGIN` + `P6_MEMORY_RAG` 现行部分 | 契约、存储、注入路径、衰减模型 |
| `P6_MEMORY_GAPS.md`（缺口与决策） | `P6_MEMORY_OPEN_QUESTIONS` 改名 | 只留**未决策**项 + 理由 + 触发条件 |
| `P6_MEMORY_EVAL_RESULTS.md`（评测数据） | 本体 | 只留数据与结论，不含设计 |
| `P3_CONTEXT_MEMORY.md` | → `docs/archive/`（阶段前缀 `P3-`） | 归档，主文档留摘要 |

> **前提**：C-01（v1/v2 定性）必须先做。v2 是扶正还是降级为评测专用，直接决定这 3 份写哪套接口——现在写等于把错误固化。

### 7.2 归档与索引规则（待确认后执行）

- 归档目录：`docs/archive/`，文件名加阶段前缀（`P3-`、`P4-`、`REVIEW-2026-11-22-`）。
- 新建 `docs/README.md` 索引，四组：**现行设计 / 阶段记录 / 运维 / 测试**，每份一句话定位。
- 所有相对链接用 `test -f` 核实；合并时保留来源段落的锚点，避免外部引用全断。
- 归档不动 `docs/testing/`（已有自己的索引且本轮 S3 已补全）。

## 8. 本轮未验证 / 未覆盖（明确声明）

1. **未做运行时验证**：全部结论来自静态阅读，未跑构建、未跑测试、未启动宿主。改代码的批次需要各自回归。
2. **未审计 `plugins/` 内部实现质量**：本轮只看「分层/命名/重复/错误语义」四类横切问题，未逐文件 review 5 个插件的业务逻辑。
3. **B-05 的「每个服务该由谁注册」**未给最终分组方案，只给了方向（按关注点拆）。落地前需要一次服务清单盘点。
4. **C-03 的收敛目标位置**（`Abstractions` vs `Plugin.Abstractions`）取决于 B-02 的结论，未定。
5. **文档 34 份判定是草案**，且**未做**第三部分的命令实跑校验——`OPERATIONS_RUNBOOK.md`、`PUBLISHING.md`、`DEVELOPMENT.md` 里的命令行数很多，按你的硬要求，那些命令在第二部分执行前**一律视为未验证**。
6. 本轮**未改动任何文件**：本文之外没有其他变更；未 commit/push。
