# 插件体系安全与代码审查（NPS v1）

- 审查对象：`src/Natter.Plugin.Abstractions`、`src/Natter.Plugin.Store`、`src/Natter.Infrastructure/Plugins`（含 `Nps/`）、`src/Natter.Hosting.Android`，参照 `docs/PLUGIN_SPEC.md`、`docs/PLUGINS.md`、`docs/ANDROID_PLUGIN_HOST.md`
- 方式：只读静态审查（排除 `bin/obj`），无源码/配置/测试改动；未运行集成测试（本会话无构建权限范围内的写入点）
- 时间：2026-11-22
- 结论：zip 路径穿越（`..`/绝对路径/符号链接/重复条目）防护是**扎实**的，主要问题集中在 **store 的路径汇聚点未校验 pluginId**、**解压阶段不校验真实字节数**、**签名结果在运行时被丢弃**、**contribution 权限表存在缺口**、**ALC 无法真正回收**。

## 0. 严重度统计

| 严重度 | 数量 | 分布 |
| --- | --- | --- |
| P0 | 1 | Store 路径汇聚（任意目录删除） |
| P1 | 6 | 解压 DoS、信任/权限、权限表缺口、ALC 泄漏、依赖图环形爆炸、安装非原子 |
| P2 | 12 | 签名规范化、常量时间、完整性复验、并发、legacy 路径、路由器泄漏、事件订阅、原生库绕过、Keystore 信任根、UI 线程阻塞、状态文件损坏 |
| P3 | 10 | 死配置、CLI 平台硬编码、回滚字段陈旧、Reconcile 宽松匹配、敏感文件规则、secret 引用、文档不一致、日志脱敏等 |
| 合计 | 29 | |

## 1. Top 8 发现

| # | 等级 | 位置 | 一句话 |
| --- | --- | --- | --- |
| 1 | P0 | `src/Natter.Plugin.Store/PluginStore.cs:445-449`（配合 `:60-64`） | `UninstallAsync` 把未校验的 pluginId 直接拼进路径并递归删除，`--delete-data/--delete-config` 可删除任意目录/文件 |
| 2 | P1 | `src/Natter.Plugin.Store/PluginPackageValidator.cs:162-172` + `:229-231` | zip bomb 上限只取自中央目录声明的 `entry.Length`，解压按流原样写出，伪造长度即可写满磁盘 |
| 3 | P1 | `src/Natter.Infrastructure/Composition/NatterApplicationBuilder.cs:331-334` + `src/Natter.Infrastructure/Plugins/Nps/NpsPluginOptions.cs:12-16,98-110` | store 里 `signed/SignerKeyId` 结果在运行时被丢弃，所有文件插件按 `Local` 处理并自动授予**全部**声明权限（含 `admin.command.execute`） |
| 4 | P1 | `src/Natter.Infrastructure/Plugins/Nps/NpsPluginOptions.cs:115-129` + `NpsContributionRegistry.cs:127-149` + `ContextContracts.cs:44-55` | `context.assembler` / `prompt.provider` / `prompt.fragment` 没有任何权限要求，却直接拿到 `RecentMessages`、`Memories`、`Summary`，绕过 `chat.read`/`storage` |
| 5 | P1 | `src/Natter.Infrastructure/Plugins/Nps/NpsPluginCandidate.cs:213-227` + `NpsPluginRuntime.cs:850-861` | `DisposeLoadContext()` 只 `Unload()`，`Plugin`/`EntryType`/`Registrations` 仍持有 collectible ALC 类型，ALC 永不回收 |
| 6 | P1 | `src/Natter.Infrastructure/Plugins/Nps/NpsPluginGraph.cs:257-293` | 任一环（含 `loadAfter/loadBefore` 形成的环）把 `order` 置空，导致**全宿主**插件都不启动，且非环成员无错误 |
| 7 | P1 | `src/Natter.Plugin.Store/PluginStore.cs:171-218` | 安装非原子：先移动版本目录，再写 `installed.json`，最后写 `pending.json`；第三步失败时 `finally` 会删掉已被 state 引用的版本目录 |
| 8 | P2 | `src/Natter.Plugin.Store/PluginStore.cs:83-114` + `NatterApplicationBuilder.cs:300-335` | 安装时的哈希/签名只校验一次，加载时对已安装目录零复验，落地后改写文件不会被发现 |

## 2. 最关键的 5 个文件

