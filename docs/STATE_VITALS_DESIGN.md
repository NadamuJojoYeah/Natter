# State & Vitals 插件设计

> **状态**：约束已定 · 实现待开工
> **日期**：2026-11-22
> **定位**：娱乐/角色类内容插件，opt-in，不进 WebConsole（管理面）。通过 **Content Pack** 扩展。
> **关联**：[Harness 责任边界](HARNESS_RESPONSIBILITY.md)、[Prompt Compiler](PROMPT_COMPILER.md)、[会话控制台](CONVERSATION_CONSOLE.md)

---

## 0. 一句话

> 角色有「状态」和「数值」，被渲染成 prompt 片段去影响输出。**机制归核心插件，内容归 Pack，宿主不背书。**

---

## 1. 定位与命名

### 1.1 它是什么

| 组成 | 形态 | 例子 |
| --- | --- | --- |
| **asserted state** | 离散、外部断言、带时长 | 受伤 / 怀孕 / 被月光祝福 |
| **vital** | 连续、数值、随时间衰减 | 饥饿 / 精力 / 周期 |
| **derived state** | 由 vital 阈值算出的只读视图 | 力竭 / 濒死 / 经期中 |

三者单向串联，见 §3.1。

### 1.2 命名（避免 `state` 过载）

`state` 在本仓库已经严重过载：

```text
state-dir        运行时状态目录
AgentTaskStatus  任务状态
plugin state     插件激活状态
```

因此：

| 用途 | 取值 |
| --- | --- |
| 工程名 / 插件 id | `Natter.Plugin.StateVitals` / `natter.plugin.state-vitals` |
| 代码类型名 | `StateVitalsRuntime` / `StateVitalsOptions` |
| 人类称呼 | 「状态系统」或 states-vitals |

**短名只在人类语言里用，不进标识符。** 理由同我们把插件叫 `WebConsole` 而非 `console`。

### 1.3 它不是什么

- 不是宿主能力（不进 `src/` 核心逻辑）；
- 不是运维视图（不进 WebConsole）；
- 不是记忆；
- 不是安全机制（内容由 Pack 与配置负责，宿主不背书）；
- 不是插件平台（不加载第三方程序集，见 §9.1）。

### 1.4 与 memory 的分工

| | 记谁 | 生命周期 | 作用域 |
| --- | --- | --- | --- |
| **memory** | 关于**用户**的持久事实 | 长期，显式记住 | 按会话 |
| **state/vitals** | **角色自己**的处境 | 短期，时间/事件驱动 | **全局**（见 §7） |

一句话：**memory 记你，state 记我。**

---

## 2. 早定格（8 条）

后期修改代价极高（迁移数据或改变 prompt 身份），必须现在锁定：

| # | 决定 | 为什么早定 |
| --- | --- | --- |
| 1 | 作用域 = **全局单一角色状态** | 决定存储主键；以后改要迁移全部历史 |
| 2 | 时间模型 = **衰减用墙钟**，存活类用单调 | 混用会产生难查的诡异行为 |
| 3 | 持久化 = **插件自己的库文件** | 进 `natter.db` 会破坏单一 schema owner |
| 4 | 入 prompt 方式 = **片段 + 分桶 + 位置靠后** | 决定 hash 稳定性与缓存命中率 |
| 5 | prompt 身份 = **存下来的文本 hash**，不是 id | 决定缓存与可复现性 |
| 6 | state 分两类：**asserted（可写）/ derived（只读视图）** | 不分则成环，见 §3.1；derived 不落库 |
| 7 | 数值持久化用 **定点数**（1/1000 整数） | 浮点跨平台不确定，会让边界判定与测试抖动 |
| 8 | 所有 id 从第一天带 **pack 作用域**（`core#hunger`） | 引入 Pack 后不加前缀，历史数据全部要迁移 |

其余（衰减系数、阈值、档位名、文案模板、图表样式、UI 布局、add/max/mul 的默认选择）全部**晚定格**。

---

## 3. 数值模型

### 3.1 结构：单向三层，无环

```text
asserted state（外部断言，带时长）
      ↓ 效果（effect）
vital（数值，积分器）
      ↓ 阈值
derived state（只读视图）
      ↓ 渲染
prompt 片段
```

