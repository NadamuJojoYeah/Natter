# S3 45 项复核与分级（Triage）

> **复核基线**：`git archive HEAD` → **commit `eadcd77`**（`eadcd77e4b4c804a301353c2aa4fc8f90dcd54be`，2026-11-22 18:43）。
> **复核对象**：`docs/CODE_REVIEW_2026-11-22.md` §5「S3 低优先级（合并清单）」的 45 项（6+5+7+12+6+9）。
> **方法**：只读。所有行号与代码片段均来自 `git archive HEAD` 导出的干净副本（`artifacts/review-s3/head/`），**未采信工作区任何未提交改动**。
> **本次不修任何代码**；未部署、未重启、未 commit/push。
> **原报告写作时间**：`1e6eb77`，2026-11-22 14:43。早于该时间就已修好的项一律按「记录过期」判，不冒充“后续修复”。

---

## 0. 结论

| 判定 | 数量 | 说明 |
| --- | --- | --- |
| **仍成立** | **27** | 代码确有该问题，多数是边角 |
| **已修复** | **1** | 报告写作后由后续提交解决 |
| **记录过期** | **7** | 代码是对的，报告/文档需要更新（含 lead 已知的 2 条） |
| **不值得修** | **10** | 技术成立，但收益极低或与既有设计冲突 |
| 合计 | 45 | |

一句话：**45 项里没有一个是“必须马上修”的核心链路缺陷**。真正值得投入的是「文档对齐」（7 项，零运行时风险）和「回复分片/时间戳」（4 项，唯一影响用户体验的代码项）。CLI/脚本/部署的 17 项适合打包成一次运维向整修，与业务代码零冲突。

---

## 1. 逐项分级

编号 `S3-01…S3-45` 按原 §5 表格顺序（组合根 6 / 事件指标 5 / 聊天模型 7 / CLI 脚本 12 / 部署 6 / 文档 9）。

### 1.1 组合根与 DI（6）

| ID | 项 | 判定 | 证据 / 理由 |
| --- | --- | --- | --- |
| S3-01 | `EnsureStateDirectory` 在 `_built` 检查前执行 | 不值得修 | `src/Natter.Infrastructure/Composition/NatterApplicationBuilder.cs:178-182`：先 `EnsureStateDirectory()` 再 `if (_built) throw`。唯一后果是「重复 Build 抛异常前已建目录并 chmod 0700」——无数据损坏、无安全影响，属于防御性洁癖。 |
| S3-02 | `PortAdapterRegistry.TryGet` 后缀匹配 | 仍成立（边角） | `src/Natter.Infrastructure/Composition/PortAdapterRegistry.cs:40-42`：`adapter.Port == port \|\| adapter.Port.EndsWith("." + port)`。短名 `foo` 会命中后注册的 `x.foo`，**取到错误 adapter 且无告警**。修复：精确匹配优先、后缀匹配只作 fallback 并记一次 warning。 |
| S3-03 | `ApplicationDispatcher` 每次反射 `MakeGenericMethod` | 不值得修 | `src/Natter.Application/Dispatching/ApplicationDispatcher.cs:28,36`：每个请求一次 `MakeGenericType` + `GetService` + `MakeGenericMethod` + `Invoke`（约 1–3 µs）。调用上下文是「一条聊天消息 → 一次 dispatch」，与模型调用（100 ms 级）/SQLite 相比完全不可测。缓存收益要等 dispatch 变成热路径才成立。 |
| S3-04 | `Infrastructure → Cli.Abstractions` 反向依赖 | 仍成立（结构） | `src/Natter.Infrastructure/Natter.Infrastructure.csproj:24` 引用 `Natter.Cli.Abstractions`；`Plugins/Nps/{IPluginCliCommandSink,NpsCliCommandContributor,NpsContributionRegistry,NpsPluginRuntime}.cs` 均 `using Natter.Cli.Abstractions`。分层方向确实反了。修复需把「插件 CLI 贡献」契约上移到 `Natter.Plugin.Abstractions`（或独立 `Natter.Hosting.Contracts`），属设计决策，不建议与其他项混批。 |
| S3-05 | `Natter.Domain` 仅为字符串引用 | 不值得修 | `Natter.Domain` 实际只被 `src/Natter.Application/Demo/IncrementCounter.cs:4`（`using Natter.Domain.Demo`）使用，其余引用是工程级 ProjectReference。它是 DDD 骨架的示例聚合根，删工程要连带改 slnx / 文档 / Android 宿主 / 测试；收益是“少一个空工程”。 |
| S3-06 | 插件 `Configure` 部分失败不回滚 | 不值得修 | `src/Natter.Infrastructure/Plugins/Nps/NpsPluginRuntime.cs:349` 的 `ConfigureAsync` 在 try/catch 内，失败走 `SetFailure`（`:1094-1109`）：只设错误 + `DisposeLoadContext`，不回收 `candidate.Contributions` 里已注册的条目。但 `candidate.Contributions` 是**每插件独立** registry，贡献消费按 `State == Activated` 过滤，失败插件不可见；registry 随 candidate 一起被回收。真实残留面为零。 |