1. `src/Natter.Plugin.Store/PluginStore.cs` — 唯一的状态机与所有路径拼接汇聚点（P0 在此）
2. `src/Natter.Plugin.Store/PluginPackageValidator.cs` — zip 边界、哈希、签名入口（P1/P2 密集）
3. `src/Natter.Infrastructure/Plugins/Nps/NpsPluginRuntime.cs` — 激活/回滚/贡献提交/事件订阅
4. `src/Natter.Infrastructure/Plugins/Nps/NpsPluginOptions.cs` — 信任与权限判决（`Decide` + `RequiredPermissions`）
5. `src/Natter.Infrastructure/Plugins/Nps/NpsPluginCandidate.cs` — ALC 生命周期与候选状态（卸载泄漏点）

---

## 3. P0

### P0-1 `UninstallAsync` 未校验 pluginId，可递归删除任意目录

`src/Natter.Plugin.Store/PluginStore.cs:60-64`

```csharp
public string GetPluginConfigPath(string pluginId)
    => Path.Combine(ConfigDirectory, pluginId + ".json");

public string GetPluginDataDirectory(string pluginId)
    => Path.Combine(DataDirectory, pluginId);
```

`src/Natter.Plugin.Store/PluginStore.cs:425-449`

```csharp
if (record is not null) { ... Directory.Move(pluginRoot, Path.Combine(trash, pluginId)); ... }
...
if (deleteConfig) TryDeleteFile(GetPluginConfigPath(pluginId));
if (deleteData)   TryDeleteDirectory(GetPluginDataDirectory(pluginId));
```

- `pluginId` 从入口一路未校验：`src/Natter.Cli.Core/Commands/PluginStoreCommands.cs:186-190` 直接传 CLI 位置参数。
- `Path.Combine(DataDirectory, "/etc")` 在 .NET 返回 `/etc`（第二段为 root 时直接返回它）；`Path.Combine(DataDirectory, "../../x")` 会正常解析。随后 `TryDeleteDirectory` 走 `Directory.Delete(..., recursive: true)`。
- 关键点：这两行**在 `record is not null` 分支之外**，即使插件根本没安装（record 为 null）也会执行，因此任意 id 都能触发；`TryDelete*` 吞掉异常，返回成功，用户无感知。
- 影响：`natter plugins store uninstall <crafted-id> --delete-data`（或任何把 id 转发给该 API 的管理 UI）可递归删除 app 私有目录外的任意路径。Android 上等于 app 进程权限内的任意删除。
- 修复方向：所有以 id 拼路径的私有方法统一走一个 `ResolvePluginId(id)`：先用 `PluginManifestReader.IdPattern` 校验（`^[a-z0-9]+(?:[._-][a-z0-9]+)+$`），再 `Path.GetFullPath` + 前缀断言（与 `PluginPackageValidator.ExtractAsync:211-217` 同款）；`deleteConfig/deleteData` 移入 `record is not null` 分支；`TryDelete*` 记录失败原因而不是全吞。

## 4. P1

### P1-1 zip 解压不校验真实字节数，声明长度可伪造

`src/Natter.Plugin.Store/PluginPackageValidator.cs:162-172`

```csharp
if (entry.Length > _options.MaxEntryUncompressedBytes) { ... }
totalUncompressed += entry.Length;
totalCompressed += Math.Max(1, entry.CompressedLength);
```

`src/Natter.Plugin.Store/PluginPackageValidator.cs:229-231`

```csharp
await using var input = entry.Open();
await using var output = new FileStream(destination, FileMode.CreateNew, ...);
await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
```

- `ZipArchiveEntry.Length` 来自中央目录，是**声明值**；`DeflateStream` 解压时不按该值截断，CRC 校验发生在流末尾。攻击者构造「声明 1 KB / 实际 4 GB」的条目即可完全绕过 `MaxEntryUncompressedBytes`（128 MB）与 `MaxTotalUncompressedBytes`（256 MB）、`MaxCompressionRatio`。
- 影响：只需一个几十 KB 的包，就能在解压阶段写满 `FilesDir`（Android 上直接导致宿主不可用）；`InvalidDataException`（CRC 不匹配）在磁盘写满之后才抛出，来不及止损。
- **待确认**：不同 .NET 版本的 `DeflateStream`/`ZipArchiveEntry` 是否在流末尾额外比对声明长度；无论是否有该检查，写盘都发生在校验之前，DoS 窗口不变。
- 修复方向：`CopyToAsync` 换成带计数的循环，超过 `entry.Length` 或全局配额立即中止并删除 staging；写完后再比对「实际写入 == entry.Length」，不一致即拒绝。

### P1-2 签名/信任结果在运行时被丢弃；文件插件一律按 Local 自动授予全部权限

