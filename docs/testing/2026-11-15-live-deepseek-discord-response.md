# 开发侧处置记录：2026-11-15 真实 API 联调（Discord + DeepSeek）

> **状态**：Temporary / Active  
> **类型**：测试配对处置记录，不是长期设计文档  
> **对应测试**：[真实 API 联调：Discord + DeepSeek](2026-11-15-live-deepseek-discord.md)  
> **基线**：`HEAD=201e4ff` + 测试侧未提交临时改动  
> **归档**：BUG-001 / BUG-002 正式关闭并从干净 HEAD 复测后，本记录与测试记录一并归档

---

## 1. 结论

本次真实联调的结论应定为：

> **Live Integration: Conditional Pass**  
> **Product Acceptance: Blocked by BUG-001 / BUG-002**

测试本身有效：真实 DeepSeek、真实 Discord、真实插件加载、真实 CLI 部署都被覆盖了。  
但 `26/26` 是在**测试侧临时修改产品代码**后得到的，不等于干净 HEAD 通过，也不等于产品验收通过。

开发侧接受以下判断：

1. 测试报告可以作为真实环境基线保留；
2. BUG-001 / BUG-002 是产品缺陷，不是测试环境问题；
3. 测试侧临时补丁只作为定位证据，不直接视为最终修复；
4. 在正式修复并从干净 HEAD 复测前，不把 Discord + DeepSeek 标记为“已交付”；
5. OBS-001 / OBS-002 需要产品决策，不能只停留在测试记录里。

当前复核状态：

- `dotnet build Natter.slnx`：通过（1 个 Android/Java 版本解析 warning）；
- `Natter.Framework.Tests`：175/175 通过（新增加载上下文、自发消息、DeepSeek 重试守护）；
- 无凭据时 `Natter.Live.Tests`：全 skip；
- 测试侧临时产品补丁已由正式修复取代（见 §1.1）。

## 1.1 开发侧修复（本轮）

| 项 | 状态 | 正式实现 |
| --- | --- | --- |
| BUG-001 | **已修复，待全量 live 复测** | `PluginLoadContextOptions` + `PluginContractAssemblies` 显式契约白名单；`Natter.Chat.Discord` / `Natter.Feature.Ai.DeepSeek` / `Discord.Net.*` / `Microsoft.Extensions.AI*` 为私有；无 deps 时目录兜底并记录诊断；`3947c04` |
| BUG-002 | **已修复，待全量 live 复测** | 共享 [Natter.Plugin.targets](../../build/Natter.Plugin.targets) staging `lib/<Entry>.deps.json`；`NpsPlugin=true` 的插件统一走该 target；`plugins inspect` 对缺失 deps 给出 warning；构建期 Error 自检；`3947c04` |
| OBS-001 | **已实现** | DeepSeek `maxRetries` / `retryBaseDelayMs` / `retryMaxDelayMs`；仅重试 429/5xx/网络/超时；401/402/400 不重试；有界退避+jitter；耗尽由 responder fallback；`366d5d6` |
| OBS-002 | **已固化** | `IgnoreBots` / `IgnoreSelf` / `IgnoreSelfMessages` 默认三层过滤；新增上游 self 与 orchestrator self 守护测试；配置 schema 标注关闭风险；`d442326` |

已从干净构建（工作区产品代码为正式修复，测试资产已提交 `d442326`）验证：

```text
Natter.Framework.Tests: 175/175 passed
L1 DeepSeek live:       8/8 passed
L4 deployment live:     5/5 passed
L3/L3b E2E live:        2/2 passed
```

已覆盖 15/26 项 live；Discord transport（9）与 gateway ingress（2）本轮未重跑，因此仍不宣称 26/26。剩余验收：全量 26 项 live 从干净 HEAD 复跑、L5 人工闸门；在完成前保持 `Product Acceptance: Blocked`。

---

## 2. 处置总表