**为什么必须分两类 state**：若只有一种，立刻成环——

```text
受伤  →  降低 energy 恢复速率      （state 影响 vital）
energy < 20  →  力竭               （vital 派生 state）
```

拆开后：

| 类型 | 来源 | 能否外部设置 | 需要存储 | 例子 |
| --- | --- | --- | --- | --- |
| **asserted** | 事件 / 时长 / 规则 | 能 | 是（含到期时间） | 受伤、怀孕、诅咒 |
| **derived** | vital 阈值计算 | 不能（只读视图） | **否** | 饥饿、力竭、经期中 |

derived 不落库的附带好处：**改阈值后历史曲线自动按新定义解释**，调参时正是想要的。

### 3.2 公式

统一为一阶滞后：

```text
v_next = v + (target - v) * clamp(k * Δt, 0, MAX_STEP)
```

三个要点：

1. **乘性饱和**：增量是「到目标剩余空间的**比例**」，不是固定差值 → 天然不越过目标、接近时自动变慢。
2. **Δt 夹紧**：离线三天时 `k*Δt` 可能远大于 1，会冲过 target 变负。必须夹到 `MAX_STEP`（建议 0.5~1.0）。
3. **target 泛化**：可以是上限（饥饿→100）、下限（饱腹→0），或时间函数（睡眠欲昼夜曲线、月经 28 天周期）。不要为每个 vital 写一套数学。

### 3.3 为什么不是加法 + clamp

```text
加法      v += rate * dt      会越过，只能靠 clamp 兜底；到顶瞬间生硬
乘性      v += (cap-v)*k*dt   自限、渐近、无需硬 clamp（但仍需夹紧 dt）
```

### 3.4 三个必须处理的细节

**① 渐近导致到不了端点**

`v` 会停在 99.7 而不是 100。阈值一律设在渐近线之下（85 就算 `starving`），或在 `(target-v) < ε` 时取端点。推荐前者。

**② 多力合成必须一次性应用**

同一 tick 内可能有多个来源（自然衰减、asserted state 效果、跨 Pack 修正）。**先收集全部增量，再按 `(vitalId, sourcePriority)` 固定顺序一次性应用**。逐条顺序应用会引入不可复现的顺序依赖。

**③ 双重量级夹紧**

除 `Δt` 上限，还要对**单 tick 总变化量**设上限，防止多力叠加时单跳过大。

### 3.5 确定性要求

- 所有衰减计算走**注入的 `TimeProvider`**，禁止直接 `DateTimeOffset.UtcNow`；
- 浮点**不进入 prompt hash**（见 §4）；
- 持久化用定点数（见 §8.2）；
- 测试必须能注入可控时钟做精确断言。

---

## 4. 分桶渲染：把连续数值变成稳定文本

```text
内部   定点数，衰减自然
对外   只暴露「档位」：sated / peckish / hungry / starving
```

推论：

- 文本只在**跨档**时变化；
- 跨档是低频事件 → **prompt hash 只在跨档时变**；
- 因此 prompt 缓存命中率高（DeepSeek 的 `prompt_cache_hit_tokens` 可直接度量）；
- 图表仍按数值画，观感不受影响。

**档位必须单调、覆盖全区间、无空洞**（见 §10 校验）。

---

## 5. 状态如何进入 prompt

### 5.1 位置：靠后，但在最近对话之前

```text
人设 / 规则 / 世界观      ← 稳定，永不变
记忆摘要                  ← 低频变化
【状态片段】               ← 中频变化（跨档 / 断言态变更）
最近对话                  ← 高频变化
```

prompt 缓存按**前缀**匹配。状态放在前面会让每次跨档使后面全部失效；放中间偏后损失最小，同时保留近因权重。

### 5.2 片段契约

- 通过 `prompt.fragment` 贡献点提供；
- 片段 id 带命名空间（P3 已实现 `owner#local` 前缀；本插件再带 pack 前缀，见 §9.3）；
- **遵守每 provider 配额**（16 片段 / 8000 字符），不得挤占 memory 或 library；
- 不能声明 `Mandatory`、不能冒充 `HostBuiltin`（P3 已拒绝）。