`src/Natter.Infrastructure/Composition/NatterApplicationBuilder.cs:331-334`

```csharp
result.Candidates.Add(NpsPluginCandidate.FromManifest(
    read.Manifest, fullPath, read, _npsOptions.FilePluginTrustLevel));
```

`src/Natter.Infrastructure/Plugins/Nps/NpsPluginOptions.cs:10-16, 98-104`

```csharp
public PluginTrustLevel FilePluginTrustLevel { get; set; } = PluginTrustLevel.Local;
public bool AllowLocalPlugins { get; set; } = true;
public bool AutoGrantLocalPermissions { get; set; } = true;
...
PluginTrustLevel.Local or PluginTrustLevel.Signed => options.AllowLocalPlugins && options.AutoGrantLocalPermissions,
```

- `PluginStore` 明明记录了 `Trust = package.SignerKeyId is not null ? "signed" : "unsigned"` 与 `SignerKeyId`（`PluginStore.cs:184-185`），但运行时只读 `installed.json` 的目录列表（`PluginStore.cs:83-114`），从不读 `Trust/SignerKeyId`：签名校验在 store 里是严格的，到运行时**归零**。
- 直接后果：任何声明 `permissions: ["admin.read","admin.config.write","admin.command.execute"]` 的插件（未签名也照样），在默认配置下全部 Granted（`Decide` 只看 trust + 两个开关），随后 `BindAdminServices`（`NpsPluginRuntime.cs:680-710`）把这些宿主服务注入插件 provider。
- 与规范冲突：`PLUGIN_SPEC.md` §9.1「`signed` → 用户确认后授予」、§4.6「宿主 MUST 在激活前授予/拒绝」；`ANDROID_PLUGIN_HOST.md:424`「permissions 在安装时展示，用户确认后授予」。仓库里不存在任何「用户确认」数据结构或 API（`InstalledPluginRecord.Permissions` 只是把 manifest 的请求原样抄了一份）。
- 修复方向：① `GetActivePluginDirectories` 之外提供 `IsSigned(id, keyId)`，运行时候选 trust 取 `signed/unsigned/local` 联合判断；② 引入 `installed.json.grantedPermissions`，安装/首次激活时由 UI 明确写入，`Decide` 只认它；③ `admin.*` 类权限默认 deny，必须 `ForceGrantedPermissions` 或用户确认。

### P1-3 `context.assembler`/`prompt.*` 无权限要求，构成 `chat.read`/`storage` 绕过

`src/Natter.Infrastructure/Plugins/Nps/NpsPluginOptions.cs:115-129`

```csharp
PluginContributionTypes.MemoryStore => [PluginPermissions.Storage],
_ => [],   // prompt.provider / prompt.fragment / context.assembler / port.adapter 全部落空
```

`src/Natter.Infrastructure/Plugins/Nps/NpsContributionRegistry.cs:127-149`（三个 `AddPrompt*`/`AddContextAssembler` 均**没有** `RequirePermissions(...)`，对比 `AddChatEventSubscriber:99` 有）

`src/Natter.Application/Chat/Context/ContextContracts.cs:44-55`

```csharp
public sealed record ContextRequest(RuntimeProfile Profile, PromptBundle Prompt, Source Source,
    Conversation Conversation, User User, string UserText,
    IReadOnlyList<StoredMessage> RecentMessages, IReadOnlyList<MemoryItem> Memories,
    ConversationSummary? Summary, ContextBudget Budget);
```

- 一个 `permissions: []` 的插件注册 `context.assembler`，就能在 `AssembleAsync` 里读到全部 `RecentMessages`、`Memories`（记忆库是 `storage` 权限保护的对象）、`Summary`、`UserText`——与「`chat.read` 才能读入站消息」「`storage` 才能用持久化端口」的模型直接矛盾。
- 同样 `prompt.provider` 拿到完整 `PromptRequest`（含 `Conversation`/`User`/`Variables`）。
- 附加：`NpsPluginRuntime.cs:647-648` 对所有插件无条件注入宿主 `IPromptFragmentSource`，任何插件可主动查询「全部插件的 fragment 聚合结果」（跨插件数据面）。
- 影响：权限模型在最有价值的两个数据类型（对话历史、记忆）上是失效的，且 `plugins permissions` 诊断会显示「无请求权限」，给人错误的安全印象。
- 修复方向：给 `prompt.provider`/`prompt.fragment`/`context.assembler` 定义并强制 `chat.read`（记忆/摘要再加 `storage`）；`IPromptFragmentSource` 只在持有对应权限时注入；`ContextRequest` 改为按权限裁剪的视图（无 `chat.read` 时 `RecentMessages` 为空）。