| ID | 类型 | 严重度 | 处置 | 责任侧 | 发布闸门 |
| --- | --- | --- | --- | --- | --- |
| BUG-001 | 插件加载上下文 | 阻塞 | **已修复**，正式契约白名单 + 私有依赖策略 | NPS / 插件运行时 | 待全量 live |
| BUG-002 | 插件打包 | 高 | **已修复**，共享 staging target 含 deps.json | 插件打包 / 构建 | 待全量 live |
| OBS-001 | DeepSeek 550 | 中 | **已实现**，有界重试 + responder fallback | AI provider / responder | 已关闭（策略） |
| OBS-002 | bot 自发消息 | 信息 | **已固化**，三层过滤 + 守护测试 | Discord transport | 已关闭 |
| ENV-001 | 测试频道权限 | 环境 | 不修产品；改进测试环境 | 测试侧 | 不阻塞 |
| ENV-002 | 单 bot 身份 | 环境 | 不修产品；补第二身份或人工闸门 | 测试侧 | 不阻塞 |

---

## 3. BUG-001 处置

### 3.1 结论

**接受，按产品缺陷处理。**

当前 `PluginLoadContext` 把所有 `Natter.*` 都当宿主共享。  
结果是插件目录里的 `Natter.Chat.Discord` 被错误地从默认 ALC 解析，而宿主并没有这个程序集，最终插件加载失败。

测试侧临时改成“固定契约程序集白名单 + `Microsoft.Extensions.AI*` 私有覆盖 + 目录兜底”，可以让测试继续，但不能直接作为最终架构。

### 3.2 不接受的方案

不把以下方案原样合并：

- 硬编码 10 个 `Natter.*` 程序集名；
- 用 `Microsoft.Extensions.AI` 字符串前缀做永久特判；
- 仅靠“目录里按文件名兜底”解决所有版本冲突；
- 在 `PluginLoadContext` 里维护一份会随产品演进而漂移的清单。

这些方案能解当前两个插件，但会在后续 Satori、Android 动态插件、第三方 provider 上重复出问题。

### 3.3 建议的正式方向

1. **共享程序集按契约白名单**  
   只共享宿主与插件必须保持类型同一性的契约程序集，例如：
   - `Natter.Abstractions`
   - `Natter.Plugin.Abstractions`
   - `Natter.Chat`
   - `Natter.Application`
   - `Natter.Domain`
   - `Natter.Cli.Abstractions`

2. **插件私有程序集不按前缀共享**  
   `Natter.Chat.Discord`、`Natter.Feature.Ai.DeepSeek`、`Discord.Net.*`、`Microsoft.Extensions.AI*` 应优先从插件目录解析。

3. **`Microsoft.Extensions.*` 动态判定**  
   先询问默认 ALC 能否解析；能解析且属于宿主契约版本则共享，否则使用插件自带副本。

4. **共享清单来源明确**  
   白名单应来自宿主显式配置/契约清单，不要散落在加载器里的字符串数组。  
   如果未来允许插件声明共享依赖，应通过 manifest/打包校验表达，而不是猜文件名。

5. **目录兜底只作为最后手段**  
   有 `deps.json` 时以依赖图为准；没有时才按目录文件名兜底，并记录诊断。

6. **测试要求**
   - 宿主/插件类型同一性；
   - 插件私有同名程序集优先；
   - 同一程序集不同版本；
   - 缺少 `deps.json` 的目录加载；
   - `Microsoft.Extensions.AI*` 私有依赖；
   - 加载失败时诊断信息可读。

测试侧已新增的 `PluginLoadContextTests` 可作为起点，但需要开发侧评审并补齐上述场景。

### 3.4 验收

- 干净 HEAD 下，CLI 能加载 Discord 和 DeepSeek 文件插件；
- 不依赖测试侧临时白名单；
- 175 框架回归 + 加载上下文/自发消息/重试守护测试通过；
- L3b / L4 live 用例从干净 HEAD 通过；
- 加载失败时不再出现“宿主没有该程序集”的假共享错误。

---

## 4. BUG-002 处置

### 4.1 结论

**接受，按产品打包缺陷处理。**

插件 staging 把 DLL 复制到 `lib/`，但没有把 `<AssemblyName>.deps.json` 放到入口程序集旁边。  
`AssemblyDependencyResolver` 取不到依赖图，`Discord.Net.*`、`Newtonsoft.Json` 等私有依赖无法解析。

测试侧在两个 sample csproj 里补了 deps.json，这只能证明方向正确，不是产品修复。

### 4.2 正式方向

1. **统一打包器负责 deps.json**  
   所有 NPS 插件包都必须包含：
   - `plugin.json`
   - `config.schema.json`
   - `lib/<EntryAssembly>.dll`
   - `lib/<EntryAssembly>.deps.json`
   - 私有依赖 DLL