### 5.3 渲染分档

```text
默认      短标签列表        [hunger: hungry] [energy: low]
刚变化    该状态写完整描述
高强度    超过高阈值的写完整描述
```

让绝大多数回合只付标签成本，把「小作文」留给真正值得注意的时刻。

---

## 6. 描述生成

### 6.1 方案

描述在**状态切换时生成一次**，随状态记录落库；状态持续期间复用同一份文本。

变化点是状态切换（低频），不是回合（高频）——因此既有表达多样性，又不破坏可复现性。

### 6.2 四个硬性条件

| # | 条件 | 原因 |
| --- | --- | --- |
| 1 | **生成一次，存下来**，不得每轮重算 | 否则每轮 hash 变化，缓存与复现全失效 |
| 2 | **存下来的文本参与 planHash** | 不是 id，是文本本身的 hash |
| 3 | **失败必须降级**为模板文本，不阻塞回合 | 生成是模型调用，会超时/限流 |
| 4 | **必须幂等** | 状态切换可能被平台重投；按 `(stateId, transitionSeq)` 去重 |

### 6.3 腔调必须由 Pack 定义

**生成用的提示词、语气约束、示例、禁用词全部来自 Pack。**

如果写死在核心插件里，一百个 domain 会用同一种腔调——月经、疾病、魔法听起来像同一个人写的。

核心只负责：什么时候触发、怎么存、怎么降级、怎么脱敏。

### 6.4 内容约束与安全

- 生成文本必须落在该状态的**语义边界**内（「轻微饥饿」不能写成「濒死」）；
- 保留模板作为「即使漂移也可接受」的兜底；
- 片段正文**不进日志、不进审计、不进诊断包**（P3 已保证日志只记 id/code/count）；
- **单独确认两个后加出口**：`prompt explain` 与 WebConsole 诊断包。

---

## 7. 作用域：全局状态 + 每会话记忆

### 7.1 决定

```text
state/vitals   全局单一：角色是「一个人」，跟谁说话都累
memory         按会话：但记得每个用户各自的事
```

恰好模拟真人：我累了跟谁都累，但我记得你。

### 7.2 两条硬约束

**① 全局状态是全局争用点**

```text
两条都读到 hunger=50，都 +10，写回两次 → 60（应为 70）
```

必须**单写者或版本化 compare-and-set**：记录带 `Version`，冲突重读重算。

**② 全局状态的描述必须去标识化**

全局状态进入**所有用户**的 prompt。

```text
可以    "心情不错"
不可以  "被 A 夸奖后心情不错"     ← 把 A 的互动泄漏给 B
```

规则：**全局状态文本不得包含任何具体用户、会话、平台信息**。需要「被谁影响」的层次只能放在 per-conversation 内容里。

### 7.3 跨平台身份不是 bug

同一人在 Discord 与钉钉是不同 id。默认应视为**两个认识的人，但共享同一角色状态**——真人也不知道这两个身份是同一人。

身份关联是**独立 opt-in 功能**，不放进第一版。

---

## 8. 存储

### 8.1 自己的库

```text
<state>/plugins/natter.plugin.state-vitals/state.db
```

理由：

- `SqliteSchema` 是宿主**唯一**迁移 owner（task-38 刚统一），插件加表会重新制造双 owner；
- 卸载插件删目录即可，不留脏表；
- 可独立备份。

### 8.2 表结构要点

```text
state_snapshot     vitals 定点值 + 断言态（含到期时间）+ Version
                   + LastUpdatedAt + DefinitionVersion + PackVersions
state_events       变更流水（含 transitionSeq，用于幂等）
state_descriptions 生成的描述文本（按 state + 档位 + 版本）
```

**不存 derived state**——它是视图。

`DefinitionVersion` 与 `PackVersions` 必须存：Pack 更新后回看旧数据要用旧定义重算，否则会出现「用新参数解释旧曲线」的错乱。

**定点数**：vital 值以 1/1000 为单位的 `int` 持久化。内存可用浮点，落库前量化。

---

## 9. 扩展单位：Content Pack

### 9.1 为什么是 Pack，不是子插件