### P1-4 ALC 卸载是假卸载：collectible context 被候选对象长期引用

`src/Natter.Infrastructure/Plugins/Nps/NpsPluginCandidate.cs:213-227`

```csharp
public void DisposeLoadContext()
{
    try { LoadContext?.Unload(); } catch { }
    finally { LoadContext = null; }
}
```

`src/Natter.Infrastructure/Plugins/Nps/NpsPluginRuntime.cs:850-861`（回滚路径同样是 `DisposePluginInstanceAsync` + `DisposeLoadContext`）

- `DisposeLoadContext` 只清掉 `LoadContext` 字段，但实例上的引用没清：`Plugin`（ALC 内类型的实例）、`EntryType`（`Type`）、`Contributions.Registrations[i].ServiceType/ImplementationType`、`Descriptor.ErrorDetail`、`Context.Contributions` 全部跨越 ALC。
- `Unload()` 之后 ALC 仍被强引用 → GC 永不回收 → 程序集内存映射一直存在。**待确认（平台相关）**：Windows 上映射文件在 ALC 未回收前保持占用，`PluginStore.UninstallAsync` 的 `Directory.Move(pluginRoot → .trash)`（`PluginStore.cs:435`）会因文件占用抛 `IOException` 使卸载失败；Android/Linux 上表现为反复重载后的内存增长。
- 与 doc 冲突：`PLUGIN_SPEC.md` §7.3 第 4 步「卸载 ALC（best-effort）」、状态机要求能到 `Unloaded`——实际无法到达。
- 修复方向：`DisposeLoadContext` 里同时置空 `Plugin/EntryType/Contributions/Context/ConfigLoad`，`Registrations` 只用字符串/DTO 快照（不要存 `Type`）；`StopCandidateAsync`/`RollbackCandidateAsync` 后加弱引用验证（`WeakReference` + GC 断言，参考 `PluginLoadContextTests`），把 ALC 可回收作为验收条件。

### P1-5 依赖图出现环时整图 `order = []`，全宿主插件不启动

`src/Natter.Infrastructure/Plugins/Nps/NpsPluginGraph.cs:257-293`

```csharp
var order = TopologicalOrder(activeIds, edges, out var cycle);
if (cycle.Count > 0)
{
    ...
    order = [];
```

- `StartAsync` 只遍历 `_graph.Order`（`NpsPluginRuntime.cs:147-160`）：`order` 为空 ⇒ 所有健康插件都停在 `Configured`，既不激活也不报错（只有环成员写进 `Errors`）。
- 更要命的是 `loadAfter/loadBefore` 也被当作环边（`:345-350`），两个插件互相声明 `loadAfter` 属于「可协商的排序偏好」，却会让整个宿主插件系统静默停摆。
- 影响：单点配置错误 → 全局功能消失，`plugins list` 只显示 `Configured`，无法诊断。
- 修复方向：环内节点置 `Failed`，其余节点用「去掉环边后的 Kahn 序」继续；或对 `loadAfter/loadBefore` 环只降级为 warning 并回退字母序；无论如何 `order` 不能整体清空。

### P1-6 安装非原子：`installed.json` 先提交，版本目录后删

`src/Natter.Plugin.Store/PluginStore.cs:171-218`

```csharp
Directory.Move(extract, destination);       // 版本目录落地
...
WriteState(state);                          // installed.json 已指向新版本
WritePending(pending);                      // ← 若此处抛错
destination = null;
...
finally { if (destination is not null) TryDeleteDirectory(destination); }
```

- `WriteState` 成功、`WritePending` 失败（磁盘满、权限、JSON 序列化异常）时：`finally` 会把 `destination`（新版本目录）删掉，而 `installed.json` 已经把它记为 `ActiveVersion`。结果 store 记录指向不存在的目录，`GetActivePluginDirectories` 静默回退到 `PreviousVersion`（`PluginStore.cs:100-105`），或对首次安装直接返回空——用户看到「已安装但没加载」，无任何错误。
- 另外 `WriteState`/`WritePending` 是两次独立 `File.Move`（`PluginStore.cs:501-510`），本身也不满足 doc「原子写 installed.json」（`ANDROID_PLUGIN_HOST.md:283`）。
- 修复方向：把 `pending` 与 `installed` 合并进**一个**状态文档（单文件单次 rename），或先写 pending、再写 installed，失败时回滚并**保留**已落地的版本目录（`destination = null` 提前），最后由 `CleanupAsync` 回收孤儿目录。