2. **不要只改 sample csproj**  
   sample 的 staging target 可以保留，但必须有一个共享的打包/校验步骤，避免每个插件重复实现。

3. **`plugins inspect` / package validator 检查**  
   缺少 deps.json 时给出明确诊断，而不是等到运行期 `FileNotFound`。

4. **明确“目录加载”契约**  
   如果允许无 deps.json 的目录加载，必须把目录兜底写成正式契约并测试；  
   如果不允许，就拒绝加载并给出可操作错误。

5. **测试要求**
   - 打包后 `lib/` 中存在 deps.json；
   - 文件插件从 `lib/` 加载成功；
   - 私有依赖可解析；
   - 缺 deps.json 时有明确错误；
   - 不依赖 sample 特定 target。

### 4.3 验收

- 干净 HEAD 下，`Natter.Sample.Plugin.Discord` 和 `Natter.Feature.Ai.DeepSeek` 的 `lib/` 均包含 deps.json；
- L3b / L4 从干净 HEAD 通过；
- 打包校验测试覆盖缺 deps.json 的负面路径；
- 文档更新插件包结构约定。

---

## 5. OBS-001 处置：DeepSeek 550

### 5.1 结论

**接受为产品策略问题，不能只靠测试侧重试。**

测试侧对 `Retryable` / `HttpRequestException` 做了最多 3 次有限重试，避免瞬时 550 让用例失败。  
但真实 responder 还没有对应策略。

### 5.2 需要决策

- 550 是否等价于 503/可重试？
- 哪些错误可重试：429、5xx、网络异常、超时？
- 重试几次、退避多久、是否有 jitter？
- 重试是否可能产生重复计费/重复回复？
- 重试耗尽后，Discord 用户看到什么？
- 是否记录 usage/错误指标？

### 5.3 建议

- 只对明确的瞬时错误重试；
- 有上限、有退避、有取消；
- 不在已经产生用户可见输出后重试；
- 错误映射与重试分离测试；
- responder 层返回统一 `unavailable`，不把 DeepSeek 原始错误发给用户；
- 记录 provider error code、latency、attempt count，不记录 key/prompt。

### 5.4 验收

- 429/5xx/timeout 有独立测试；
- 重试次数与退避可配置；
- 重试耗尽有降级文案；
- 不产生重复 Discord 回复；
- live 测试不再依赖测试侧重试来掩盖产品缺口。

---

## 6. OBS-002 处置：bot 自发消息

### 6.1 结论

**接受为设计前提，固化为默认安全行为。**

真实探针确认：Discord bot 会收到自己发送的 `MESSAGE_CREATE`。  
如果 `ignoreSelf=false`，AI 回复可能被当成新输入，形成自问自答。

### 6.2 处置

- `DiscordOptions.IgnoreBots` 默认 `true`；
- `DiscordOptions.IgnoreSelf` 默认 `true`；
- `ChatOrchestrationOptions.IgnoreSelfMessages` 默认 `true`；
- 文档明确三层过滤的关系；
- 增加守护测试：bot 自发消息、`ignoreSelf=false` 的风险提示、orchestrator 层过滤；
- 配置 schema 不提供“轻易关闭”的默认值；如果允许关闭，必须显式标注危险。

### 6.3 验收

- 默认配置下 `selfEchoEvents=0`；
- 三层过滤都有测试；
- Discord 设计文档、AI provider 文档、配置说明一致。

---

## 7. ENV-001 / ENV-002 处置

不修产品。

- ENV-001：测试频道缺少 Manage Channels / Manage Webhooks，导致不能并行、不能造人类消息；
- ENV-002：只有单 bot 身份，真人触发只能人工完成。

测试侧改进：

- 单独测试 guild/频道；
- 给测试 bot 必要的频道管理权限；
- 引入第二 bot 或专用测试用户；
- 或保留 L5 人工闸门，但每次发布前至少跑一次。

这些是测试基础设施问题，不进入产品缺陷列表。

---

## 8. 临时改动处置

测试侧临时改动**不直接合并**，按以下方式处理：