### 1.2 事件与指标（5）

| ID | 项 | 判定 | 证据 / 理由 |
| --- | --- | --- | --- |
| S3-07 | `TaskMetrics` 计数可为负，被 `Math.Max(0,…)` 掩盖 | 记录过期 | 报告写作前已在 `9b72971`（2026-11-22 10:00「修正任务重复入队误判与指标泄漏」）修好。现状配对严密：`TaskDispatcher.cs:89-90` 在 `_tracked[taskId]=0` 的同一行区间 `metrics.Created()`；扣减侧只有 `MarkTerminal`（`:343-364`，先 `TryRemove` 成功才扣）与 `ReleaseTracked`（`:366-369`，同样先 `TryRemove`）。`TaskMetrics.cs:49` 的 `Math.Max(0, …)` 现在只是防御，不会再掩盖负值。 |
| S3-08 | 订阅计数异常路径泄漏 | 不值得修 | `src/Natter.Infrastructure/Events/RxDomainEventBus.cs:38-48`：`Interlocked.Increment` 后仅 `_synchronizedSubject.Subscribe(...)` 可能抛，而该路径只在「已 dispose 竞态」下可达（前置 `ObjectDisposedException.ThrowIf` 已挡常规路径）。`SubscriberCount` 只用于诊断，泄漏一个计数不影响行为。同型代码在 `RxChatEventHub.cs:39-45`。 |
| S3-09 | `AdminLogBufferLoggerProvider.IsEnabled` 恒真 | 不值得修 | `src/Natter.Infrastructure/Admin/AdminLogBufferLoggerProvider.cs:45-46`：`logLevel != LogLevel.None`。这是**有意的**：ring buffer 要能回放完整链路（含 Debug），否则「/api/logs 看不到出问题的前一秒」。容量有上限（默认 500/最大 4096）、逐条脱敏截断，成本可控。若要优化，应改为可配置 min level 而不是改恒真。 |
| S3-10 | `JsonlAdminAuditSink` 把取消当“跳过审计” | 仍成立（安全面·小修） | `src/Natter.Infrastructure/Admin/JsonlAdminAuditSink.cs:61-64`：`catch (… OperationCanceledException)` → `if (ex is OperationCanceledException) return;`。取消时审计行**静默丢弃且无任何痕迹**，事后无法区分“没发生”和“发生了但被取消”。修复很小：取消分支至少 `LogWarning` 一条“audit dropped (cancelled)”，或用独立 token 写入。 |
| S3-11 | `RxDomainEventBus` 的 dispose/publish 竞态 | 不值得修 | `RxDomainEventBus.cs:51-69` 是 check-then-act：`_disposed` 检查后 `Dispose` 可能已执行。但 System.Reactive 的 `Subject<T>.OnNext` 在 dispose 后是 no-op（`_observer` 置空），不会抛、不会半派发；`_disposed` 非 volatile 只影响“是否抛 ODE”，不影响正确性。 |

### 1.3 聊天模型（7）

