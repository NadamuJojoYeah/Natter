# Natter 作为 Harness 的责任边界

> **状态**：已决策（2026-11-22）· 实施分阶段
> **定位**：定义 Natter 对下游插件「负责什么、不负责什么」，并据此修订 Prompt Compiler 的 mandatory 语义。
> **关联**：[Prompt Compiler](PROMPT_COMPILER.md)、[插件模型](PLUGIN_MODEL.md)、[Harness 架构](HARNESS_ARCHITECTURE.md)

---

## 0. 一句话

> **Natter 是 harness：提供机制，不背书内容。**

harness 的职责是让插件能被加载、被隔离、被调度、被观察、能共存。
它不决定插件该说什么、该约束用户什么、业务上什么算「安全」。

---

## 1. 为什么

如果 host 强制一段内容（例如「安全条款必须存在且不可关闭」），它就在为这段内容的有效性背书。
但 host 并不知道具体部署是什么场景：

| 部署 | 需要的策略 |
| --- | --- |
| 公开 Discord 客服 bot | 严格内容策略、注入防护 |
| 内部运维 bot | 宽松，要能直接执行 |
| 个人助手 | 用户自定义 |
| 评测 / 回归环境 | 完全不要任何附加内容 |

host 写死一套，等于替所有业务做了同一个决定。这是越权，也是虚假的责任承担。

对比同类系统：

```text
nginx         不管你的网页有没有 XSS
Kubernetes    不管你的容器里跑什么
ASP.NET       不管你的中间件写成什么样
```

harness 只管机制。内容由装载者决定，后果也由装载者承担。

---

## 2. 三类责任

| 类型 | 归属 | 判据 |
| --- | --- | --- |
| **机制约束** | host | 不做，插件之间就无法共存 |
| **内容不变量** | 插件 / 配置 | harness 不背书任何内容 |
| **内容质量与业务正确性** | 插件 | host 无从判断，也不该判断 |

### 2.1 关键判据

```text
插件出问题，host「活下来并说清是谁的问题」   → 机制，host 必须做
插件出问题，host「去修好插件的输出」         → 越界，host 不该做
```

---

## 3. host 必须保留的（机制）

这些不是「为插件负责」，而是「让插件能共存」：

| 机制 | 不做的后果 |
| --- | --- |
| 生命周期与贡献注册 | 插件无法被发现与绑定 |
| ALC 隔离 | 插件间类型冲突、版本互踩 |
| 单插件超时 | 一个死循环拖死整个进程，其他插件陪葬 |
| 异常隔离 | 一个插件抛错导致整轮请求失败 |
| 资源配额（每插件公平份额） | 单个插件可以饿死其他所有插件 |
| id 命名空间隔离 | 插件之间相互覆盖 |
| 可归因（谁、什么码、多久） | 出事时无法定责 |

**配额要公平**：配额是自保，不是仲裁。上限应该是「每个 provider 各一份」，超限只影响自己，不能让先加载的插件占满全局额度。

**命名空间要隔离**：插件声明的 fragment id 自动带上 owner 前缀，从根上不产生冲突，host 不做「谁先谁赢」的仲裁。

---

## 4. host 不该保留的（内容）

| 内容层 | 说明 |
| --- | --- |
| prompt 里的安全条款 | 业务决定，不是 harness 决定 |
| 「不可关闭」的片段 | 任何无法关闭的东西，长期都会变成阻碍 |
| 业务规则、话术、约束 | 同上 |
| 片段语义审查 | 做不到，也不该做 |
| 替插件修输出 | 越界 |

### 4.1 为什么不做语义审查

插件是部署者主动安装的，属于可信来源。它的片段是**合法指令**，不是注入。

host 去判断「这句话像不像 prompt injection」：

- 判断不准，总有绕过；
- 一旦开始判断，就要为漏判负责；
- 真正的注入风险来自不受信输入（用户消息、工具结果），那属于 context 层的职责，不是 prompt 编译层的职责。

不受信插件的治理方式是**限制能力**（`PluginTrustLevel`），不是审查内容。

---

## 5. 默认值 ≠ 强制

这是本次修订的核心区分。

```text
默认提供      开箱可用，可替换、可关闭、可卸载     ← 正确
强制不可关闭   用户无法移除，只能绕                 ← 越权
```

harness 应该提供**默认实现**，而不是**不可变约束**。

---

## 6. Prompt Compiler 的迁移：mandatory → default-removable

### 6.1 现状（P1 实现）

```text
composer 注入 host.safety.untrusted-data
  Mandatory=true, CanDisable=false, Source=HostBuiltin
  → 强制排在最后
  → 插件与配置都无法移除
  → 关闭请求只产生 prompt.mandatory_ignored warning
assembler 在 system prompt 末尾再追加一次安全后缀
PromptBundle.SafetyAppended 用于避免重复追加
```

这套语义与 §0 的定位冲突：host 在为一段自己无法评估充分性的内容背书。

### 6.2 目标

```text
safety 是普通 fragment
  来源：默认 preset 里的一个片段（HostBuiltin 或插件贡献）
  默认：enabled
  可关闭：是
  可替换：是
  可叠加：是

composer 不再有 mandatory 特权
  不注入、不预留预算、不强制排序
  只做编译：解析、排序、预算、hash
```

### 6.3 行为变更对照