| 文件 | 处置 | 说明 |
| --- | --- | --- |
| `src/Natter.Infrastructure/Plugins/PluginLoadContext.cs` | **已由正式实现取代** | 契约白名单/私有依赖/目录兜底/诊断 |
| `samples/Natter.Sample.Plugin.Discord/Natter.Sample.Plugin.Discord.csproj` | **已由共享 target 取代** | `NpsPlugin=true` + `build/Natter.Plugin.targets` |
| `src/Natter.Feature.Ai.DeepSeek/Natter.Feature.Ai.DeepSeek.csproj` | **同上** | 同上 |
| `tests/Natter.Live.Tests/` | 保留 | 测试资产，提交前检查无密钥 |
| `tests/Natter.Framework.Tests/PluginLoadContextTests.cs` | 保留并扩展 | 作为 BUG-001 回归起点 |
| `scripts/live-test.sh` | 保留 | 检查无密钥输出 |
| `docs/LIVE_TEST_PLAN.md`、`docs/testing/` | 保留 | 测试资产 |

原则：

1. 测试资产与产品修复分开提交；
2. 产品修复必须有 issue/PR 和评审；
3. 临时补丁回退后，L3b/L4 重新失败是预期，不是回归；
4. 不允许把“测试侧改过所以通过”写进发布说明。

---

## 9. 发布闸门

在以下条件全部满足前，不把 Discord + DeepSeek 标记为“已交付”：

- [x] BUG-001 正式修复，PluginLoadContext 不再按 `Natter.*` 前缀共享；
- [x] BUG-002 统一打包流程包含 deps.json；`plugins inspect` 缺失诊断；
- [x] 测试侧临时产品补丁已由正式实现取代；
- [x] 从干净构建成功；
- [x] 175/175 框架回归通过；
- [ ] 26/26 live 用例从干净 HEAD 通过（L4 5/5、L3/L3b 2/2 已通过，全量待复跑）；
- [ ] L5 人工闸门至少通过一次；
- [x] OBS-001 重试/降级策略落地；
- [x] OBS-002 三层过滤有守护测试和文档；
- [ ] 真实 `.env` 已清理或轮换，仓库无密钥；
- [ ] 测试报告与处置记录已更新最终状态。

---

## 10. 安全与清理

- `artifacts/live/.env` 含真实凭据，虽然被 `.gitignore` 忽略且权限 600，仍按敏感文件处理；
- 测试结束轮换或删除测试 token/API key；
- 不把 `.env` 复制到 issue、聊天记录、CI 日志或归档包；
- live 测试只在本地/受控 runner 运行；
- 归档前扫描 `sk-`、bot token 前缀、`Authorization` 头；
- 插件包中的 `config.json` 只允许 secret 引用，不允许明文 key。

---

## 11. 下一轮覆盖

本轮未覆盖，不能算“全部通过”：

- slash command 点击/真实交互；
- 附件上传与下载；
- streaming；
- 429/5xx 主动注入；
- 长时间断线重连；
- 多 guild / 多频道；
- thinking=enabled 的 E2E；
- 真实用户 L5 全链路。

建议下一轮优先补：

1. slash interaction（Discord 的核心交互路径）；
2. 429/5xx 降级；
3. 断线重连；
4. thinking disabled/enabled 对比；
5. 附件能力按 `Capabilities.Attachments` 真实声明。

---

## 12. 归档规则

本文是临时配对文档，不是长期规范。

归档条件：

- BUG-001 / BUG-002 关闭；
- 干净 HEAD 复测通过；
- 本记录第 9 节闸门全部完成；
- 测试报告补充最终结论。

归档动作：

1. 将本文与测试记录一起移入 `docs/testing/archive/2026-11/`（或团队约定的归档目录）；
2. 在两份文档头部标记 `Archived`；
3. 更新 [测试记录索引](README.md)；
4. 保留 issue 编号与最终提交 SHA，保证可追溯；
5. 不把临时补丁 diff 作为长期文档保留，只保留结论、根因和验证证据。

---

## 13. 待确认

- BUG-001 共享程序集清单的最终来源：宿主配置、manifest，还是独立的契约包？
- BUG-002 是否强制所有插件包包含 deps.json，还是允许目录加载兜底？
- OBS-001 550 的重试语义和用户降级文案；
- OBS-002 是否允许 `ignoreSelf=false`，如果允许，如何防止自问自答；
- 下一轮 live 是否引入第二 bot / 专用测试用户；
- 测试侧临时补丁由谁转正式 PR、在哪个里程碑关闭。