## 5. P2

### P2-1 哈希比较非恒定时间

`src/Natter.Plugin.Store/PluginPackageValidator.cs:462`

```csharp
if (!string.Equals(expected, actual, StringComparison.Ordinal))
```

- 十六进制串逐字符早退比较。真实影响有限（哈希不是秘密，且拒绝路径还要求控制包），但如果将来把 `packageHash` 用于下载校验或作为 IPC 凭据，就是可观测的时序侧信道。**待确认**：当前是否有把 hash 结果暴露给远程调用方的路径。
- 修复方向：`CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual))`，或改存二进制字节数组。

### P2-2 签名规范化文本存在歧义（`hash + "  " + name + "\n"` 无转义）

`src/Natter.Plugin.Store/PluginPackageValidator.cs:468`、`PluginPackageSignatureVerifier.cs:61-62`、`PluginPackageWriter.cs:50`

```csharp
canonical.Append(actual).Append("  ").Append(file).Append('\n');
```

- 文件名未做转义/长度前缀，`IsSafeEntryPath`（`PluginPackageValidator.cs:235-259`）也不拒绝控制字符与换行。构造一个文件名内含 `"\n<hash>  b.dll"` 的条目，可让两个不同文件集合产生**逐字节相同**的签名载荷（把 `a.dll` 之后的 `b.dll` 行吞进文件名）。因为内容哈希必须匹配，攻击者不能注入新内容，但能重排/搬移已签名文件在包内的路径布局，并让「规范化」不再是一一映射。
- 修复方向：canonical 行改为 `hash + " " + length + " " + path`（或对 path 做 JSON/base64 编码）；`IsSafeEntryPath` 增加 `char.IsControl` 拒绝、拒绝以 `.` 结尾的空格名（Windows 归一化）。

### P2-3 `package.sig` 尺寸无上限 + 全量 `ReadAllBytes` 哈希

`src/Natter.Plugin.Store/PluginPackageValidator.cs:512`（`File.ReadAllBytes(signaturePath)`）、`:440-453`（逐文件 `ReadAllBytes` 后哈希）、`PluginPacking.cs:37-41`（条目上限 128 MB、总量 256 MB）

- 一个 100 MB 的 `package.sig` 会被整体读入内存，再经 `NormalizeSignature` 的 `Encoding.UTF8.GetString` + `.All(...)` 复制一遍（约 2× 内存 + 全量扫描）。哈希也是对每个文件整体读入。
- 影响：Android 低内存设备上安装期 OOM/ANR；`MaxEntryUncompressedBytes=128MB` 显然不是为签名/哈希设计的。
- 修复方向：`package.sig` 限制 ≤ 4 KB；哈希改 `SHA256.HashData(Stream)` 流式计算；`NormalizeSignature` 先判断长度再解码。

### P2-4 安装后无完整性复验

`src/Natter.Plugin.Store/PluginStore.cs:83-114` → `NatterApplicationBuilder.cs:300-335`

- 哈希/签名只在 `InstallAsync` 校验一次；之后宿主直接 `PluginManifestReader.Read(fullPath)` + `PluginLoadContext.LoadFromAssemblyPath`，不比对 `installed.json` 中记录的 `packageHash`，也不看 `package.sha256`。
- 影响：任何能写 `FilesDir/plugins/<id>/<ver>` 的路径（本地提权、备份还原、错误的分发脚本、共享存储误拷）都能替换 DLL，签名体系对「已安装态」零保护；且 `Trust` 字段仍是 `signed`，UI 会显示为已签名。
- 修复方向：加载前对活动版本目录做一次轻量复验（至少对 entry assembly + `plugin.json` 比对 `installed.json` 里存的 `packageHash`/逐文件哈希摘要）；不一致 → 状态 `Failed` + 明确错误码。

### P2-5 `CleanupAsync` 绕过 `_gate`；store 无跨进程锁

`src/Natter.Plugin.Store/PluginStore.cs:457-463`

```csharp
public Task CleanupAsync(CancellationToken cancellationToken = default)
{
    ClearDirectory(StagingDirectory);
    ClearDirectory(TrashDirectory);
```

- 其它所有变更都持 `_gate`，只有它不持；`MainActivity.cs:60` 启动时调用、CLI `plugins store cleanup` 也会调用。与 `InstallAsync` 并发时可能删掉在途 `.staging/<txn>/extract`，导致安装中途失败（错误信息是 `extract-failed`，不可诊断）。
- 跨进程：`_gate` 是 `SemaphoreSlim` 实例字段（`:26`），CLI（`NATTER_PLUGIN_STORE` 指向同一目录）与 app 同时写 `installed.json` 是 read-modify-write 竞争，可丢更新（安装被卸载覆盖）。
- 修复方向：`CleanupAsync` 持 `_gate`，并跳过 mtime 在 N 分钟内的 txn 目录；状态写入加跨进程文件锁（`FileStream` + `FileShare.None` 锁文件，或 `flock`）。