| 项 | 现状 | 目标 |
| --- | --- | --- |
| safety 来源 | host 代码内嵌 | 默认 preset 的片段 |
| 能否关闭 | 否（warning 后仍存在） | 是 |
| 排序特权 | 永远最后 | 按 `Order`，无特权 |
| 预算预留 | 先预留 | 与普通片段一致 |
| 关闭后的提示 | `prompt.mandatory_ignored` | 无（这是正常配置） |
| `PromptFragment.Mandatory` | 生效 | 移除 |
| `PromptFragment.CanDisable` | 生效 | 移除 |
| `PromptBundle.SafetyAppended` | 生效 | 移除 |
| 截断保底 `HostPromptSafety.Fit` | 保底保留后缀 | 移除（改为普通截断） |

### 6.4 保留项

`host#static` 仍然保留，但定位从「host 特权层」改为「**零插件 fallback**」：

```text
作用：没装任何 prompt 插件时，提供一份最小可用 prompt
性质：默认实现，不是不变量
内容：profile.systemPrompt + 可选 safety 片段（默认启用，可关）
```

否则空配置直接无法运行，那是功能缺失，不是「坚持原则」。

### 6.5 诊断变化

`ContextDiagnostics` / `PromptPlan` 不再需要表达「safety 是否被保留」，改为表达事实：

```text
presetId / presetVersion
fragments=[{id, source, order}]        ← 含 safety 片段本身
disabled=[{id, reason}]
pluginFragmentCount / providerCount
planHash
```

安全相关不再是 host 的「保证项」，而是**可观察的一项配置**。

---

## 7. 迁移影响面

| 面 | 影响 |
| --- | --- |
| `PromptSafety.cs` | 移除 mandatory 片段定义、`Fit` 截断保底 |
| `PromptCompilerContracts.cs` | 移除 `Mandatory` / `CanDisable`；`PromptBundle` 移除 `SafetyAppended` |
| `PromptComposer.cs` | 移除 mandatory 注入、排序特权、预算预留、`prompt.mandatory_ignored` |
| `DefaultContextImplementations.cs` | assembler 不再追加安全后缀；`host#static` preset 声明 safety 片段为普通片段 |
| `PromptFragmentSource.cs`（P3 聚合器） | 不再以 `Mandatory` 作为拒绝条件（字段消失） |
| 测试 | 「mandatory 不可关闭」类用例改写为「默认开、可关、关了仍能编译」 |
| 文档 | `PROMPT_COMPILER.md` 的 §0 第 8 条、§4.2、§9、§17 相关表述修订 |

### 7.1 需要重写的测试语义

```text
旧：mandatory 片段无法被 disabled 列表移除
新：safety 片段默认在 plan 中；disabled 后从 plan 消失；重新启用后恢复；hash 随之改变

旧：插件提供同 id 片段编译失败
新：id 命名空间隔离后不会冲突（owner 前缀）

旧：截断时安全后缀必须保留
新：截断按普通片段规则处理，safety 与其他片段同等对待
```

---

## 8. 代价

不美化：

1. **默认配置下可以零安全**：用户关掉 safety 就没有，这是他的选择；
2. **host 失去背书能力**：不能再声称「我强制加了安全条款」；
3. **升级兼容**：`Mandatory` 等字段移除，属于契约破坏性变更，需要版本处理；
4. **既有测试大改**：P1 的一部分既定语义被推翻；
5. **用户需要自己判断**：默认值好用，但不再是「保证」。

换回来的是：prompt 完全由插件和配置决定，host 只做编译和调度。这才是 harness。

---

## 9. 其他待重审的越权点

同一原则下，Natter 里还有几处「host 帮你想好了」，方向应一致，但本轮不动：

| 位置 | 越权表现 |
| --- | --- |
| `HostRecentWindowContextAssembler` | 写死 system → memory → summary → recent 的组装顺序与格式 |
| `ChatCommandRouter` | `/status` `/tasks` `/cancel` 等内置命令由 host 实现 |
| `AiChatResponder` | 对模型输出做 `<response>` 标签清洗 |
| `RuntimeProfileValidator` | 对业务字段做强校验 |

处理顺序建议：先完成 prompt 层，再评估 context 层，命令层最后。

---

## 10. 验收标准

- [ ] `PromptFragment` 不再有 `Mandatory` / `CanDisable`；
- [ ] composer 不再注入任何片段，不预留预算，不强制排序；
- [ ] `host#static` 的 safety 片段可 `enabled: false`，关闭后 prompt 仍正常编译；
- [ ] 关闭 safety 不产生 warning（属于正常配置）；
- [ ] 重新启用后片段恢复、`planHash` 改变；
- [ ] 插件 fragment id 自动带 owner 命名空间，不再存在「先到先得」仲裁；
- [ ] 配额按 provider 公平分配，单插件超限不影响其他插件；
- [ ] 诊断输出显示每个片段的来源与顺序，不显示正文；
- [ ] 默认 profile 在未关闭 safety 时，输出与当前版本一致（可回归对比）；
- [ ] 文档不再出现「host 强制安全策略不可移除」的表述。

---

## 11. 非目标

- 不做插件内容审查；
- 不做语义过滤 / 注入检测（那是 context 层与部署策略的事）；
- 不为插件的错误做业务补偿；
- 不提供「host 认证安全」之类的背书；
- 不改变机制层的时间/资源/隔离约束。

---

## 12. 一句话总结

> **host 保证机制完整、插件能共存、故障可归因；内容由插件与配置负责，harness 不背书。**