用户想要的例子（月经生理 / 疾病生理 / 魔法状态）里，那些人是**跑机器人的人**，不是 C# 开发者。

如果扩展单位是「子插件」，他们要建工程、写 csproj、打包、签名、跟 NPS 版本适配——99% 的目标用户会放弃。

而子插件在本项目还有额外代价：

| 代价 | 说明 |
| --- | --- |
| 依赖环 | task-37 刚修过「任一环导致全图 `order=[]`」；两级依赖让环风险与排查难度翻倍 |
| ABI 版本化 | state 插件对外接口变成公共 ABI，改一次子插件全废，形成二级版本矩阵 |
| 代码执行面 | NPS 插件**进程内完全信任**运行；「魔法状态包」= 任意代码执行 |
| 加载顺序 | 第二层顺序依赖，故障诊断路径变长 |
| 测试形态 | 从「装一个插件断言行为」变成「装两个 + 顺序 + 版本」 |

因此：**内容归 Pack（数据），机制归核心插件（代码）。**

按名解析留一个口子，将来真需要第三方代码再扩（§9.6）。

### 9.2 五个原语（类型冻死）

借鉴 RimWorld Def 系统的核心克制：**扩展面是类型，不是实例**。

v1 冻结为五类，Pack 只能实例化，不能定义新类型：

```text
vital           数值驱动
assertedState   外部断言态（可写、带时长）
derivedState    阈值派生态（只读视图）
effect          一处连线（state → vital / vital → state / 跨 Pack 引用）
trigger         断言从哪来（事件 / 时长 / 规则）
```

**冻死类型是对的**：RimWorld 至今不让 mod 加 DefType，所以它的加载器长期保持稳定。

### 9.3 Pack 形态

```text
<config>/state-vitals/packs/
├── menstrual.json
├── disease.json
└── magic.json
```

```json
{
  "schemaVersion": 1,
  "packId": "menstrual",
  "version": "1.0.0",
  "enabled": true,
  "vitals": [ ... ],
  "assertedStates": [ ... ],
  "derivedStates": [ ... ],
  "effects": [ ... ]
}
```

**id 命名空间**：`<packId>#<localId>`，如 `menstrual#cycle`、`core#hunger`。

### 9.4 继承：`abstract` + `parent`

```json
{ "abstract": true, "id": "baseNeed",
  "range": {"min":0,"max":100}, "baseline": 0,
  "levels": [ {"at":0,"id":"sated"}, {"at":60,"id":"urgent"} ] }

{ "id": "hunger", "parent": "baseNeed",
  "decay": {"ratePerHour": 0.08, "target": 100} }
```

没有继承，每个 Pack 都要重抄 range/levels，改一次全都要改。**建议 v1 就做**（一次深合并），但一旦作者开始写内容再改就晚了。

### 9.5 软依赖：`mayRequire`

```json
{ "id": "comorbidity", "mayRequire": "disease",
  "when": { "disease#fever": {"at": ">= 50"} }, ... }
```

「只有装了 disease pack 才存在」。让 Pack 之间可以**软依赖**：月经包可以在疾病包存在时加一条「经期免疫力下降」，没装也不报错。

硬依赖会让用户少装一个包就整个启动失败，尽量避免。

### 9.6 不做 Patch，用可组合效果替代

RimWorld 有 `PatchOperationAdd/Remove/Replace`，强大但复杂。**我们的场景不需要**，因为效果是**可组合的**（add / max / mul）：

```text
想表达「魔法使用者不来月经」
RimWorld 做法   patch 掉 menstrual 的 cycle 定义
我们的做法      加一条 effect：magicUser → menstrual#cycle rate *= 0
```

后者不改别人的定义，只是叠加，天然可共存、可独立关闭。

若将来真需要结构性修改，给一个**受限 overrides**（只能改数值字段，不能删 def），不要上通用 patch。

### 9.7 代码口子：三层解析

```text
第一层  纯声明式 Pack          v1 就做
第二层  Pack 按名引用内置 rule  "rules": [{"name":"moonPhase"}]
第三层  （将来）按名解析到外部程序集
```

关键是**名字解析是间接的**：Pack 说「用 moonPhase 这个规则」，解析器不关心它是内置还是外部的。

