# Natter 全仓库代码审查报告

> **⚠️ 归档文档 · 历史记录，勿据此实现。**
> 本文件描述的阶段已完成或已被取代，仅用于追溯当时的决策与证据。
> 现行文档见 [docs/README.md](../README.md)。

> **审查日期**：2026-11-22
> **基线**：`671097b`（工作区 clean；`git status --short` 无输出）
> **范围**：`src/`、`plugins/`、`samples/`、`tests/`、`scripts/`、`deploy/`、`.github/`、`config/`、`docs/`
> **方式**：只读静态审查 + 本地构建/测试实测；**未修改任何源码、配置、测试或脚本**
> **产物**：本报告 + 六份分领域详细报告（[附录 A](#附录-a分领域详细报告)）

> **修复进度（2026-11-22 更新）**：12 项 S1 已全部修复并验收。详见
> [最终 S1 验收报告](../testing/2026-11-22-final-s1-acceptance.md) 与
> [审查修复验收](../testing/2026-11-22-review-acceptance.md)。S2/S3 未动。

---

## 修复状态（2026-11-22）

| 项 | 状态 | 主要提交 |
| --- | --- | --- |
| S1-01 卸载路径穿越 | ✅ 已修 | `4c3c425` |
| S1-02 代理路径 SSRF | ✅ 已修 | `670b3b6` |
| S1-03 Satori 出站注入 | ✅ 已修 | `359f0d0` |
| S1-04 validate diff 泄漏 | ✅ 已修 | `ebf54de`、`5736d7a` |
| S1-05 会话 lease 覆盖 | ✅ 已修 | `cb537e3` |
| S1-06 编排器 fire-and-forget | ✅ 已修 | `cb537e3` |
| S1-07 结果误发 Task failed | ✅ 已修 | `cb537e3` |
| S1-08 Android INTERNET | ✅ 已修 | `67c02e6` |
| S1-09 CI 漏构建漏测试 | ✅ 已修 | `82f6866` |
| S1-10 soak 假 PASS | ✅ 已修 | `82f6866` |
| S1-11 CLI 抢 bot 会话 | ✅ 已修 | `7a6f054`、`2bf08f8` |
| S1-12 插件信任未传播 | ✅ 已修 | `27357e1`、`06629d7` |

### 审查外的额外修复

| 项 | 原因 | 提交 |
| --- | --- | --- |
| Discord 网关静默失联 3h19m | 生产事故，见 [事故记录](../operations/2026-11-22-discord-gateway-stall.md) | `c81c4c4`、`1eb283a` |
| Prompt mandatory 语义迁移 | harness 责任边界调整，见 [责任边界](../HARNESS_RESPONSIBILITY.md) | `f8f17b2` … `8673e89` |
| 发布 staging 复核 | 旧产物会让修复在实机失效 | `aee1b0b` |
| 发布检查增加 staging 断言 | OBS-REVIEW-STAGING | `aee1b0b` |

### 当前验证基线

```text
dotnet build Natter.slnx -c Release   0 error（仅既有 Android XA0033）
Framework                             727/727
Prompt Library                        36/36
WebConsole                            314/314
```

### 未处理

- **S2 74 项、S3 45 项**：尚未排期。
- **真实平台验证**：Discord 网关重建、6 插件宿主启动、Satori 元素类型，均只经 fake 验证。
- **部署产物**：`/opt/natter` 仍是旧构建，需重新 staging 才能让上述修复生效。

---


## 0. 结论摘要

**一句话**：Natter 的内核实现质量明显高于其验证链与运维链的质量。代码层面没有发现远程可利用的严重漏洞，但存在 12 项高优先级缺陷，集中在**插件存储的边界校验**、**管理面的脱敏一致性**、**聊天/任务的并发与生命周期语义**，以及**"绿灯不等于验证过"的 CI/soak 假阳性**。

| 维度 | 评价 | 说明 |
| --- | --- | --- |
| 分层与依赖方向 | 良 | 依赖向内核收敛，`Abstractions` 无出向依赖；`Infrastructure → Cli.Abstractions` 有轻微反向耦合 |
| 领域与应用建模 | 良 | DDD 基元、Specification、管道行为齐全；`Specification` 组合表达式不可被 ORM 翻译（S3） |
| 聊天/NLMP 模型 | 中上 | 模型完整、降级/分片/幂等/去重设计到位；出站转义、会话串行、幂等竞态有真实缺陷 |
| 插件体系 | 中 | ALC/清单/依赖图/权限/签名设计完整；卸载路径穿越、安装非原子、信任结果丢弃是硬伤 |
| 管理面（WebConsole） | 良 | 回环绑定、CSRF、角色、只读开关、命令 allowlist 均有效；脱敏存在一处可绕过 |
| CLI/运维/部署 | 中 | 功能完整，但"任何命令都拉起完整 host"、soak 判定假阳性、Android Release 清单缺网络权限 |
| 测试 | 中上 | 936 个用例、断言密度 3.66/用例；存在假验收、弱断言、超时保护缺失与覆盖盲区 |
| 文档 | 中 | 体量大、可追溯性好；数字与能力声称大量过期，与实现互相矛盾 |
| 构建/供应链 | 中下 | 全仓关闭分析器与 NuGet 审计；CI 只覆盖 3/4 测试套件，从不构建解决方案 |

**严重度定义**

| 级别 | 定义 | 数量 |
| --- | --- | --- |
| **S0 阻断** | 非特权/远程可触发，或静默数据损坏 | **0** |
| **S1 高** | 特定配置或操作下造成安全、数据、核心功能错误，或验证链失效使错误可静默合入；下一迭代必修 | **12** |
| **S2 中** | 可维护性、性能、边界鲁棒性、验证链可信度 | **74** |
| **S3 低** | 风格、死代码、诊断细节 | **45**（合并计数，见附录） |

> 判定说明：本报告把"需要本地同等权限主动执行危险参数"的问题定为 S1 而非 S0；把"治理声明被绕过"（插件权限）定为 S2，因为 NPS 权限是**治理而非沙箱**——插件代码本身就在宿主进程内以完全信任运行，绕过权限不构成额外的能力提升。

---

## 1. 审查方法与证据等级

1. **单元划分**：按职责拆成 6 个互不重叠的审查单元并行精读，主审对所有 P1 结论逐条复核源码。
2. **证据等级**：
   - `实测`：本地运行命令/测试得到的输出（构建、测试、清单、脚本探针）。
   - `代码核对`：直接阅读实现并交叉验证调用链。
   - `静态推断`：由代码结构推断，未构造运行期复现（文中标注"待确认"）。
3. **约束**：只读。构建/测试产生的 `bin/`、`obj/` 均在 `.gitignore` 内；新增文件只有本报告与附录目录 `docs/review/2026-11-22/`。
4. **未覆盖**：真实 Discord / DeepSeek / 钉钉 / 星火平台行为（40 个 live 用例因无凭据全部 skip）；Android 真机；24h soak 实跑。

---

## 2. 实测基线

### 2.1 仓库规模

| 指标 | 数值 |
| --- | --- |
| 解决方案工程数 | 28（`Natter.slnx`） |
| 手写 `.cs` 文件 | 407（src 257 / tests 109 / plugins 24 / samples 17） |
| 产品代码行数 | 42,028（src + plugins + samples） |
| 测试代码行数 | 27,152 |
| 文档 | `docs/` 下 71 个 Markdown + `README.md` |
| 脚本 | `scripts/` 19 个、`deploy/` 8 个 |
| Git 跟踪文件 | 579；`bin/`、`obj/`、`artifacts/` 均未入库 |

### 2.2 构建与测试（实测）

```text
$ dotnet build Natter.slnx -c Release
Build succeeded. 1 Warning(s), 0 Error(s)
warning XA0033: Failed to get the Java SDK version ... (Android workload，本机 JDK 21 输出格式导致，非产品代码问题)
```

| 套件 | 结果 | 耗时 |
| --- | --- | --- |
| `Natter.Framework.Tests` | **557 / 557 通过** | 4 s |
| `Natter.Plugin.WebConsole.Tests` | **303 / 303 通过** | 1 s |
| `Natter.Plugin.PromptLibrary.Tests` | **36 / 36 通过** | 221 ms |
| `Natter.Live.Tests` | 0 通过 / **40 跳过**（无 `NATTER_LIVE` 凭据） | 51 ms |
| **合计** | **936 个用例，896 实跑通过，40 跳过** | — |

- 测试属性：722 个 `[Fact]`、31 个 `[Theory]`、174 组 `[InlineData]`；断言约 3,421 次（3.66/用例）。
- **README 声称"264 个框架级测试"，实测 557，偏差 −52.6%**（见 S2-F9）。

### 2.3 值得肯定的工程实践（先说好的）

- **SQL 全参数化**：`SqliteConversationStore`、`SqliteContextStores`、`SqliteTaskStore` 无字符串拼接 SQL；唯一的插值是 `PRAGMA user_version = {int}`，不构成注入。
- **无 shell、无命令拼接**：`CliAdminCommandRunner` 在进程内解析 argv 并复核 allowlist，不启动子进程。
- **路径穿越防护**：管理面配置路径使用 `Path.GetFullPath` + 带分隔符的前缀校验（`HostAdminConfigStore.cs:481-488`），并拒绝符号链接末级。
- **WebConsole 纵深防御**：回环绑定强校验、`SameSite=Strict`+`HttpOnly` Cookie、CSRF 头、角色二次校验、请求体预算、静态资源白名单、审计。
- **凭据处理**：token 只以 `${secret:NAME}` 引用形式存在；`EnvironmentSecretProvider`；日志脱敏规则含 PEM/AK/SK/Bearer；常量时间比较。
- **取消与 `ConfigureAwait(false)` 的纪律**：库代码普遍正确使用，未见 `async void`、`.Result`、`Thread.Sleep`。
- **测试与文档纪律**：每个功能有验收记录、缺陷编号（BUG/OBS/ENV/FLAKE）、可复制命令。

---

## 3. S1 高优先级缺陷（12 项，逐条）

### S1-01 插件卸载不做 id 校验，可递归删除任意目录

- **位置**：`src/Natter.Plugin.Store/PluginStore.cs:415-455`（`UninstallAsync`）、`:60-64`（路径拼接）、`:553-563`（`TryDeleteDirectory`）；入口 `src/Natter.Cli.Core/Commands/PluginStoreCommands.cs:176-196`
- **证据**：

  ```csharp
  public string GetPluginDataDirectory(string pluginId)
      => Path.Combine(DataDirectory, pluginId);   // pluginId 为绝对路径时直接返回该路径
  ...
  if (deleteData)
      TryDeleteDirectory(GetPluginDataDirectory(pluginId));   // 位于 record is not null 之外
  ```

  `UninstallAsync` 只在 `record is not null` 分支里移动安装目录，但 `--delete-config` / `--delete-data` 在插件未安装时同样执行；CLI 只做 `IsNullOrWhiteSpace` 检查，不校验 id 形状。
- **影响**：`natter plugins store uninstall /home/ndm/important --delete-data` 会对该绝对路径执行 `Directory.Delete(recursive: true)`。控制台命令面若把 `plugins` 放进 allowlist，则同一 API 也经由管理面暴露给 admin 会话。属于同权限下的数据毁灭性误用，且该 API 是公开库方法，未来任何传入"来自清单/请求的 id"的调用方都会把它升级为真正的越权。
- **修复方向**：在 `PluginStore` 内统一 `ValidatePluginId`（与 manifest 的 `^[a-z0-9]...` 规则一致）；删除前 `Path.GetFullPath` + 目标根前缀校验；仅当 `record is not null`（或显式 `--force`）才允许删除数据目录。

### S1-02 配置 HTTP 代理时，SSRF 的连接期地址校验被静默关闭

- **位置**：`src/Natter.Infrastructure/Chat/ChatCompositionModule.cs:27-78`
- **证据**：

  ```csharp
  var targetHost = context.InitialRequestMessage.RequestUri?.DnsSafeHost;
  var direct = string.IsNullOrEmpty(targetHost)
               || string.Equals(endpoint.Host, targetHost, StringComparison.OrdinalIgnoreCase);
  ...
  if (direct) { /* 逐地址 IsBlockedAddress 检查 */ }
  ```

  配置 `HTTP(S)_PROXY` 后，`ConnectCallback` 收到的是**代理端点**，`direct=false`，整段私网/元数据地址校验被跳过。工具层 `HttpFetchTool.ValidateAddressAsync` 仍会做一次本地 DNS 预检，但注释所声称的"DNS rebinding / redirect 防线"依赖的正是这段被跳过的代码。
- **影响**：在设置出网代理的部署里（本仓 `deploy/docker/compose.yaml` 明确支持 `HTTP(S)_PROXY`），若目标域在预检后改解析到私网，或代理侧以自己的 DNS 解析内网域名，`http-fetch` 工具可触达云元数据/内网地址。默认工具白名单为空，降低了现实可利用性，但防守承诺与实现不符。
- **修复方向**：该 handler 固定 `UseProxy = false`（工具只做只读外网抓取，不需要代理），或在 `direct == false` 时显式拒绝并返回 `proxy_not_supported`。

### S1-03 Satori 出站文本未转义，可注入元素与 @全体

- **位置**：`src/Natter.Chat.Satori/SatoriCommandMapper.cs:106-137`（`SatoriOutboundContent.Format`）；入站等价语义 `src/Natter.Chat.Satori/SatoriContentMapper.cs:59-83`
- **证据**：出站把 `content.Text` 原样拼进 Satori 元素串，只对附件 URL 做属性转义：

  ```csharp
  var builder = new StringBuilder(text);
  builder.Append('<').Append(tag).Append(" src=\"").Append(EscapeAttribute(attachment.Url))...
  ```

  入站映射把 `<at type="all"/>` 解析为 `MentionKind.Everyone`。也就是说 Satori 平台会把文本里的 `<at .../>` 当成真实元素。
- **影响**：模型输出（或任何上游文本）中出现 `<at type="all"/>` 即触发真实 @全体；`<img src="http://...">` 可强制加载任意外部媒体。`ChatMentionGuard` 只检查结构化 `Content.Mentions`，对文本注入无效，等于绕过出站 mention 护栏。
- **修复方向**：文本段先做 XML 文本转义（`&`/`<`/`>`）再拼接元素；或改用"元素列表"而不是字符串拼装；出站前对映射后的元素再做一次 mention 策略校验。

### S1-04 WebConsole `/validate` 回传未脱敏 diff，viewer 可读到明文 secret

- **位置**：`plugins/Natter.Plugin.WebConsole/WebConsoleRequestHandler.cs:795-835`；`WebConsoleAdminGateway.cs:202-244`、`:579-581`；授权分类 `WebConsoleAuthorization.cs:36-48`
- **证据**：读取路径对内容做 `WebConsoleRedaction.RedactSecrets`（`WebConsoleAdminGateway.cs:176`），但 `MapDiff` 直接返回宿主 diff 的原始行：

  ```csharp
  private static WebConsoleAdminDiffView? MapDiff(AdminConfigDiff? diff)
      => new(diff.Added, diff.Removed, diff.Changed, diff.Truncated, diff.Lines);
  ```

  而 `POST /api/config/{id}/validate` 因路径以 `/validate` 结尾被归类为 `Read`，**viewer 角色可调用，且不受 `readOnly` / `allowConfigWrite` 限制**。提交 `{"content":"{}"}` 后，diff 的 `-` 行即当前文档内容（受 200 行 / 32 KiB 上限截断）。
- **影响**：只读凭据持有者可获得未脱敏的配置原文（含明文 token/PEM 私钥），绕过了控制台唯一的脱敏边界。分领域报告的探针在本地复现了 `sk-plaintextsecret1234` 泄漏。
- **修复方向**：`MapDiff` 的 `Lines` 逐行经 `WebConsoleRedaction` 处理；或将返回宿主内容的 validate 归为 `Manage`；或对 `Read` 角色不返回 diff。

### S1-05 "按会话串行"没有覆盖历史读写，回复可乱序、上下文可错位

- **位置**：`src/Natter.Feature.Ai/AiChatResponder.cs:163-211`（`GenerateAsync`）、`:206`（进入串行区）、`:104`（写历史）
- **证据**：历史读取与上下文装配在 `_gate.AcquireAsync` **之前**完成，写历史在其**之后**：

  ```csharp
  messages = await _contextPipeline.AssembleAsync(...);          // 读历史
  using var lease = await _gate.AcquireAsync(conversationKey);   // 才进入串行区
  var text = await CallAsync(messages, options, cancellationToken);
  ...
  await _history.AppendTurnAsync(key, text, reply, ...);          // 串行区之外写历史
  ```

- **影响**：同会话两条并发消息基于同一份旧历史各自生成，两个回复的落库顺序与发送顺序都可能反转；与 `AI_PROVIDER_DESIGN.md` 中"per-conversation 串行"的描述不一致。配合 S1-06 的无界并发，同会话叠话时串台概率显著上升。
- **修复方向**：把"读历史 → 生成 → 写历史 → 组装命令"整体放进会话 lease；或引入会话版本号做乐观并发并拒绝陈旧回复。

### S1-06 编排器对每条事件 fire-and-forget：无界并发、乱序、停机不排空

- **位置**：`src/Natter.Application/Chat/ChatOrchestrator.cs:155-168`（`OnNext` → `_ = HandleAsync(value)`）、`:46-55`（`StopAsync` 只退订）
- **证据**：

  ```csharp
  public void OnNext(ChatEvent value) => _ = HandleAsync(value);
  ```

- **影响**：
  1. 消息洪峰直接放大为无界并发的 AI 调用与出站请求（成本与内存双风险，且无每条消息的成本上限）；
  2. 同会话事件无顺序保证（`message.created` 与 `message.updated` 可乱序处理）；
  3. `StopAsync` 不等待在途处理，进程退出时回复与历史写入被截断；
  4. 处理异常只记日志，调用方无从感知。
- **修复方向**：`ChatOrchestrator` 改为"有界 Channel + N 个消费 worker"；按会话键做一次性投递/串行；持有 host 生命周期 `CancellationToken`；`StopAsync` 排空在途任务。

### S1-07 任务"未真正运行"时仍向用户发送 `"Task failed."`

- **位置**：`src/Natter.Application/Chat/Tasks/TaskDispatcher.cs:99-222`（`finally` 在 `:206-217`）、`:112-137`（租约/版本冲突提前返回）、`:302-320`（状态到文案的映射）
- **证据**：租约未取得或版本冲突时函数直接 `return`，被注释判定为"不是失败"；但 `finally` 无条件调用 `SendResultOnceAsync(task)`，而它重新读库后把 `Pending/Running` 映射为：

  ```csharp
  AgentTaskStatus.Pending or AgentTaskStatus.Running or AgentTaskStatus.WaitingForInput => "Task failed.",
  ```

  发送成功后被写入 `_resultsSent`，真正的终态结果不会再发。
- **影响**：多实例/恢复/并发推进场景下，用户收到一条假的失败消息，且真实结果被永久抑制。属于"静默错误语义"——比崩溃更难排查。
- **修复方向**：只在 `MarkTerminal` 之后发送结果；用局部 `bool terminalReached` 控制 `finally`；"被别处推进"应作为 `skipped` 而非发送。

### S1-08 Android Release 清单没有 `INTERNET` 权限，发布包无法联网

- **位置**：`src/Natter.Hosting.Android/Properties/AndroidManifest.xml:1-6`
- **证据（实测）**：源清单只有 `<application>`，无任何 `<uses-permission>`；生成的清单中 **Debug 有 1 个 `INTERNET`（.NET Android 为调试器自动注入），Release 为 0 个**：

  ```text
  obj/Debug/net10.0-android/android/AndroidManifest.xml   -> uses-permission INTERNET: 1
  obj/Release/net10.0-android/android/AndroidManifest.xml -> uses-permission INTERNET: 0
  ```

  `scripts/publish-android.sh` 默认走 Release。
- **影响**：Android 发布包加载即无法访问 Discord / DeepSeek / 钉钉，宿主的核心能力在 Release 上不可用；而 Debug 测试一切正常，问题只会在发布后暴露。
- **修复方向**：清单显式声明 `android.permission.INTERNET`；补前台服务/通知（见附录 [05-cli-ops.md](../review/2026-11-22/05-cli-ops.md) R-51）与真机冒烟；CI 增加"Release 清单必须含 INTERNET"的断言。

### S1-09 CI 漏构建/漏测试：1/4 套件与 2 个产品工程不在任何验证路径上

- **位置**：`.github/workflows/ci.yml:44-49`（restore 列表）、`:52-84`（build/test）、`:86-99`（依赖扫描）
- **证据**：CI 只 restore/build/test `Natter.Cli`、`Natter.Framework.Tests`、`Natter.Live.Tests`、`Natter.Plugin.WebConsole.Tests`。`Natter.Plugin.PromptLibrary`、`tests/Natter.Plugin.PromptLibrary.Tests`、`Natter.Hosting.Android` 在整个仓库的 workflow 中零引用（`grep -rn PromptLibrary .github` 无命中），也从不执行 `dotnet build Natter.slnx`。
- **影响**：PromptLibrary 插件与 36 个用例可长期编译失败而主干全绿；任何"只被未构建工程引用"的破坏无法被 CI 发现；与 README/DEVELOPMENT 中"CI 全量验证"的印象不符。
- **修复方向**：CI 增加 `dotnet build Natter.slnx`（Android 可用条件 job 或单独 workflow），补齐 PromptLibrary 测试；叠加 `dotnet format --verify-no-changes`、分析器与覆盖率门槛。

### S1-10 soak 判定存在两条假 PASS 路径，24h 验收可能对"已死主机"判绿

- **位置**：`scripts/soak-report.py:115-118`（`error` 样本被跳过）、`:142-145`（覆盖率分母）
- **证据**：

  ```python
  if row.get("error"):
      audit.errors.append(row)
      continue            # 该样本不进入时间线
  ...
  first, last = samples[0].at, samples[-1].at
  window = (last - first).total_seconds()
  expected = int(window // interval) + 1          # 分母是"观测到的跨度"
  coverage = round(100.0 * len(samples) / expected, 1)
  ```

  头部的 `intervalSeconds` / `durationSeconds` 只被回显，不参与判定；`errorSamples` 只打印、不进 `reasons`/`result`。
- **影响**：主机在 24h 里死了 23h，只要残留两三个可解析样本，覆盖率仍可 100%、gap 为零、结论 PASS。仓库把 24h soak 作为发布闸门（`docs/P4_TIER1_PRODUCTIZATION.md:457` 仍标"未执行"），这条链路实际不可信。
- **修复方向**：`error` 样本进入时间线并计为 gap；`expected` 用头部声明的 `durationSeconds/intervalSeconds`；`status != healthy`、`unknown`、`offline` 计入结论；补充覆盖上述三条的 self-test。

### S1-11 除 `health --offline` 外所有命令都会拉起完整 host 并抢占 bot 会话

- **位置**：`src/Natter.Cli.Core/Cli/NatterCli.cs:96-124`；单实例锁只覆盖 `serve`（`:41-44`）
- **证据**：`if (!offlineHealth) await host.StartAsync(runToken);` —— 因此 `natter plugins list`、`upstreams --json`、`ai status`、`discord status` 等只读诊断命令都会激活插件、连接 Discord 网关。
- **影响**：主实例运行期间执行巡检命令会建立第二条网关会话，导致其中一条被 Discord 断开；`docs/OPERATIONS_RUNBOOK.md:119-121` 仍把这类命令写进"启动后检查"代码块（虽然同段 `:134` 有警告），说明工具行为与运维直觉不符。这是"读命令产生写副作用"的典型设计缺陷。
- **修复方向**：按命令声明"是否需要启动 host"（默认不启动，只有 `serve`/在线 `health`/`discord status` 等显式声明）；或让所有启动 host 的命令都参与单实例锁并支持 `--offline`。

### S1-12 插件 store 的签名/信任结果在运行时被丢弃，信任策略形同虚设

- **位置**：`src/Natter.Plugin.Store/PluginStore.cs:184-185`（写入 `Trust`/`SignerKeyId`）、`:83-108`（只返回目录）；消费方 `src/Natter.Hosting.Android/AndroidPluginHost.cs:66`；`src/Natter.Infrastructure/Composition/NatterApplicationBuilder.cs:329-334`（一律使用 `_npsOptions.FilePluginTrustLevel`）
- **证据**：`installed.json` 记录 `Trust = "signed" \| "unsigned"` 与 `SignerKeyId`，但激活路径（当前为 Android 动态插件宿主）只消费 `GetActivePluginDirectories()` 的目录列表，从不读取 `record.Trust`；所有文件插件按 `Local` 处理，而 `NpsPluginOptions.AutoGrantLocalPermissions` 默认 `true`（`NpsPluginOptions.cs:12-16`）。
- **影响**：即使运维只信任签名包、或把 `FilePluginTrustLevel` 调成 `Signed`，已安装包仍以 Local 信任激活并自动获得清单声明的全部权限（含 `admin.command.execute`）。安装后也不再复验哈希，落地后篡改 DLL 照样加载（S2-C9）。信任模型只有"安装时一次"的效力。
- **修复方向**：store 的 `Trust/SignerKeyId` 传入候选；启动时按策略校验；提供 `RequireSignedPlugins` 开关并默认拒绝 unsigned；启动时校验清单摘要。

---

## 4. S2 中优先级问题

> 完整证据与代码片段见分领域报告（附录 A）。下表按领域归并，同一根因只列一次；合计 74 项（22 + 10 + 15 + 5 + 13 + 9）。

### 4.1 聊天 / AI / 平台适配器（22 项）

| ID | 位置 | 问题 | 影响 |
| --- | --- | --- | --- |
| S2-A1 | `ChatCommandDispatcher.cs:53-75`；`DiscordChatUpstream.cs:147-157` 等三处 | 幂等"检查-使用"无预留，只缓存成功 ack | 同 key 并发可双发；失败重试不入缓存 |
| S2-A2 | `DingTalkStreamTransport.cs:95` | 每条回复 `new HttpClient` | 连接池/TLS 不复用、TIME_WAIT 堆积、注入的 `_http` 代理失效 |
| S2-A3 | `DingTalkStreamTransport.cs:277-299` | 先 `OnNext` 再 ack | 下游慢 → ack 超时 → 平台重投 → 重复处理 |
| S2-A4 | `DingTalkStreamTransport.cs:229-256` | 只发 ping、不检测 pong/空闲 | 半开连接永久挂起且 `IsConnected=true` |
| S2-A5 | `DingTalkChatUpstream.cs:227-231` 存储、`DingTalkStreamTransport.cs:103` 使用 | `sessionWebhook` 原样 POST，无 scheme/host 校验 | 平台消息携带的 URL 可指向任意主机，构成 SSRF/凭据外发面 |
| S2-A6 | `DingTalkStreamTransport.cs` 重连路径 | 重连成功不通知上游 | 健康快照永久显示断线 |
| S2-A7 | `SatoriChatUpstream.cs` sn 提交 | sn 先提交后映射，只增不减 | 映射失败即永久丢失该事件 |
| S2-A8 | `SatoriCapabilityMapper.cs:82-91`；`ChatCommandDispatcher.cs:129-136` | 能力只声明 `Guild` | Satori 私聊入站可收、出站被判 `unsupported` |
| S2-A9 | `SatoriChatUpstream.cs` Dispose | Dispose 不断开也不释放 transport | 资源泄漏、重连残留 |
| S2-A10 | `DiscordUpstream.cs`（`BuildAllowedMentions`） | 未指定 `AllowedTypes` | 显式 user mention 实际不生效（与设计意图相反） |
| S2-A11 | Discord 交互/频道缓存 | 仅在 Ready 时清理 | 无界增长 |
| S2-A12 | `ChatEventDeduplicator` 窗口 | 10 分钟 < 钉钉 60 分钟重放窗口 | 重放可穿透去重 |
| S2-A13 | `ChatEventId` 编辑事件 | 编辑事件 id 恒定 | 同一消息第二次编辑被去重误杀 |
| S2-A14 | `ChatEventDeduplicator.cs:112-134` | prune/trim 在锁内全表扫描 | 1 万条目下每条消息 O(n) |
| S2-A15 | `ChatCommandDispatcher.cs:142-169` | 分片部分失败不回写已送达分片 | 重试依赖适配器缓存，可能重复 |
| S2-A16 | Discord 发送路径 | 429/限流不重试，`retryAfterMs` 仅透传 | 高峰期丢消息 |
| S2-A17 | `AiChatResponder.cs:233-242` | 认证/余额/非法请求类错误返回 `null` | 用户完全静默，无兜底、无计数 |
| S2-A18 | `AiChatResponder.cs:212-222` | clip 重试无退避 | 双倍计费与延迟，忽略取消 |
| S2-A19 | `AiChatResponder.IsClipped` | 只识别三个英文标记 | `finish_reason=length` 的截断被当成正常回复 |
| S2-A20 | `AiConversationGate.cs:10-46` | 会话字典无界；`Dispose` 与在途 lease 竞争 | 长跑内存增长；释放时 `SemaphoreSlim` 已释放异常 |
| S2-A21 | `ChatCompositionModule.cs:173` | 裸单例 `HttpClient` | DNS 变更不刷新、代理配置不可调 |
| S2-A22 | `PromptSafety`/`PromptComposer` 输入侧 | 输入长度上限分散且不一致 | 不可信内容隔离不足，成本不可预估 |

### 4.2 任务运行时（10 项）

| ID | 位置 | 问题 | 影响 |
| --- | --- | --- | --- |
| S2-B1 | `InMemoryTaskStore.cs:28-35`；`SqliteTaskStore.cs:46-49` | in-memory 版 `ListActiveAsync` 不含 `WaitingForInput` | 无 `--state-dir` 时 `/tasks status:waiting` 恒为空 |
| S2-B2 | `NatterHost.cs:47-101` | `_started=true` 先置位，profile 校验在后且失败无回滚 | 半启动不可重试；已连 upstream 未断开 |
| S2-B3 | `SqliteTaskStore.cs` 事件写入 | `MAX(seq)+1` 取号 | 并发写主键冲突 → `TransitionAsync` 失败 |
| S2-B4 | `SqliteTaskStore.cs:221-223` | 反解析 `Source.Key` 复原 `Source` | 违反结构化 key 契约；selfId 含 `:` 往返失真，结果投递被拒 |
| S2-B5 | `ChatCompositionModule.cs:101-170`；`SqliteContextStores.cs`；`SqliteConversationStore.cs` | 4 个 store 各自迁移同一 `natter.db`，`sessions` 被两套 DDL 拥有，`foreign_keys` 仅一方设置 | schema 漂移与级联删除不一致 |
| S2-B6 | `ChatCompositionModule.cs:218-227` | `ChatCommandRouter` 未注入 `ToolLoopOptions` | `/status`、`/tasks` 报的预算与 worker 实际 cap 脱节 |
| S2-B7 | `TaskRecoveryLifecycle.cs:13-21` | 恢复期先入队，transport 尚未就绪 | 结果投递首轮必失败，依赖重试兜底 |
| S2-B8 | `TaskDispatcher.cs:21-24` | `_tracked`/`_resultsSent`/`_pendingResults` 无界 | 长跑宿主内存单调增长 |
| S2-B9 | `TaskDispatcher.DisposeAsync` | 停机不等待在跑 worker | 退出时结果丢失 |
| S2-B10 | `HostContextPipeline.cs:111-160` | 四处 `catch (Exception)` 吞掉 `OperationCanceledException` | 取消被当作"降级"，继续发起模型调用 |

### 4.3 插件体系与插件存储（15 项）

| ID | 位置 | 问题 | 影响 |
| --- | --- | --- | --- |
| S2-C1 | `PluginPackageValidator.cs:162-231` | 解压上限只信中央目录声明长度 | 伪造声明的 zip bomb 可写满私有目录 |
| S2-C2 | `NpsPluginOptions.cs:115-129` | `context.assembler`/`prompt.*` 贡献无权限要求 | 零权限插件即可读取会话/记忆/摘要（治理层绕过） |
| S2-C3 | `NpsPluginCandidate.cs:213-227` | 卸载只 `Unload()`，候选仍持有 ALC 类型 | collectible ALC 永不回收；Windows 上更新可因文件占用失败 |
| S2-C4 | `NpsPluginGraph.cs:257-293` | 任一环 → 全图 `order=[]` | 非环插件也不启动且无错误，故障不可诊断 |
| S2-C5 | `PluginStore.cs:171-218` | 安装顺序：移动目录 → 写 state → 写 pending，失败不补偿 | `installed.json` 指向不存在的目录，静默回退 |
| S2-C6 | `PluginPackageSignatureVerifier` | 哈希比较非常量时间（待确认可利用路径） | 理论上可做时序侧信道 |
| S2-C7 | 签名规范化文本 | `hash + "  " + name + "\n"` 无转义，entry 名允许换行 | 签名覆盖范围有歧义 |
| S2-C8 | `PluginPackageValidator` | `package.sig` 无尺寸上限、全量 `ReadAllBytes` | 恶意包可造成内存峰值 |
| S2-C9 | 安装后路径 | 哈希/签名只校验一次 | 落地后篡改 DLL 仍加载，信任显示仍为 signed |
| S2-C10 | `PluginStore.CleanupAsync` | 绕过 `_gate`，无跨进程锁 | 与安装并发时删掉 staging |
| S2-C11 | legacy `entryAssembly` | 未校验必须位于插件目录内 | 可加载目录外 DLL |
| S2-C12 | 激活回滚 | 不回收 port adapter 注册与单例缓存 | 回滚后仍可路由到失败插件 |
| S2-C13 | `PluginPackageInspector` | 原生库禁令只按扩展名/PE 头 | `.dylib`/改扩展名可绕过 |
| S2-C14 | `AndroidPluginHost.cs` | 信任根（trusted-keys）与应用包投放同一可写目录 | 提权后信任锚可替换 |
| S2-C15 | `PluginStore.ReadState` | `installed.json` 损坏即抛，无修复路径 | 全量插件不可用 |

### 4.4 管理面与配置（5 项）

| ID | 位置 | 问题 | 影响 |
| --- | --- | --- | --- |
| S2-D1 | `WebConsoleAuth.cs:194-278` | 全局失败上限无提前解除；`Reset` 只清单客户端 | 5 分钟内可被刷满全局阈值 |
| S2-D2 | `WebConsoleRequestHandler.cs:1020-1032` | 信任 `X-Forwarded-For` 最左值（默认信任回环代理） | 代理配置不当可伪造限速键与审计 IP |
| S2-D3 | `WebConsoleServer.cs:255-267`、`:305-329` | body 读取阶段无超时；每连接 `Task.Run` 无并发上限 | 本地慢速请求可占满监听队列 |
| S2-D4 | `WebConsoleRequestHandler.cs:122-126`；`WebConsoleAudit.cs:158-166` | 认证前即写审计，且轮转只保留一份 `.1` | 任意网页可刷掉取证历史 |
| S2-D5 | `WebConsoleConfig.cs:58-67` | 脱敏规则缺 PEM 私钥块；宿主 `IAdminRedactor` 未接入 | 配置中的 PEM 私钥以明文回显 |

### 4.5 CLI / 运维 / 部署（13 项）

| ID | 位置 | 问题 | 影响 |
| --- | --- | --- | --- |
| S2-E1 | `RuntimeHealth.cs:52-64` | 只脱敏 `LastDisconnectError` | `LastError`/`LastSendError` 原文进入 `/health`、`health --json`、soak 证据 |
| S2-E2 | `NatterCli.cs:96-124` | `health --offline` 跳过 `StartAsync` | 插件从未激活，upstream/provider 状态只能是未连接/unknown |
| S2-E3 | `RuntimeCommands` | `serve`/`health` 在 degraded/失败插件时仍返回 0 | 自动化无法据退出码判断健康 |
| S2-E4 | `NatterCliOptions.cs:40-73` | 全局选项缺值静默忽略，且吞掉下一个选项 | `--config-dir --state-dir /x` 静默丢参数 |
| S2-E5 | `CliParser.cs:53-59` | 未知选项被接受并吞掉后续位置参数 | 拼写错误静默生效 |
| S2-E6 | `NatterCliOptions.cs:100-108` | `--no-plugins` 连带禁用 config/state/profile 并绕过锁 | 容器只读根上直接失败 |
| S2-E7 | `StateDirectoryLock.cs:13-33` | 只捕 `IOException` | 目录不可写时带栈崩溃 |
| S2-E8 | 多处 `JsonSerializerOptions` | `--json` 三套序列化配置并存 | 输出字段命名/空值行为不稳定 |
| S2-E9 | `HealthHttpServer.cs:41-57` | 每请求 `_ = HandleAsync`，无并发上限 | 本机可耗尽线程 |
| S2-E10 | `scripts/p2-collect.sh:38-39` | `curl -H "Authorization: Bot $TOKEN"` | token 出现在 `ps`/`/proc/*/cmdline` |
| S2-E11 | `Directory.Build.props:18-19` | `NuGetAudit=false` + `NoWarn NU1900` | 全仓供应链审计默认关闭，CI 只覆盖一个工程 |
| S2-E12 | `ci.yml:33-38` | NuGet 缓存 key 漏 `Directory.Build.props` | 改构建属性不触发缓存失效 |
| S2-E13 | `deploy-verify.yml` | 上传的 `soak-report.json` 在 `image` job 中不存在 | 证据缺件但不报错 |

### 4.6 测试与文档（9 项）

| ID | 位置 | 问题 | 影响 |
| --- | --- | --- | --- |
| S2-F1 | `Phase23Acceptance/AcceptanceHarness.cs:24,214-215` | 验收自造 200 行 host，并逐字重抄产品 SHA256/ETag 算法 | 产品契约漂移时验收仍绿（假验收） |
| S2-F2 | `ContextPipelineTests.cs:178` | 预算测试只断言 `EstimatedCharacters <= 80` | 空 bundle 也通过 |
| S2-F3 | `DeepSeekResponderTests.cs:121,133` | 串行化只断言 `MaxActive == 1` + `Delay(50)` | 丢请求或死锁同样通过 |
| S2-F4 | `WebConsoleServerTests.cs:118-125` 等 | `FreePort()` 绑 0 → 释放 → 再绑（TOCTOU）；无 `xunit.runner.json` | 默认并行下的 flake 主源 |
| S2-F5 | 全仓测试 | 936 用例仅 32 处 `WaitAsync`，0 个 `[Fact(Timeout)]`，无 `--blame-hang` | 挂死会拖满 CI 超时且无 dump |
| S2-F6 | 覆盖盲区 | ALC 卸载、Android 宿主、插件签名失败路径无测试 | 高风险路径零回归保护 |
| S2-F7 | `P5FollowupTests`/`P5TaskUxTests`/`P5TaskUxAcceptanceTests` | 三个文件覆盖面高度重叠 | 维护成本高、真实缺口被"用例数"掩盖 |
| S2-F8 | `docs/LIVE_TEST_PLAN.md:3,17`；`docs/P4_TIER1_PRODUCTIZATION.md:457` | 同一文档内"待人工/已复测通过"并存；24h soak 状态与运维文档矛盾 | 验收记录不可复核 |
| S2-F9 | `README.md:27`；`docs/operations/2026-11-21-p4-5-nonsoak-status.md:12-13` | 多份验收文档用过期测试总数（264 / 410 / 236）作为 Go 判定依据 | 验收记录不可复核、发布决策依据失真 |

---

## 5. S3 低优先级（合并清单）

> **复核状态（2026-11-22，基线 commit `eadcd77`）**：45 项已逐条复核并分级 → [S3 45 项复核与分级（Triage）](REVIEW-2026-11-22-S3-TRIAGE.md)。汇总：仍成立 27 / 已修复 1 / 记录过期 7 / 不值得修 10。派工与取舍以 triage 文档为准，下面的原始条目保留不动。

完整条目见附录；此处按主题给出代表项与数量：

| 主题 | 数量 | 代表项 |
| --- | --- | --- |
| 组合根与 DI | 6 | `EnsureStateDirectory` 在 `_built` 检查前执行；`PortAdapterRegistry.TryGet` 后缀匹配；`ApplicationDispatcher` 每次反射 `MakeGenericMethod`；`Infrastructure → Cli.Abstractions` 反向依赖；`Natter.Domain` 仅为字符串引用；插件 `Configure` 部分失败不回滚 |
| 事件与指标 | 5 | `TaskMetrics` 计数可为负并被 `Math.Max(0,…)` 掩盖；订阅计数异常路径泄漏；`AdminLogBufferLoggerProvider.IsEnabled` 恒真；`JsonlAdminAuditSink` 把取消当"跳过审计"；`RxDomainEventBus` 的 dispose/publish 竞态 |
| 聊天模型 | 7 | 分片可超出 `maxLength`（单个超长 text element）；markdown 围栏跨片断裂；`sequence` 非单调；Satori 有损映射；附件守卫覆盖面；时间戳宽容解析按本机时区解释；`ConversationKinds` 组合语义 |
| CLI/脚本 | 12 | `--verbose` 形同虚设；取消与"时长到期"共用退出码 130；`memory export` 先写后 chmod；`rotate-secret.sh` 无 trap；`soak-sample.sh` 无 jq 回退写出坏样本；`ops-health-alert.sh` 无锁；`p2-serve.sh` 日期写死；`stage-tier1-plugins.sh` 硬编码 TFM；`webconsole-e2e.sh` 端口预算；脚本权限位不齐；`shellcheck` 白名单只覆盖新脚本；`deploy-verify` 未固定 action SHA |
| 部署 | 6 | Dockerfile 基础镜像浮动 tag；`EXPOSE` 与"绝不发布端口"注释矛盾；`natter.service` 无资源上限；`SuccessExitStatus=0` 空操作；runbook 与脚本对 `/opt/natter` 属主相反；`SystemCallFilter` 是否过严待确认 |
| 文档 | 9 | 2 个失效相对链接；README 命令表未转义 `\|` 破坏渲染；README 工程表缺 2 个测试工程与 2 个插件；`docs/testing/README.md` 索引缺 3 份；`DEVELOPMENT.md` 的 `ReferenceOutputAssembly` 机制不存在；`scripts/test.sh` 只跑一个套件；`LIVE_TEST_PLAN` 漏 12 个用例；`HARNESS_RESPONSIBILITY` 的 mandatory 迁移未落地；OBS-4 已修复但记录未更新 |

---

## 6. 横向主题分析

### 6.1 并发模型：三处 fire-and-forget 构成系统性风险

| 位置 | 形态 | 后果 |
| --- | --- | --- |
| `ChatOrchestrator.cs:156` | 每条事件一个 Task | 无界并发、乱序、停机不排空 |
| `NpsPluginRuntime` 事件回调 | 插件订阅回调 fire-and-forget | 回滚后仍可能回调、乱序 |
| `WebConsoleServer.cs:265`、`HealthHttpServer.cs:55` | 每连接一个 Task | 无并发上限，本地可耗尽线程 |

仓库对"进程内热流、无 outbox、无重试"有清晰文档，但**没有把"背压与生命周期"作为框架能力提供**。建议抽一个 `BoundedEventPump<T>`（有界 Channel + 并发度 + 停机排空 + 可观测队列深度），供编排器、插件回调与 HTTP 入口复用。

### 6.2 错误处理：降级策略淹没了真实失败

`catch (Exception)` 后返回兜底值在上下文装配、responder 组合、任务执行中大量出现。单独看每一处都有注释说明"不让上下文失败阻断回复"，但叠加后产生三类问题：

1. **取消被吞**（`HostContextPipeline`）：用户已取消，系统仍继续调用模型；
2. **真实故障静默**（`CompositeChatResponder`、AI 认证错误）：没有日志、没有健康指标变化；
3. **语义反转**（S1-07）：跳过被当成失败发送给用户。

建议统一为 `catch (Exception ex) when (ex is not OperationCanceledException)`，并为每次降级打点（计数器 + 结构化日志），让"降级"成为可观测事件而不是隐形路径。

### 6.3 安全边界：三层模型，第三层最弱

| 层 | 机制 | 评价 |
| --- | --- | --- |
| 网络层 | 回环绑定、`/health` 仅本机、`TrustedProxies` | 良 |
| 应用层 | token + CSRF + 角色 + 只读开关 + 命令 allowlist | 良（S1-04 除外） |
| 数据层 | 脱敏、`${secret:}` 引用、审计 | 中（PEM 缺失、diff 未脱敏、`/health` 字段未脱敏） |
| 插件层 | manifest 权限、签名、ALC | **弱**：权限是治理不是沙箱；签名只在安装时生效；卸载边界缺失 |

报告建议把"插件信任"从"多级策略"简化为"可信/不可信两档 + 默认拒绝未签名"，并在文档中明确"进程内插件 = 完全信任代码"这一事实，避免运维误以为权限声明提供了隔离。

### 6.4 数据层：一个数据库，四套写者

`IConversationStore`、`IMemoryStore`、`ISummaryStore`、`ITaskStore` 各自 `new SqliteXxx(path)`，各自 `CreateDirectory` + `Migrate` + `SecureFiles`。同一文件被两套 DDL 声明 `sessions`，`user_version` 只由其中一方维护，`PRAGMA foreign_keys` 只在会话 store 的连接上开启。当前`CREATE TABLE IF NOT EXISTS` 掩盖了冲突，但任何一次列变更都会变成"谁先打开谁赢"的隐式竞态。建议抽出 `SqliteHostDatabase`（单一迁移入口 + 统一连接工厂 + 统一 pragma），四个 store 复用它。

### 6.5 验证链：绿灯的可信度低于代码质量

这是本仓库最值得优先投入的方向：

- CI 不构建解决方案、漏 1/4 测试套件（S1-09）；
- soak 报告有两条假 PASS 路径（S1-10）；
- 验收 harness 重抄产品算法、自造 host（S2-F1）；
- 文档用过期测试总数作为 Go 依据（S2-F9）；
- 40 个 live 用例在无凭据时全 skip，CI 只验证"确实 skip"（S2-F5 相关）。

**建议**：把"验证链"当作产品代码对待——给 soak 报告、验收 harness、CI 配置写单元测试，并把这些测试本身纳入 CI。

---

## 7. 与文档不一致清单

| # | 文档声称 | 实现事实 | 位置 |
| --- | --- | --- | --- |
| 1 | 264 个框架级测试 | 实测 557；全仓 936 用例 | `README.md:27` |
| 2 | 钉钉/星火"真实平台凭据未接入" | 联调记录称端到端通过、Spark 7/7、DingTalk 2/2 | `README.md:275` vs `docs/testing/2026-11-15-live-dingtalk-spark.md:1-16` |
| 3 | `Specification` 组合表达式"可交给 ORM 适配器翻译" | `And/Or/Not` 使用 `Expression.Invoke`，EF Core 无法翻译 | `docs/ARCHITECTURE.md:65` vs `Specification.cs:21-56` |
| 4 | mandatory 安全片段"不可关闭、永远最后" | `HARNESS_RESPONSIBILITY.md` 已决策改为 default-removable，代码仍是 mandatory | `docs/HARNESS_RESPONSIBILITY.md:118-160` vs `PromptSafety.cs:12-35` |
| 5 | OBS-4（allowlist 内私网仍可访问）为已知缺口 | 已有测试证明即使 allowlist 也拒绝私网/元数据 | `docs/testing/2026-11-21-p5-followup-regression.md:22,180` vs `P5FollowupTests.cs:542-554` |
| 6 | L5 人工闸门"待人工触发" | 同文档后文写"已复测通过" | `docs/LIVE_TEST_PLAN.md:3` vs `:17` |
| 7 | `docs/testing/README.md` 是测试记录索引 | 缺 3 份记录（`2026-11-21-p5-followup-regression`、`2026-11-21-webconsole-phase23-acceptance`、`2026-11-22-p5-task-ux-acceptance`） | `docs/testing/README.md` §4 vs `docs/testing/` 目录 |
| 8 | 测试工程用 `ReferenceOutputAssembly="false"` + Copy 目标 | 实际是普通 `ProjectReference` + `CopyTestPlugins` 目标，未设置 `ReferenceOutputAssembly` | `docs/DEVELOPMENT.md:705` vs `tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj:26-56` |
| 9 | README 工程表覆盖全部工程 | 缺 `plugins/*`、`tests/Natter.Plugin.WebConsole.Tests`、`tests/Natter.Plugin.PromptLibrary.Tests` | `README.md:96-101` vs `Natter.slnx` |
| 10 | `forget <id\|text>` 表格正常渲染 | 未转义 `\|` 使该行多出一列 | `README.md:173` |
| 11 | 链接 `docs/PLUGIN_MODEL.md`、`testing/2026-11-15-p2-acceptance-response.md` | 两个目标文件不存在 | `docs/HARNESS_RESPONSIBILITY.md:5`、`docs/P2_RUNTIME_PROFILE.md` |
| 12 | 诊断命令（`plugins list`、`upstreams`、`ai status`）被当作只读巡检手段 | 这些命令都会 `StartAsync` 并抢占网关会话；仅 `health --offline` 例外 | `README.md:166-168`、`docs/OPERATIONS_RUNBOOK.md:119-121` vs `NatterCli.cs:96-124`（同文档 `:134` 已警告） |

---

## 8. 修复路线图

### 8.1 发布前（建议 1–2 天，S1）

| 顺序 | 项 | 工作量 | 备注 |
| --- | --- | --- | --- |
| 1 | S1-08 Android `INTERNET` 权限 | 5 分钟 | 阻断 Android 发布 |
| 2 | S1-09 CI 补 PromptLibrary + 解决方案构建 | 30 分钟 | 恢复"绿灯=编译通过"的最低保证 |
| 3 | S1-01 卸载 id 校验 + 根前缀校验 | 1 小时 | 防数据毁灭 |
| 4 | S1-04 validate diff 脱敏 | 1 小时 | 防只读凭据读明文 secret |
| 5 | S1-10 soak 判定三条修正 | 2 小时 | 恢复发布闸门可信度 |
| 6 | S1-02 代理路径拒绝/禁代理 | 1 小时 | 与安全文档承诺对齐 |

### 8.2 下一迭代（S1 剩余 + 高价值 S2）

- S1-03 出站转义、S1-05 会话 lease 覆盖、S1-06 有界事件泵、S1-07 结果发送条件、S1-11 命令启动策略、S1-12 信任传播。
- 同期处理：S2-A1 幂等预留、S2-B3 事件序号、S2-C1 zip 上限、S2-C5 安装原子性、S2-D5 PEM 脱敏、S2-E1 health 字段。

### 8.3 后续（结构性）

1. 抽出 `SqliteHostDatabase` 统一迁移与连接工厂（§6.4）。
2. 抽出 `BoundedEventPump` 并替换三处 fire-and-forget（§6.1）。
3. 插件体系收敛为"可信/不可信"两档 + 启动复验；文档明确"进程内插件无隔离"。
4. 验证链自测：soak 报告、验收 harness、CI 配置纳入测试。
5. 文档自动化：测试数、链接、能力声称由脚本生成/校验，杜绝手工漂移。

---

## 9. 已排除的疑似问题（避免后续重复投入）

以下项目经代码核对或实测确认**不存在**所述问题，记录在此以免误报：

1. **SQL 注入**：全部使用 `AddWithValue` 参数化；`user_version` 插值为整数。
2. **zip 路径穿越**：`PluginPackageValidator` 对 `..`、绝对路径、符号链接条目、重复条目均拒绝；目标是 `Path.GetFullPath` + 前缀校验。
3. **Web 授权绕过**：`Classify` 把未知动词归 `Manage`；写端点二次校验只读/开关；config id 双层白名单；命令终端不落 shell 且宿主复核 allowlist + argv。未发现命令注入或 allowlist 绕过。
4. **CSRF**：状态变更端点强制 `X-WebConsole-CSRF` 常量时间校验；Cookie `SameSite=Strict`，跨站 fetch 还会被 Origin 策略拦截。
5. **PromptLibrary 越权**：片段 id 有 owner 前缀；`host.safety.untrusted-data` 被宿主以 `prompt.fragment_reserved` 拒绝，安全片段不会被插件遮蔽（探针实测）。
6. **异步反模式**：全仓无 `async void`、无 `Thread.Sleep`、无 `.Result`（除两处同步适配器与 `PluginStore` 的信号量 `Wait`，均有其同步 API 语境）。
7. **Discord `@everyone` 泄漏**：`AllowedMentions` 实际生成 `parse: []`（实测 Discord.Net 3.20.2 行为），问题是显式 user mention 反而失效，而非全员提及泄漏。
8. **SSRF 直连路径**：`HttpFetchTool` 的 IPv4/IPv6 私网、回环、链路本地、元数据、CGNAT 判定完整；直连时 `ConnectCallback` 二次校验有效（问题仅在代理路径，见 S1-02）。

---

## 10. 局限与后续建议

- 本次未在真实 Discord/DeepSeek/钉钉/星火 上验证，40 个 live 用例全部 skip；S1-03、S2-A10 等平台行为结论基于协议映射与本地 fake，建议在修复后用真实平台各回归一次。
- Android 仅做清单与静态分析，未上真机；建议补一条"Release 包冷启动 + 网络可用"的 CI/手工闸门。
- 24h soak 未实跑；在修正 soak 判定之前，历史 soak 结论不应作为发布依据。
- 建议把本报告转为 issue 清单时保留 `S1-xx`/`S2-xx` 编号，便于与附录中的原始编号（C-xx、P0-x、WC-xx、R-xx、S-x、D-x、DT-x、A-x、发现 x）交叉追溯。

---

## 附录 A：分领域详细报告

| 报告 | 范围 | 行数 | 发现数（S0/S1/S2/S3） |
| --- | --- | --- | --- |
| [01-core.md](../review/2026-11-22/01-core.md) | Abstractions / Domain / Application / Infrastructure | 749 | 34（1/4/21/8） |
| [02-plugins.md](../review/2026-11-22/02-plugins.md) | Plugin.Abstractions / Plugin.Store / NPS / Android 宿主 | 363 | 29（1/6/12/10） |
| [03-chat-ai.md](../review/2026-11-22/03-chat-ai.md) | NLMP / Satori / Discord / DingTalk / AI provider | 707 | 57（0/8/27/22） |
| [04-console-prompt.md](../review/2026-11-22/04-console-prompt.md) | WebConsole / PromptLibrary 及其测试 | 490 | 16（0/1/5/10） |
| [05-cli-ops.md](../review/2026-11-22/05-cli-ops.md) | CLI / Android / scripts / deploy / workflows | 960 | 68（0/7/31/30） |
| [06-tests-docs.md](../review/2026-11-22/06-tests-docs.md) | 测试质量、覆盖盲区、文档一致性 | 495 | 25（0/3/13/9） |

> 分报告为原始证据（含代码片段、复现命令、"已排除的疑似问题"），本报告为其去重与分级后的汇总；两者编号可交叉追溯。

## 附录 B：复核命令

```bash
# 基线
git rev-parse --short HEAD && git status --short

# 构建与测试（本报告实测）
dotnet build Natter.slnx -c Release
dotnet test Natter.slnx -c Release --no-build

# 单套件
dotnet test tests/Natter.Framework.Tests -c Release --no-build
dotnet test tests/Natter.Plugin.WebConsole.Tests -c Release --no-build
dotnet test tests/Natter.Plugin.PromptLibrary.Tests -c Release --no-build
dotnet test tests/Natter.Live.Tests -c Release --no-build   # 无 NATTER_LIVE 时应全部 skip

# 关键复核点
sed -n '415,455p' src/Natter.Plugin.Store/PluginStore.cs                # S1-01
sed -n '27,78p'  src/Natter.Infrastructure/Chat/ChatCompositionModule.cs # S1-02
sed -n '106,137p' src/Natter.Chat.Satori/SatoriCommandMapper.cs          # S1-03
sed -n '795,835p' plugins/Natter.Plugin.WebConsole/WebConsoleRequestHandler.cs  # S1-04
sed -n '163,211p' src/Natter.Feature.Ai/AiChatResponder.cs               # S1-05
sed -n '155,168p' src/Natter.Application/Chat/ChatOrchestrator.cs        # S1-06
sed -n '99,222p' src/Natter.Application/Chat/Tasks/TaskDispatcher.cs      # S1-07
grep -c uses-permission src/Natter.Hosting.Android/obj/Release/net10.0-android/android/AndroidManifest.xml  # S1-08 -> 0
sed -n '44,99p' .github/workflows/ci.yml                                 # S1-09
sed -n '105,150p' scripts/soak-report.py                                  # S1-10
sed -n '96,124p' src/Natter.Cli.Core/Cli/NatterCli.cs                     # S1-11
grep -n 'Trust' src/Natter.Plugin.Store/PluginStore.cs                    # S1-12
```

## 附录 C：统计口径

- 用例数取 `dotnet test` 的 `Total` 值（Theory 展开后）；属性计数取源码 `grep`，两者不等属正常。
- 代码行数统计排除 `bin/`、`obj/`；包含注释与空行。
- 严重度计数按"合并后的条目"统计：分报告中同一根因的条目（如 orchestrator fire-and-forget 同时出现在 core 与 plugins）合并为一条。
- 分报告合计 229 条（34 + 29 + 57 + 16 + 68 + 25，含重复与"待确认"）；合并去重后为本报告的 12 + 74 + 45 = 131 条确定性条目。
- "待确认"条目（如哈希常量时间可利用性、`SystemCallFilter` 对 .NET 过严）未计入确定性缺陷，仅在附录保留。