### P2-6 legacy 入口程序集路径未校验，可逃出插件目录

`src/Natter.Infrastructure/Plugins/PluginLoader.cs:94-102`

```csharp
var entryAssemblyName = manifest?.EntryAssembly ?? PluginDiscovery.FindEntryAssembly(directory, assemblies);
...
var entryPath = Path.Combine(directory, entryAssemblyName);
```

- legacy `plugin.json`（无 `schemaVersion`）的 `entryAssembly` 未过 `IsSafeRelativePath`，`"../../evil.dll"` / 绝对路径都会被 `Path.Combine` 接受并加载；NPS 路径有校验（`PluginManifestReader.ValidateEntry:259`），legacy 路径没有。
- 影响：`cliOptions.PluginDirectories` 里任意目录（含用户可写的示例插件目录）都能让宿主加载目录外 DLL；与 `PluginDir` 之外的「签名只覆盖 store 包」形成错位。
- 修复方向：复用 `PluginManifestReader.IsSafeRelativePath(entryAssemblyName, directory, out _)`；legacy `Id` 也做一次 pattern 校验。

### P2-7 激活回滚不回收 port adapter 注册与单例缓存

`src/Natter.Infrastructure/Plugins/Nps/NpsPortAdapterRouter.cs:30-34, 89-103`

```csharp
private readonly Dictionary<(string PluginId, Type ServiceType), object?> _pluginSingletons = [];
...
if (_pluginSingletons.TryGetValue((active.PluginId, serviceType), out var cached) && cached is not null)
    return cached;
```

- `Register`（`:54-69`）没有对应的 `Unregister`；`RollbackCandidateAsync`/`StopCandidateAsync` 也不清理 `_registrations`/`_pluginSingletons`/`_fallbacks`。功能上被 `GetPluginState == Activated` 兜住（`NpsRuntimeTests.Port_adapter_contribution_overrides_and_rolls_back_to_the_host_adapter` 已覆盖回退），但缓存里的实例是 ALC 内对象 → 又一处阻止回收的强引用；同一 serviceType 反复安装/回滚会线性累积。
- 修复方向：候选回滚时按 `pluginId` 移除注册与单例缓存；单例缓存改为 `WeakReference`。

### P2-8 事件订阅 fire-and-forget：乱序、无背压、回滚后仍可能回调

`src/Natter.Infrastructure/Plugins/Nps/NpsPluginRuntime.cs:1138-1153`

```csharp
public void OnNext(ChatEvent value) { _ = HandleAsync(value); }
```

- 每个事件一个新 Task：同一插件内事件处理**无序**、无背压（高频事件直接产生大量并发 Task）；`RollbackCandidateAsync:828-829` 只 `Dispose()` 订阅，已在飞行中的 `HandleAsync` 仍会在已 `DeactivateAsync`/`Dispose` 的插件对象上执行。
- 修复方向：每插件一个串行 channel + 单消费者循环（带容量与丢弃策略），`Stop` 时先 `Complete` 再 `await` 排空，超时后才强 Dispose。

### P2-9 「v1 禁止原生库」只按扩展名/PE 头判断，可绕过

`src/Natter.Plugin.Store/PluginPackageValidator.cs:174-179`、`:306-319`

- 扩展名黑名单是 `".so"/".dylib"/".a"/".exe"`（大小写不敏感比较缺失：`:176` 用 `extension is ".so"` 模式，`.SO` 会漏过），而 `.dll` 只做 PE CLI header 检查。把原生 ELF 改名为 `libfoo.dat`/`foo.bin`，包校验完全放过；插件内 `NativeLibrary.Load(dir + "/libfoo.dat")` 即可执行原生代码。
- 与 `ANDROID_PLUGIN_HOST.md:411`「v1 禁止原生库」不一致；如果将来真按 doc 放开 `.so`，相关 ABI/签名设计并未就位。
- 修复方向：对非文本/非托管候选文件做魔数嗅探（ELF/Mach-O/PE 无 CLI），不只是看扩展名；`AllowNativeLibraries=false` 时对整个包做一次二进制嗅探。

### P2-10 Android 信任根放在与包投放同一可写目录

`src/Natter.Hosting.Android/AndroidPluginHost.cs:119-131`