将来真要开放第三方代码，只换「名字 → 实现」这一层，Pack 格式与状态存储都不用动。

### 9.8 Pack 配额

防止单个 Pack 让求值退化：

```text
每 Pack 最多 N 个 vital / assertedState / derivedState / effect
全局最多 M 个 Pack
单 tick 求值的效果总数上限
```

超限 → 启动时拒绝并指出是哪个 Pack。

---

## 10. 校验与作者体验

### 10.1 加载期校验，报错必须可操作

Pack 作者不是程序员，报错质量直接决定他们能否自己修好。

必须覆盖：

- `levels` 单调递增、覆盖 `[min, max]`、无空洞；
- `ratePerHour` 有合理上界；
- `effects` 引用的 target 必须存在（跨 Pack 引用同理）；
- 引用未注册的 rule 名 → **启动时报错**，不是运行时静默跳过；
- `parent` 存在且无环；
- `mayRequire` 指向未安装 Pack → 静默不生效（这是设计），但要在 `validate` 输出里列出「因缺依赖跳过」。

报错格式沿用 `config.schema.json` 那套（JSON Pointer + 期望值 + 实际值），并额外给**建议改法**：

```text
packs/disease.json#/vitals/1/levels/2
  档位 at=40 小于前一个 at=60（必须单调递增）
  建议：改为 at=70，或调整前一档
```

### 10.2 两个 CLI 命令（v1 就要）

不做热重载（RimWorld 也是重启生效），但要给更便宜的东西：

```bash
natter states validate          # 加载所有 Pack，报全部配置错误与因缺依赖跳过的 def
natter states explain hunger    # 解析后的定义 + 当前值/档位 + 渲染出的片段
```

作者改个数字立刻能看到最终 prompt 长什么样，不用起完整宿主。

价值高、成本低，且避开热重载那堆麻烦（DefinitionVersion、prompt hash 失效、状态迁移）。

---

## 11. 审计与图表

**按消费者拆，不按数据拆。**

| 消费者 | 需求 | 落点 |
| --- | --- | --- |
| 调参的人 | 多天曲线、参数对比 | **状态插件自己的页面**（第二版） |
| 排查的人 | 第 N 轮时的状态 | **已有的 task trace**（一行摘要） |
| 追溯的人 | 变更流水 | 插件自己的事件表 |

### 三条原则

**① 主图表留在插件自己的页面**

状态是内容，WebConsole 是运维面。把角色状态曲线放进管理面，等于让管理面承载内容——与 Harness 责任边界冲突，也会逼管理面申请内容侧权限。

**② 轻量快照进已有时间轴**

```text
hunger=72(hungry) energy=30(low) [injured]
```

完整曲线点进去看。

**③ 不做跨插件读库**

会话控制台不要直接读状态插件的库。要联动就**深链接**（带 turn id 跳转）。跨插件读会立刻产生「谁的 schema、谁的迁移、谁负责脱敏」三连问。

### 图表实现（第二版）

- **内联 SVG 手写折线**，不引图表库（与 WebConsole 同一取向：零依赖、零构建）；
- 数据分级保留：24h 原始点 → 更早按小时聚合 → 超过 N 天只留档位变更事件。

---

## 12. 三条 Natter 特有约束（RimWorld 不用管）

### 12.1 随机性必须种子化

疾病包会想写「25% 概率感染」，魔法包会想写「10% 概率反噬」。

**裸随机直接破坏可复现性**——`PromptPlan.Hash` 与会话复现都依赖确定性。

```text
seed = hash(packId, triggerEventId, sequence)
```

同一事件重放得到同一结果。这样：

- 同一次对话永远可复现；
- 平台重投不会掷出第二个结果；
- 测试可精确断言。

**不做这条，所有涉及随机的 Pack 都不可测。**

### 12.2 数值用定点，不用 double 持久化

同一表达式在 x64 与 ARM 上末位可能不同。虽然分桶后浮点不进 hash，但：

- 测试断言会偶发失败（尤其 ARM CI）；
- 「刚好卡在阈值上」的边界判定会抖。

**持久化用 1/1000 定标整数**，内存可用浮点，落库前量化。