| ID | 项 | 判定 | 证据 / 理由 |
| --- | --- | --- | --- |
| S3-12 | 分片可超出 `maxLength`（单个超长 text element） | 仍成立 | `src/Natter.Chat/Reliability/ChatTextChunker.cs:55-63`：`if (current.Length > 0 && current.Length + element.Length > maxLength) Flush…; current.Append(element);`。当**单个 grapheme cluster 本身长于 limit**（ZWJ emoji 家族、组合字符；或 `maxLength` 被配成小值）时 `current.Length == 0` 不 flush，直接 append → 产出超限分片，平台按长度拒收整条消息。修复：append 前若 `element.Length > maxLength` 则按 code point 硬切。 |
| S3-13 | markdown 围栏跨片断裂 | 仍成立 | 同文件 `SplitText` 全文无围栏感知（只在 `FindBoundary` 里找空白/标点），```` ``` ```` 代码块、表格、引用块会被从中间切开 → 后一片渲染成普通文本。修复：切点避开三反引号区间，或非首片降级 `Plain` + 续片标记。 |
| S3-14 | `sequence` 非单调 | 仍成立（诊断） | `src/Natter.Chat.Discord/DiscordChatUpstream.cs:1082-1084`、`src/Natter.Chat.DingTalk/DingTalkChatUpstream.cs:311-313`：`Id = $"{prefix}:{Interlocked.Increment(ref _sequence)}"` 之后 `Sequence = _sequence`（**非原子读**）。并发下 status 事件的 `Sequence` 可能大于 `Id` 里的序号，甚至与前一条相同。仅影响诊断排序，不影响投递。修复：`var seq = Interlocked.Increment(ref _sequence);` 复用同一值。 |
| S3-15 | Satori 有损映射 | 仍成立（边角） | `src/Natter.Chat.Satori/SatoriContentMapper.cs:111-118`：`<a>` 无条件追加 `(href)`（与文本自带 URL 重复）；`:64-88`：`<at type="role">` 落成 `MentionKind.User`（下游 mention 策略会把 role 当 user 错判）；`:164-165`：附件 URL 非法时静默丢弃、无计数。属显示/策略精度问题。 |
| S3-16 | 附件守卫覆盖面 | 不值得修 | 守卫已覆盖三层：`src/Natter.Chat/Model/Content.cs:43`（每次构造 `Content` 校验每个附件）、`src/Natter.Chat/Degradation/ChatCapabilityDegrader.cs:51`（出站降级时校验）、`src/Natter.Chat.Satori/SatoriContentMapper.cs:164`。剩余“静默丢弃”属 S3-15。Discord/DingTalk 声明 `Attachments=false`，附件在降级阶段即被剥离，不存在未守门路径。 |
| S3-18 | `ConversationKinds` 组合语义导致 Satori 私聊出站被判 unsupported | 已修复 | `src/Natter.Chat.Satori/SatoriCapabilityMapper.cs:82-91`：`DeriveConversationKinds` 现在**无条件** `kinds.Add(ConversationKind.Direct)`（注释：「Satori always delivers direct messages…this is a known platform capability」），`guild.plain` 只追加 Guild，不再出现“只有 Guild → 私聊回复被 dispatcher 判 unsupported”。修复提交 `3068ec7`（**晚于**报告 14:43）→ 四项里唯一一条真正的「已修复」。遗留小项：`Capabilities.SupportsConversation` 的 `Unknown` 通配语义仍未写进注释（`Capabilities.cs:60`）。 |
| S3-17 | 时间戳宽容解析按本机时区解释 | 仍成立（边角） | `src/Natter.Chat/Serialization/NlmpJson.cs:21`：`DateTimeOffset.TryParse(text, InvariantCulture, DateTimeStyles.RoundtripKind, out value)`。`RoundtripKind` 只保留 `DateTime.Kind`，**不带偏移的输入（`2026-11-22T12:00:00`）仍按本机时区解释**，同一份 JSON 在不同时区主机上得到不同 UTC 瞬间。修复：无 `Z`/偏移时直接 `throw JsonException`（契约是 RFC 3339 UTC），或显式 `AssumeUniversal`。注意：原报告已排除“RoundtripKind 会抛异常”那条误报，本条只讲宽容度。 |

### 1.4 CLI / 脚本（12）

| ID | 项 | 判定 | 证据 / 理由 |
| --- | --- | --- | --- |
| S3-19 | `--verbose` 形同虚设 | 仍成立 | `src/Natter.Cli.Core/Logging/CliLoggerProvider.cs:52-57`：`Log` 里无条件 `_console.Error.WriteLine(exception.ToString())`，完全不看 logLevel。`NatterCli.cs:95` 的 `--verbose` 只改**最小级别**（Debug/Warning），异常栈在任何级别都打印 → 默认命令异常就吐完整栈（含内部路径）。修复：仅 `logLevel >= Debug \|\| verbose` 时打印 `exception.ToString()`，把 verbose 传给 provider。 |
| S3-20 | 取消与“时长到期”共用退出码 130 | 记录过期 | `src/Natter.Cli.Core/Cli/NatterCli.cs:151-162`：`durationSeconds > 0 && !cancellationToken.IsCancellationRequested` → `return CliExitCodes.Success`（0）；用户取消才 130。该语义由 `4885345`（2026-11-17，早于报告）引入，报告未反映。 |
| S3-21 | `memory export` 先写后 chmod | 仍成立（安全·小修） | `src/Natter.Cli.Core/Commands/MemoryCommands.cs:161-165`：`File.WriteAllTextAsync(path, json)` 之后才 `File.SetUnixFileMode(..., 0600)`。窗口期内文件按 umask（常 0644）可被同机其他用户读取，内容含记忆正文。修复：`File.Open(path, Create, Write, None)` 在创建时就带 0600（`FileStreamOptions.UnixCreateMode`）。 |
| S3-22 | `rotate-secret.sh` 无 trap | 不值得修 | `scripts/rotate-secret.sh:126,145,151`：`cp -p` 备份 → 写临时文件 → `mv -f` 原子替换 → 校验失败 `cp -p "$backup" "$FILE"` 回滚。失败路径只可能残留一个 `tmp` 文件，目标文件要么旧要么新（不会半写）。加 trap 的收益仅是清 tmp。 |
| S3-23 | `soak-sample.sh` 无 jq 回退写出坏样本 | 仍成立 | `scripts/soak-sample.sh:169-171`：无 jq 时 `printf '%s\n' "$body" >>"$OUT"` —— 直接把原始 health body 追加进 JSONL，**没有 `sampledAt`/`source` 字段**（注释自己写着 “separate prefix-free line”）。`soak-report.py` 按 `sampledAt` 采样，这类行会被计入样本但无时间戳 → 覆盖率/间隔判定失真。修复：无 jq 时也要用纯 bash 拼一行带 `sampledAt` 的 JSON（脚本里已有 `boot_suffix` 的字符串拼接范式可复用）。 |
| S3-24 | `ops-health-alert.sh` 无锁 | 仍成立（低） | `scripts/ops-health-alert.sh:311-322`：读 `STATE_FILE` → 组 snapshot → 直接覆盖写，全程无 `flock`。两个实例并发（timer 重叠 + 手工执行）会交错写，可能漏报一次状态变化。修复：`exec 9>"$STATE_FILE.lock"; flock -n 9` 一行。 |
| S3-25 | `p2-serve.sh` 日期写死 | 仍成立（低） | `scripts/p2-serve.sh:13`：`RUN_DIR="${NATTER_P2_RUN:-$ROOT/artifacts/p2/2026-11-15-1647}"` 默认日期写死；可用环境变量覆盖，但默认值永久指向历史运行目录。 |
| S3-26 | `stage-tier1-plugins.sh` 硬编码 TFM | 仍成立 | `scripts/stage-tier1-plugins.sh:161`：`bin_dir="$ROOT/$(dirname "$project")/bin/$CONFIGURATION/net10.0"`。TFM 升级（net11.0）或 RID 特定输出时静默找不到产物。修复：从 `$(dotnet msbuild -getProperty:TargetFramework …)` 读，或用 `bin/$CONFIGURATION/net*` glob 并断言唯一命中。lead 已确认此条。 |
| S3-27 | `webconsole-e2e.sh` 60s 预算与端口占用 | 仍成立（低） | `scripts/webconsole-e2e.sh:95`：`serve --duration 60` 写死，整套矩阵（构建 + 20+ 断言 + rollback）必须挤进 60s；`:14-15` 端口有 env 覆盖（`WEBCONSOLE_E2E_PORT`/`WEBCONSOLE_E2E_HEALTH_PORT`），但仍无占用预检 → 残留进程只表现为 “console did not start”。 |
| S3-28 | 脚本权限位不齐 | 记录过期 | HEAD 的 `git ls-files -s scripts/`：18 个 `100755`，唯一 `100644` 是 `scripts/publish.ps1`（PowerShell 不需要执行位，正确）。报告写“不齐”时的问题已不存在。 |
| S3-29 | `shellcheck` 白名单只覆盖新脚本 | 仍成立 | `.github/workflows/deploy-verify.yml:60-71` 的 `files=(…)` 只列 **8 个**（`deploy/docker/verify-image.sh`、`deploy/systemd/{install,verify}.sh`、`scripts/{stage-tier1-plugins,soak-sample,ops-health-alert,discord-egress-probe,rotate-secret}.sh`）。仓库共 19 个 `.sh`，`webconsole-e2e.sh`、`natter-backup.sh`、`live-*.sh`、`p2-*.sh` 等 11 个从未被 shellcheck。修复：`files=($(git ls-files '*.sh' | grep -v '^build/'))`。 |
| S3-30 | `deploy-verify` 未固定 action SHA | 仍成立（供应链·低） | `.github/workflows/deploy-verify.yml:49,128`：`uses: actions/checkout@v5`；`:187`：`uses: actions/upload-artifact@v5`。tag 可被上游重指，未固定 `@<40位SHA>`。 |

### 1.5 部署（6）

| ID | 项 | 判定 | 证据 / 理由 |
| --- | --- | --- | --- |
| S3-31 | Dockerfile 基础镜像浮动 tag | 仍成立（低） | `deploy/docker/Dockerfile:18`：`FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build`；`:67`：`FROM …/runtime:10.0 AS runtime`。补丁版本随上游漂移，构建不可复现。可选：`@sha256:…` digest（需定期更新，运维成本）。 |
| S3-32 | `EXPOSE` 与“绝不发布端口”注释矛盾 | 记录过期 | `deploy/docker/compose.yaml:36-39` 用的是 **`expose:`**（仅容器网络内可见），注释与配置一致；`Dockerfile:98` 的 `EXPOSE 5099` 只是镜像元数据，不发布到宿主。**不矛盾**，报告判断有误。 |
| S3-33 | `natter.service` 无资源上限 | 仍成立 | `deploy/systemd/natter.service:65` 只有 `LimitNOFILE=4096`，无 `MemoryMax`/`CPUQuota`/`TasksMax`。单进程内存失控（如缓存/队列）会拖垮整机。修复：按实测 RSS × 2 设 `MemoryMax`，`TasksMax=512`。 |
| S3-34 | `SuccessExitStatus=0` 空操作 | 仍成立（低） | `deploy/systemd/natter-soak.service:20`：`SuccessExitStatus=0`。0 本来就是成功码，该行无任何效果（应删或改成实际会返回的非零成功码）。 |
| S3-35 | runbook 与脚本对 `/opt/natter` 属主相反 | 仍成立 | `deploy/systemd/install.sh:88-90`：`chown -R root:root` + `chmod go-w`（只读发布产物）；`docs/OPERATIONS_RUNBOOK.md:53`：`chown -R natter:natter /opt/natter`（服务用户可写）。照 runbook 操作会让发布目录可被服务进程改写（与 `ProtectSystem=strict` 的意图冲突）。修复：runbook 改为 `root:root` + 只读，状态目录单独 `natter:natter`。 |
| S3-36 | `SystemCallFilter` 是否过严待确认 | 不值得修 | `natter.service:62`：`SystemCallFilter=@system-service`（systemd 官方为常规服务准备的集合，覆盖 .NET 所需的 `clone/futex/epoll/socket/…`），同 unit 还有 `ProtectSystem=strict` + `NoNewPrivileges=yes`。仓库内无任何启动失败证据；真要证伪必须在 root + 真 systemd 上跑（当前环境 BLOCKED）。保持现状并留观察项即可。 |

### 1.6 文档（9）

| ID | 项 | 判定 | 证据 / 理由 |
| --- | --- | --- | --- |
| S3-37 | 2 个失效相对链接 | 仍成立（文档） | `docs/HARNESS_RESPONSIBILITY.md:5` → `[插件模型](PLUGIN_MODEL.md)`，`docs/PLUGIN_MODEL.md` 不存在；`docs/P2_RUNTIME_PROFILE.md:9` → `[P2 验收处置](testing/2026-11-15-p2-acceptance-response.md)`，该文件不存在（现有的是 `2026-11-15-p4-acceptance-response.md`）。 |
| S3-38 | README 命令表未转义 `\|` | 仍成立（文档） | `README.md:173`：`` | `forget <id|text>` | 删除显式记忆并失效 summary | … | `` —— 单元格内的 `\|` 被当成列分隔符，该行渲染多出一列。修复：`<id\|text>`（反斜杠转义）。 |
| S3-39 | README 工程表缺插件/测试工程 | 仍成立（文档） | `README.md:94-101` 只列到 `tests/Natter.Live.Tests`。`Natter.slnx` 有 **30** 个工程，README 缺：`plugins/Natter.Plugin.{PromptLibrary,WebConsole,ConversationConsole}`（3）、`tests/Natter.Plugin.{PromptLibrary,WebConsole,ConversationConsole}.Tests`（3）、`samples/Natter.Sample.Plugin.{Memory,Discord,DingTalk}`（3）。原报告说“缺 2+2”，实际缺口更大。 |
| S3-40 | `docs/testing/README.md` 索引缺记录 | 仍成立（文档） | 该索引未收录 **7** 份现存记录：`2026-11-21-p5-followup-regression.md`、`2026-11-21-webconsole-phase23-acceptance.md`、`2026-11-22-final-s1-acceptance.md`、`2026-11-22-p5-task-ux-acceptance.md`、`2026-11-22-review-acceptance.md`、`2026-11-22-s2-batch1-acceptance.md`、`2026-11-22-s2-test-quality-acceptance.md`。 |
| S3-41 | `DEVELOPMENT.md` 的 `ReferenceOutputAssembly` 机制不存在 | 记录过期 | `docs/DEVELOPMENT.md:705` 声称测试工程用 `ProjectReference … ReferenceOutputAssembly="false"` + MSBuild `Copy`；实际 `tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj:26-56` 是**普通 ProjectReference + `CopyTestPlugins` 目标**，全仓无 `ReferenceOutputAssembly`。文档描述与实现不符 → 改文档。 |
| S3-42 | `scripts/test.sh` 只跑一个套件 | 仍成立（低） | `scripts/test.sh:8` 只跑 `tests/Natter.Framework.Tests`。CI 已遍历全部 4 个套件（S1-09 修复），但本地/手工入口仍只覆盖 1/4，容易本地全绿而插件套件红。 |
| S3-43 | `LIVE_TEST_PLAN` 漏用例 | 仍成立（文档） | 实测 live 测试 **31** 个（`[LiveFact]`/`[DeepSeekLiveFact]`/`[DiscordLiveFact]`/…），`docs/LIVE_TEST_PLAN.md` §5 只列约 24 个 → 漏 **7** 个（原报告说 12 个，缺口已缩小）。 |
| S3-44 | `HARNESS_RESPONSIBILITY` 的 mandatory 迁移未落地 | 记录过期（文档项） | 代码已迁移：`src/Natter.Application/Chat/Context/PromptSafety.cs` 与 `PromptCompilerContracts.cs` 已无 `mandatory`/`Mandatory`（`f8f17b2`，2026-11-22 13:20，**早于报告**）。文档 `docs/HARNESS_RESPONSIBILITY.md:4,118,127` 仍是“待迁移”口吻（含 `prompt.mandatory_ignored` warning 的旧行为描述）。lead 已知项，确认：**代码对、文档错**。 |
| S3-45 | OBS-4 已修复但记录未更新 | 记录过期（文档项） | 代码已拒绝 allowlist 内的私网：`src/Natter.Application/Chat/Tools/HttpFetchTool.cs:32,122,139,169`（`IsBlockedAddress` → `address_not_allowed`），由 `d9f47ba`（2026-11-21 18:01，早于报告）引入，并有测试覆盖。`docs/testing/2026-11-21-p5-followup-regression.md:22,72` 仍写“allowlist 内私网仍可访问（OBS-4）”。lead 已知项，确认：**代码对、文档错**。 |

---

## 2. 问题一：仍成立项的风险分级

**触及核心链路（4 项）**

| ID | 链路 | 为什么算核心 |
| --- | --- | --- |
| S3-12 | 出站回复（所有适配器共用 `ChatTextChunker`） | 超限分片会被平台整条拒收 → 用户收不到回复 |
| S3-13 | 出站回复渲染 | 代码块/表格被切断 → 长回答可读性直接受损 |
| S3-15 | 入站映射 → mention 策略 | `<at type="role">` 当 user 会让下游 mention/允许策略错判 |
| S3-10 | 审计 | 取消时静默丢审计行，事后无法区分“没发生”与“被取消” |

**旁路（6 项）**：S3-14（诊断序号）、S3-17（时区宽容度）、S3-19（CLI 噪声）、S3-21（导出文件权限窗口）、S3-23（soak 样本质量）、S3-02（端口名解析）。

**运维/文档（17 项）**：S3-24/25/26/27/29/30/31/33/34/35/37/38/39/40/42/43 + S3-04（分层结构）。

> 明确结论：**没有任何 S3 项落在 Context 装配、prompt 组装、编排并发、插件信任这些本轮 S1/S2 重点链路上**。S3 的整体定位（低优先级）是准确的。

---

## 3. 问题二：按写作用域归组

| 组 | 目录 | 项 | 备注 |
| --- | --- | --- | --- |
| G1 聊天模型 | `src/Natter.Chat/` | S3-12, S3-13, S3-17 | 同文件/同目录，一个 agent 可一次改完；**建议合并成一个提交** |
| G2 适配器细节 | `src/Natter.Chat.Satori/`、`src/Natter.Chat.Discord/`、`src/Natter.Chat.DingTalk/` | S3-15、S3-14 | Satori 一项 + 两个 adapters 各一行（sequence 原子读） |
| G3 组合根 / 基础设施 | `src/Natter.Infrastructure/` | S3-02, S3-04, S3-10 | S3-04 会牵动 `Natter.Plugin.Abstractions`，需单独评审 |
| G4 CLI 内核 | `src/Natter.Cli.Core/` | S3-19, S3-21 | 同目录、互不重叠 |
| G5 脚本 | `scripts/` | S3-23, S3-24, S3-25, S3-26, S3-27, S3-42 | 全是 bash 单文件，可并行 |
| G6 CI | `.github/workflows/` | S3-29, S3-30 | 单文件，与 G5 有 `shellcheck` 文件清单交集（S3-29 依赖 G5 的脚本集合，但脚本本身不用改） |
| G7 部署单元 | `deploy/` | S3-31, S3-33, S3-34, S3-35 | 其中 S3-35 还需改 `docs/OPERATIONS_RUNBOOK.md`（与 G8 交集一行） |
| G8 文档 | `docs/`、`README.md` | S3-37, S3-38, S3-39, S3-40, S3-41, S3-43, S3-44, S3-45 | 纯文档，零运行时风险；`docs/OPERATIONS_RUNBOOK.md` 与 G7 有一行交集 |

> 并行建议：G1/G2/G3/G4/G5/G6 互不重叠，可 6 个 agent 同时开工；G8 与所有组都不冲突（除了 G7 的 runbook 一行），可在任何时刻并行。**唯一冲突点是 `docs/OPERATIONS_RUNBOOK.md`（G7 的 S3-35 与 G8）和 `shellcheck` 文件清单（G5/G6 语义耦合）**，派工时把这两处串行即可。

---

## 4. 问题三：与在飞批次的冲突

| 在飞目录 | 45 项中落在这里的 | 结论 |
| --- | --- | --- |
| `src/Natter.Application/Chat/Context/` | **0 项**（S3-44 是文档，不是 Context 代码） | 不冲突 |
| `plugins/Natter.Plugin.WebConsole/` | **0 项**（S3-27 是 `scripts/webconsole-e2e.sh`，不在插件目录） | 不冲突 |
| `plugins/Natter.Plugin.StateVitals/` | **0 项** | 不冲突 |

**结论：S3 的 27 个仍成立项没有一个落在三个在飞目录里。** 唯一需要留意的是 `src/Natter.Chat/`（G1，3 项）——它是所有适配器的公共依赖，改动会触发全量回归，但不会与在飞文件产生文本冲突（在飞批次改的是 Application/Context 与两个插件目录）。

---

## 5. 问题四：建议修复批次（价值 ÷ 风险）

### 批次 A · 文档对齐（价值高 / 风险≈0）
**S3-37, 38, 39, 40, 41, 43, 44, 45**（8 项，含全部 7 个「记录过期」的文档侧）
- 改动面：`README.md`、`docs/HARNESS_RESPONSIBILITY.md`、`docs/P2_RUNTIME_PROFILE.md`、`docs/testing/README.md`、`docs/DEVELOPMENT.md`、`docs/LIVE_TEST_PLAN.md`、`docs/testing/2026-11-21-p5-followup-regression.md`。
- 为什么排第一：不碰代码、不动构建、可与其他批次完全并行，且直接消除“报告说有问题、其实是文档旧”的认知成本（本轮已因此误判 7 项）。
- 预估：1 个 agent，1–2 小时。

### 批次 B · 回复质量与安全小修（价值中高 / 风险中低）
**S3-12, 13, 17（G1）+ S3-14, 15（G2）+ S3-10（G3）+ S3-19, 21（G4）**（8 项）
- 改动面：`ChatTextChunker`（新增围栏感知 + 超长 element 硬切）、`NlmpJson`（严格 UTC）、`SatoriContentMapper`、两个 upstream 的 sequence 一行、`JsonlAdminAuditSink` 取消分支、`CliLoggerProvider` 栈打印条件、`MemoryCommands` 创建即 0600。
- 风险点：`ChatTextChunker` 是公共依赖，需全量框架 + 适配器回归；建议单独提交、单独 reviewer。
- 预估：2 个 agent（G1+G2 一个、G3+G4 一个），半天。

### 批次 C · 运维与 CI 一致性（价值中 / 风险低，但与运行环境相关）
**S3-23, 24, 25, 26, 27, 29, 30（G5+G6）+ S3-31, 33, 34, 35（G7）**（11 项）
- 改动面：`scripts/*.sh`、`.github/workflows/deploy-verify.yml`、`deploy/{docker,systemd}/`、runbook 一行。
- 注意：S3-33（资源上限）与 S3-35（属主）会改变生产部署行为，**必须先在一台真机上 `systemd-analyze verify` + 灰度**（本机 `pid1=bwrap` 无法验证，沿用既知 BLOCKED）。
- 预估：1 个 ops agent，半天 + 一次灰度。

### 暂不排期（需要设计决策或证据）
- **S3-04**（Infrastructure → Cli.Abstractions 反向依赖）：要动分层契约，建议与“插件 Host API 收敛”一起做，不单列。
- **S3-02**（端口名后缀匹配）：需先确认插件是否真的依赖短名（否则直接改成精确匹配 + 报错）。
- **S3-31**（镜像 digest）：取决于是否引入 dependabot/renovate 维护 digest。
- 不值得修的 10 项（S3-01/03/05/06/08/09/11/16/22/36）建议在报告中标注为“已评估、不排期”，避免下次重复审查。

---

## 6. lead 指定 8 条的核实结论

| # | lead 的怀疑 | 结论 | 证据 |
| --- | --- | --- | --- |
| 1 | `stage-tier1-plugins.sh` 硬编码 `net10.0` | **成立** | `scripts/stage-tier1-plugins.sh:161` |
| 2 | `TaskMetrics` 可为负被 `Math.Max(0,…)` 掩盖 | **不成立（已修）** | `9b72971`；配对见 `TaskDispatcher.cs:89-90,343-369` |
| 3 | 分片可超出 `maxLength` | **成立** | `ChatTextChunker.cs:55-63`（单 element > limit 时无 flush） |
| 4 | markdown 围栏跨片断裂 | **成立** | `ChatTextChunker.SplitText` 无围栏感知 |
| 5 | `JsonlAdminAuditSink` 把取消当跳过审计 | **成立** | `JsonlAdminAuditSink.cs:61-64` |
| 6 | `natter.service` 无资源上限 | **成立** | `natter.service:65` 仅 `LimitNOFILE` |
| 7 | `ApplicationDispatcher` 每次反射 `MakeGenericMethod` | **成立但判“不值得修”** | `ApplicationDispatcher.cs:28,36`；量级 1–3 µs/次 |
| 8 | 文档 9 项里哪些真失效 / 只是数字过期 | 需改文档/代码的失效项 **4**：37（2 个死链）、38（表格 `\|` 未转义）、39（工程表缺 3 插件 + 3 测试 + 3 sample）、40（索引缺 7 份）；纯数字/状态过期 **4**：43（漏 12→7）、41（机制描述不符）、44（mandatory 已迁移）、45（OBS-4 已修）；42 不属文档类，是脚本行为（`test.sh` 只跑 1/4 套件） | 见 §1.6 |

---

## 7. 证据与复现

- 干净副本：`artifacts/review-s3/head/`（`git archive eadcd77` 解包，gitignore）。
- 关键取证命令与输出摘要：`artifacts/review-s3/evidence.md`。
- 复核未使用工作区未提交文件；所有行号以 `eadcd77` 为准。若要复核本报告，请在干净副本里执行：
  ```bash
  cd artifacts/review-s3/head
  sed -n '55,63p' src/Natter.Chat/Reliability/ChatTextChunker.cs
  sed -n '40,42p' src/Natter.Infrastructure/Composition/PortAdapterRegistry.cs
  sed -n '61,64p' src/Natter.Infrastructure/Admin/JsonlAdminAuditSink.cs
  sed -n '161p' scripts/stage-tier1-plugins.sh
  sed -n '169,171p' scripts/soak-sample.sh
  sed -n '52,57p' src/Natter.Cli.Core/Logging/CliLoggerProvider.cs
  ```