```csharp
var path = Path.Combine(context.FilesDir!.AbsolutePath, "plugin-trusted-keys.txt");
if (!File.Exists(path)) return [];
return File.ReadAllLines(path)...
```

- 公钥列表从 `FilesDir/plugin-trusted-keys.txt` 读取，而恶意包投放点是 `FilesDir/plugin-inbox`（`:99`）、安装目标是 `FilesDir/plugins`。能往 inbox 投包的一方（sideload/adb push/被诱导的用户操作）通常也能写 `plugin-trusted-keys.txt` → 追加自己的公钥即让任意自签包通过 `ValidateSignature`，签名体系对 sideload 形态失效。
- 注释自称「Production builds should embed publisher keys in the APK」，但 release 分支（`AllowUnsigned=false`）仍读这个文件，没有编译期注入路径。
- 修复方向：release 构建把公钥编译进程序集（`#if` 或 MSBuild 生成常量）；文件仅供 DEBUG；对文件做完整性校验（APK 签名绑定 / Android Keystore 签名保护）。

### P2-11 `ConfigureAsync` 在调用线程同步阻塞（Android 主线程）

`src/Natter.Infrastructure/Plugins/Nps/NpsPluginRuntime.cs:340, 1108-1117`

```csharp
RunSynchronously(candidate.Plugin!.ConfigureAsync(candidate.Context));
...
task.AsTask().GetAwaiter().GetResult();
```

- `MainActivity.cs:44` 用 `_ = RunCliAsync(console)` 从 `OnCreate` 启动，组合根在**主线程**执行；插件 `ConfigureAsync` 里的 `await` 若捕获到 UI `SynchronizationContext`（插件作者常见写法：`await Something();` 后续回到 UI 线程），`GetResult()` 会死锁，同时必然造成 ANR。
- 修复方向：`Prepare` 改 `PrepareAsync`，由宿主在后台线程 `await`；或在 Android head 用 `Task.Run` 承载整个 CLI 启动并把 `SynchronizationContext` 置空（至少别在主线程上 `GetResult`）。

### P2-12 `installed.json` 损坏即全量不可用，无修复路径

`src/Natter.Plugin.Store/PluginStore.cs:465-479`

```csharp
catch (Exception ex) when (ex is JsonException or IOException)
    throw new PluginStoreException($"Plugin store state '{StatePath}' is corrupt: {ex.Message}", ex);
```

- 一次截断/半写（`WriteJson` 的 tmp+move 只保护单次写；跨进程竞争或崩溃时机不当仍可留下损坏文件）就让 `GetInstalledPlugins`/`InstallAsync`/`UninstallAsync` 全部抛出，`plugins list` 直接失败，`CleanupAsync` 也救不了；无 `.bak`、无隔离带病启动。
- 修复方向：写入时保留 `installed.json.bak`，读取失败先尝试 bak；仍失败则把损坏文件改名留证并重建空状态，同时在 `plugins doctor` 上报。

## 6. P3