### 12.3 腔调归 Pack

见 §6.3。核心只提供机制，不提供文风。

---

## 13. 宿主侧缺口

### 13.1 `PromptFragmentContext` 没有会话身份

当前形状：

```csharp
public sealed record PromptFragmentContext(
    RuntimeProfile Profile, string? PresetId, string? Locale,
    string Platform, string Protocol, ConversationKind ConversationKind,
    IReadOnlyDictionary<string, string> Variables);
```

**没有 source / conversationId / actorId**，`Variables` 白名单也只有 `user.name`。

补充要求：

- **以可选 `init` 属性添加**（如 `ConversationScope? Conversation { get; init; }`），**不要改现有构造函数签名**——位置参数一变，所有既有 `IPromptFragmentProvider` 实现全部编译失败；
- 为 null 时行为与现在完全一致；
- 补齐后 `PromptLibrary` 与既有片段测试不受影响。

### 13.2 宿主全局时钟

宿主应提供 `TimeProvider` 单例；插件拿不到时回落到 `TimeProvider.System`，不得因此激活失败。

### 13.3 日志与导出出口复核

P3 保证片段正文不进日志，但**后来新增的两个出口需单独确认**：`prompt explain` 与 WebConsole 诊断包。

---

## 14. 非目标

- 不做跨平台身份关联（独立 opt-in 功能）；
- 不加载第三方程序集（§9.7 第三层留给将来）；
- 不做通用 XML patch；
- 不做热重载（用 `validate`/`explain` 替代）；
- 不内置脚本引擎（Lua/JS）；
- 不做内容审查（Harness 不背书内容）；
- 不做真实平台消息发送；
- 不接管 memory；
- 第一版不支持多角色并发持有多个状态集。

---

## 15. 验收标准（第一版）

**模型与数值**
- [ ] state 分 asserted / derived 两类，derived 不落库；
- [ ] 依赖单向：asserted → vitals → derived，无反向写入；
- [ ] 衰减走注入时钟，可控时钟可精确断言；
- [ ] `Δt` 与单跳总量双重夹紧，离线一周不越界；
- [ ] 多力合成一次性应用，顺序固定且结果确定；
- [ ] 持久化为定点整数，round-trip 无损。

**渲染与 prompt**
- [ ] 渲染按档位分桶，浮点不进 hash；
- [ ] 跨档才改 planHash，同档内多轮 hash 稳定（两次采样对比断言）；
- [ ] 片段遵守每 provider 配额，不挤占 memory 与 library；
- [ ] 状态文本不含用户名/会话 id（哨兵断言）。

**Pack 体系**
- [ ] 五类原语冻结，Pack 不能定义新类型；
- [ ] id 全部带 pack 作用域（`core#hunger` 形式）；
- [ ] `abstract` + `parent` 继承可用；
- [ ] `mayRequire` 缺依赖时静默跳过，且 `validate` 输出可见；
- [ ] 校验覆盖档位单调/覆盖/无空洞、effect target 存在、未知 rule 名、parent 环；
- [ ] 报错含 pack 路径 + JSON Pointer + 建议改法；
- [ ] 配额生效，超限拒绝并指出 Pack。

**生成与并发**
- [ ] 描述生成一次落库，重投不重复，失败降级模板且不阻塞回合；
- [ ] 生成用提示词来自 Pack（不在核心里写死）；
- [ ] 全局状态写入版本化，并发两路不丢更新；
- [ ] 随机走种子化 RNG，同事件重放结果一致。

**隔离与回归**
- [ ] 插件使用自己的库文件，未向 `natter.db` 增表；
- [ ] 日志、`prompt explain`、诊断包导出均不含状态正文；
- [ ] 默认不启用；未安装该插件时宿主行为零变化；
- [ ] 既有 7 个插件加载不变，PromptLibrary 36 例不回归。

---

## 16. 一句话总结

> 数值用「乘性饱和 + dt 夹紧 + 定点持久化」，文本用「分桶」，结构用「asserted → vital → derived 单向链」，扩展用「Pack（类型冻死、可继承、软依赖、不 patch）」，三件 RimWorld 不用管的事：**种子化随机、定点数、腔调归 Pack**。