| # | 位置 | 问题 | 修复方向 |
| --- | --- | --- | --- |
| P3-1 | `src/Natter.Plugin.Store/PluginPacking.cs:49` | `AllowUpdates` 声明后全仓无引用（`grep` 为空），策略无法生效 | 在 `InstallAsync` 的 `existing is not null` 分支读取该选项 |
| P3-2 | `src/Natter.Cli.Core/Commands/PluginStoreCommands.cs:25-29` | `Platform = "android"` 硬编码：桌面 CLI 上 `platforms:["windows"]` 的合法包被拒、`android`-only 包被误收 | 用 `PluginPlatforms.Current`（`NpsPluginGraph.cs:447-457`） |
| P3-3 | `src/Natter.Plugin.Store/PluginStore.cs:275-284` | 回滚后 `record.Version` 仍为新版本、`PreviousVersion` 置空（无法二次回滚）、`Enabled` 保持 `true`，且 `GetActivePluginDirectories:100-105` 静默回退无诊断 | 回滚时同步 `Version`、保留一条历史、写 `FailureReason` 并在 `plugins list` 显示实际活动目录 |
| P3-4 | `src/Natter.Plugin.Store/PluginStore.cs:334-362` | `ReconcileAsync` 只按 `pluginId` 匹配节点，不比对 `candidate.Version`，旧版本被加载也会把新版本标记为 `Activated` | 比对 `node.Version == candidate.Version` 后再 confirm |
| P3-5 | `src/Natter.Plugin.Store/PluginPackageValidator.cs:519-535`（与 `Nps/PluginPackageInspector.cs:6-14` 同一份逻辑） | 敏感文件规则只匹配精确名：`prod.env`、`token.json`、`id_rsa`、`.npmrc` 全部漏过，doc「秘密不得随包发布」形同建议 | 改为后缀/前缀匹配 + 内容嗅探（`BEGIN PRIVATE KEY`、`sk-`） |
| P3-6 | `src/Natter.Infrastructure/Plugins/Nps/NpsPluginContext.cs:218-239` | `${secret:NAME}` 不会被宿主解析，`Get<T>()` 直接把字面量字符串交给插件；`TryGetSecretReference` 需插件自己调用，与 `PLUGIN_SPEC.md` §4.8 描述不一致 | 在 `NpsPluginConfig` 构造时按 schema 预解析为 `SecretReference`，或明确文档降级为「手写解析」 |
| P3-7 | `docs/PLUGIN_SPEC.md:1027,1032`（对照 `PluginStore`/`PluginPackageSignatureVerifier` 已实现） | 文档称「Phase 4：签名、市场未交付」「v1 可以只实现 bundled 和 local」，实际 store + ECDSA 已落地且被 Android head 使用；§4.3 第 13 项「磁盘空间足够」未实现 | 更新状态表，或在代码侧补齐磁盘余量检查 |
| P3-8 | `src/Natter.Infrastructure/Plugins/PluginCatalog.cs:52-65`；`PluginStore.cs:134, 507` | `UpdateDescriptor` 无 `break`，同 id 多描述符会全部被改写；`ExtractIdHint` 计算后立即被覆盖（死代码）；`installed.json.<guid>.tmp` 崩溃后永久残留 | 加 `break`；删死代码；`CleanupAsync` 清理 `*.tmp` |
| P3-9 | `src/Natter.Infrastructure/Plugins/Nps/NpsPluginContext.cs:48-60` | 日志缓冲与输出无脱敏设施，插件把 token 拼进 message 就原样进宿主日志与 `plugins doctor`，`PLUGIN_SPEC.md` §4.7「日志 MUST 脱敏」无对应实现 | 提供 `IPluginLog` 脱敏钩子（已知 secret 值做字符串替换），或在 `doctor`/`logs` 输出前过滤 |
| P3-10 | `src/Natter.Hosting.Android/AndroidKeystoreSecretProvider.cs:94-113, 57-62` | `GetKey()` 无锁且「存在即复用」：并发首用可能生成两把 key，先写者的密文永久解不开；`TryGet` 捕获全部异常静默返回 false，「secret 损坏」与「secret 缺失」不可区分 | `GetKey` 加锁 + 双检；失败区分 `SecretCorrupt` 并在 `plugins doctor` 报告 |

## 7. 已核验、未发现问题的点（避免重复投入）

- **zip 路径穿越**：`IsSafeEntryPath` 拒绝空段/`.`/`..`/前导 `/`，`ExtractAsync:211-217` 再做一次 `GetFullPath` + 前缀断言；`C:\abs` 这类名称在 Windows 上会被前缀检查拒绝；符号链接（`S_IFLNK`）与大小写重复条目均被拒（`PluginPackageValidator.cs:124-195`）。
- **缺失 manifest / 空签名 / 未配置公钥**：`ValidateAsync:120-140` 与 `ValidateSignature:499-516` 的处理是 fail-closed；`TryVerify:20-21` 正确拒绝空签名与空 canonical。
- **`plugin.json` 的 id/version 用于拼目录**：受 `IdPattern`（`IsSafeRelativePath` + `^[a-z0-9]+(?:[._-][a-z0-9]+)+$`）与 SemVer 解析约束，`manifest.Id` 无法注入 `..`/`/`。
- **宿主 API / 平台 / 依赖校验**：store 与运行时两处都查（`ValidateHostApiAndPlatform`、`NpsPluginGraph:107-131`），带 prerelease 的候选只匹配显式含 prerelease 的子句（`VersionRange.IsSatisfiedBy:99-101`），行为一致。
- **`IPluginSecrets` 读取**：每次 `Get` 都校验 `secrets:NAME`（`NpsPluginContext.cs:260-270`），错误信息不含值。
- **Chat 命令派发**：`chat.send/edit/delete/interactions` 在 `NpsPermissionedChatCommandDispatcher:22-40` 逐命令校验。
- **contribution 声明强绑定**：未声明或重复注册由 `NpsContributionRegistry.Stage/Require:190-239` 拒绝；`FindMissingContributions` + `DetectDuplicateContributions` 会让不一致的插件进入 `Failed`。
