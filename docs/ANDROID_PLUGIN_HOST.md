# Android 插件宿主设计（NPS Android Profile）

> **状态**：Accepted design · 基础实现已交付（store/校验/宿主接入；真机矩阵与管理 UI 待补）  
> **日期**：2026-11-15  
> **决策**：Android 支持动态插件，但只作为 **extended / sideload / 企业分发** 的可选发行形态；默认与 Play 发行仍使用 embedded 插件。动态形态关闭 AOT/Trim、只加载已签名的托管 DLL、存放在 app 私有目录、安装后重启生效。  
> **关联**：[Natter Plugin Specification v1](PLUGIN_SPEC.md)、[NLMP](NLMP.md)、[发布指南](PUBLISHING.md)

---

## 0. 决策摘要

Android 采用**双发行形态**：

| 形态 | 插件来源 | AOT / Trim | 默认 | 用途 |
| --- | --- | --- | --- | --- |
| `embedded` / `play` | 编译进 APK 的 NPS 插件 | 允许开启 AOT/Trim | 是 | 稳定发行、应用商店 |
| `dynamic` / `extended` | app 私有目录中的签名插件包 | AOT off、Trim off | 否 | 企业、自托管、社区分发、实验插件 |

明确决策：

1. **动态加载技术上成立**：关闭 AOT 后，Android 上的 Mono/JIT 可以加载 app 私有目录中的托管程序集；团队已做过实验验证。
2. **动态加载不是默认路径**：默认/Play 形态继续使用 embedded NPS 插件，保留 AOT/Trim 的启动与体积收益。
3. **动态形态使用独立构建配置**：不能在一个 APK 里同时追求“Play + 任意动态插件 + AOT”。
4. **v1 只支持托管 DLL**：不支持动态加载 `.so` / `.dylib` / 原生库。
5. **动态插件必须签名**：安装时校验 manifest、host API、SHA-256 与发布者签名。
6. **只从 app 私有目录加载**：禁止外部存储、下载目录、任意 URL。
7. **安装/更新后重启进程**：不承诺热重载；ALC 回收不作为设计前提。
8. **不把 NPS 权限当安全沙箱**：动态代码拥有 app 进程权限，只安装可信发布者的插件。

---

## 1. 背景与现状

### 1.1 现状（实现前基线）

> 本节记录设计时的基线；当前实现状态与代码映射见 §15。

当前 Android 宿主是编译期嵌入：

- [MainActivity.cs](../src/Natter.Hosting.Android/MainActivity.cs) 使用 `LoadPlugins = false`；
- 通过 `AddEmbeddedPlugin<GreetingsPlugin>()` / `AddEmbeddedPlugin<MetricsPlugin>()` 注册 legacy 插件；
- [Natter.Hosting.Android.csproj](../src/Natter.Hosting.Android/Natter.Hosting.Android.csproj) 通过 `ProjectReference` 把示例插件编进 APK；
- APK 无法像桌面一样扫描 `plugins/` 目录。

当前构建已经是：

```xml
<PublishTrimmed>false</PublishTrimmed>
<RunAOTCompilation>false</RunAOTCompilation>
<AndroidLinkMode>None</AndroidLinkMode>
```

因此动态加载的构建前提已经具备，但宿主仍关闭了插件扫描。

### 1.2 NPS 已有能力

NPS v1 已经具备：

- `AddEmbeddedNpsPlugin(manifest, plugin)`；
- 文件插件扫描、manifest 校验、依赖图、配置、秘密、权限、激活回滚；
- `PluginLoadContext` / `AssemblyDependencyResolver`；
- plugin-local DI 与 contribution registry。

缺少的是 Android 侧的：

- app 私有 plugin store；
- 签名插件包安装器；
- 动态构建配置与发行流程；
- Android 插件管理 UI；
- 重启生效/回滚策略；
- 真机测试矩阵。

### 1.3 为什么单独写这份文档

“能 `Assembly.LoadFrom`”不等于“有插件系统”。  
如果没有提前固定目录、签名、安装、回滚、AOT、安全边界，后面 Discord 插件、插件市场和更新机制会互相冲突。因此本文作为 **Android 插件宿主的决策记录**，后续实现以此为准。

---

## 2. 目标与非目标

### 2.1 目标

- 在 Android 上安装、更新、卸载、启用/禁用 NPS 插件；
- 复用 NPS manifest、Host API、contribution、依赖图、配置、权限；
- 只从 app 私有目录加载；
- 签名与完整性校验；
- 安装失败不破坏现有版本；
- 支持 embedded 与 dynamic 两种发行形态；
- 插件代码保持 platform-neutral（`net10.0` 托管库）；
- 可诊断：版本、状态、权限、失败原因可查；
- 不修改主 app 的默认稳定性。

### 2.2 非目标

- iOS；
- 热重载 / HMR；
- 任意第三方不可信代码的进程内沙箱；
- 动态加载原生 `.so`；
- 插件市场/远程商店（v1 只做安装器与本地/签名包）；
- 跨进程插件宿主；
- Android 主机上的 CLI 插件管理 UI（先做应用内管理界面或调试入口）；
- 自动信任 TOFU（首次使用即信任）；
- 把 Play 版本改造成动态插件版本。

---

## 3. 发行形态

### 3.1 `embedded` / `play`

```text
APK
├── Natter.Hosting.Android.dll
├── Natter.Plugin.*.dll        # 编译期嵌入
└── plugins/                   # 不扫描
```

- 插件通过 `AddEmbeddedNpsPlugin(manifest, instance)` 注册；
- 可开启 AOT/Trim（以实际平台策略为准）；
- 插件更新随 APK 更新；
- 安全边界是 APK 签名；
- 默认发行形态。

### 3.2 `dynamic` / `extended`

```text
APK
├── Natter.Hosting.Android.dll
└── (无插件)

FilesDir/plugins/
├── natter.transport.discord/0.1.0/...
└── natter.feature.ai.openai/0.1.0/...
```

- 关闭 AOT/Trim；
- 启动时扫描 app 私有 plugin store；
- 只加载签名托管插件；
- 安装/更新后重启进程；
- 用于 sideload、企业、自托管、开发/社区分发。

### 3.3 构建配置

不要在一个 Configuration 里同时做 AOT 和动态插件。建议：

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Embedded'">
  <!-- 可开启 AOT/Trim，按实际设备验证 -->
  <RunAOTCompilation>true</RunAOTCompilation>
  <PublishTrimmed>true</PublishTrimmed>
  <AndroidLinkMode>SdkOnly</AndroidLinkMode>
</PropertyGroup>

<PropertyGroup Condition="'$(Configuration)' == 'DynamicPlugins'">
  <RunAOTCompilation>false</RunAOTCompilation>
  <AndroidEnableProfiledAot>false</AndroidEnableProfiledAot>
  <PublishTrimmed>false</PublishTrimmed>
  <AndroidLinkMode>None</AndroidLinkMode>
</PropertyGroup>
```

注意：

- `RunAOTCompilation=false` 不等于关闭 Trim；两者必须一起处理。
- 动态构建保留 `PublishTrimmed=false` / `AndroidLinkMode=None`，避免移除插件加载、反射、JSON、NPS 契约所需的类型。
- AOT/Trim 的具体收益与可用性以目标设备和 .NET Android 版本为准。
- 插件包自身在 v1 也建议 `PublishTrimmed=false`，避免插件反射代码被裁掉。
- 两个构建共享代码，但产出不同 APK；动态构建不应作为 Play 默认包。

### 3.4 代价与取舍

- **启动与运行**：AOT off 依赖 JIT，冷启动、首次调用和内存/电量开销都会上升；需要实测基线。
- **体积**：Trim off 保留更多元数据和代码，APK/内存占用可能增加。
- **反射**：插件加载、JSON、NPS 契约依赖反射；动态构建不能随意开启 linker，否则插件入口/类型会被裁掉。
- **安全**：动态代码拥有 app 进程权限，NPS permissions 不是沙箱；只安装可信签名插件。
- **稳定性**：插件未处理异常可能终止整个进程；v1 没有进程隔离。
- **更新**：安装/更新/卸载后重启；ALC collectible 在 Mono/Android 上不能作为保证。
- **平台**：如果走 Google Play，需要核对 Dynamic Code Loading 政策；Play 默认包建议 embedded。
- **后台**：Discord bot 长连接仍需要 foreground service/通知；动态插件不解决 Android 后台限制。
- **iOS**：该方案不适用于 iOS；不要把它当作跨平台动态插件方案。

---

## 4. 插件存储与包格式

### 4.1 存储目录

使用 app 私有目录：

```text
Context.FilesDir/
└── plugins/
    ├── .staging/<txn-id>/
    ├── .trash/<txn-id>/
    ├── installed.json
    ├── pending.json
    └── natter.transport.discord/
        ├── 0.1.0/
        │   ├── plugin.json
        │   ├── lib/
        │   │   ├── Natter.Plugin.Discord.dll
        │   │   └── ...私有依赖
        │   └── package.sig
        └── 0.2.0/
            └── ...
```

规则：

- 只允许 `Context.FilesDir` 下的路径；禁止外部存储、`Download`、`/sdcard`。
- 插件目录按 `pluginId/version` 分层；版本目录不可变。
- `installed.json` 记录已安装版本、启用状态、活动版本、安装时间；写入必须原子替换。
- `pending.json` 记录待激活更新，用于启动时选择候选版本。
- `.staging` 用于解压/校验；失败直接删除。
- `.trash` 用于卸载延迟删除；重启后清理。
- 插件配置和插件数据与代码分开：
  - 配置：`FilesDir/plugin-config/<pluginId>.json`；
  - 数据：`FilesDir/plugin-data/<pluginId>/`；
  - 秘密：Android Keystore / Android secret provider，不写入插件目录。

### 4.2 包格式

建议扩展名 `.natter-plugin.zip`：

```text
plugin.json
lib/
  Natter.Plugin.Discord.dll
  ...
package.sha256
package.sig
```

`plugin.json` 是 NPS v1 manifest，字段见 [PLUGIN_SPEC §4](PLUGIN_SPEC.md#4-manifest)。

`package.sha256`：

```text
<sha256>  plugin.json
<sha256>  lib/Natter.Plugin.Discord.dll
...
```

`package.sig`：

- 对规范化后的 `package.sha256` + `plugin.id` + `plugin.version` 做签名；
- 算法建议 ECDSA P-256 + SHA-256（.NET BCL 可用）；如后续选 Ed25519，需要额外库支持；
- 签名文件是 base64 或原始二进制，不参与自身哈希；
- 应用内固定发布者公钥，不采用“首次使用即信任”。

### 4.3 包校验

安装器必须校验：

1. 包大小、文件数量、压缩比上限（防 zip bomb）；
2. 路径安全：禁止绝对路径、`..`、符号链接、重复路径；
3. `plugin.json` 存在、schemaVersion=1、id/version/hostApi 合法；
4. `entry.assembly` 位于包内、路径不逃逸、文件存在；
5. 所有运行时 DLL 都在 `package.sha256` 中；
6. SHA-256 匹配；
7. 发布者签名匹配内置公钥；
8. `hostApi` 与当前宿主兼容；
9. `platforms` 包含 `android`；
10. v1 禁止原生库：`.so`、`.dylib`、`.a`、`.dll` 中的原生混合模式（检测 PE CLI header；无法确认时拒绝）；
11. 依赖插件已在 store 中或为可选；
12. 配置 schema 存在且可解析；
13. 磁盘空间足够；
14. 版本目录不存在，或更新流程允许覆盖。

任何一步失败：删除 staging，不修改 `installed.json`。

---

## 5. 安装、更新、卸载

### 5.1 安装

```text
下载/选择 zip
  → 写入 .staging/<txn>/
  → 解压到 .staging/<txn>/extract/
  → 校验包/签名/manifest/hostApi/依赖
  → 移动到 plugins/<id>/<version>/
  → 原子写 installed.json
  → 标记 pending activation
  → 提示重启
```

v1 不在安装后立即加载；必须重启应用进程。

### 5.2 更新

- 新版本放入新版本目录；
- `installed.json` 记录 `activeVersion` 与 `previousVersion`；
- 重启后优先尝试新版本；
- 如果新版本激活失败：
  - 记录失败；
  - 尝试回退到 `previousVersion`（best-effort）；
  - 用户可手动回退；
- 如果新版本导致进程崩溃，v1 不保证自动回滚；`pending.json` 至少保留候选与上一版本信息，下一次启动允许用户选择回退。
- 旧版本在确认新版本稳定前不删除；保留至少一个上一版本。
- 不做后台静默更新；更新需要用户确认。

### 5.3 卸载

- 先标记禁用，重启；
- 删除版本目录；
- 可选择保留 `plugin-data`；
- 配置与秘密按用户选择清理；
- 删除操作失败进入 `.trash`，下次启动重试。

### 5.4 启用/禁用

- 写入 `installed.json` 的 enabled 状态；
- 重启生效；
- 禁用被依赖插件时，依赖插件标记 `Blocked`；
- v1 不做热启用/禁用。

---

## 6. 运行时加载

### 6.1 接入点

Android 宿主启动时：

```csharp
var pluginRoot = Path.Combine(FilesDir!.AbsolutePath, "plugins");
Directory.CreateDirectory(pluginRoot);

var options = new NatterCliOptions
{
    LoadPlugins = true,
};

options.PluginDirectories.Add(pluginRoot);

await NatterCli.RunAsync(args, console, options, configure);
```

`NatterCli` 组合根调用 NPS 文件扫描；插件 manifest、依赖、权限、配置、ALC 与桌面路径一致。

注意：

- 当前默认构建已迁移为 embedded NPS：`AndroidPluginHost.Configure` 调用 `AddEmbeddedNpsPlugin`，不再使用 legacy `IPluginModule` 示例。
- `DynamicPlugins` 构建已接入 `PluginStore.GetActivePluginDirectories()` 与启动 reconcile；默认/Play 仍不扫描外部目录。
- `NatterCliOptions.AddDefaultPluginLocations` 可能追加 `AppContext.BaseDirectory/plugins`；Android 上该目录可能不可写，应优先使用显式 `FilesDir` 路径。
- 插件目录不存在时创建；只扫描 app 私有目录。

### 6.2 加载细节

- 每个插件一个 `PluginLoadContext`（如果 Mono 上 collectible ALC 行为不符合预期，至少按插件隔离加载）；
- `Natter.*`、`Microsoft.Extensions.*`、`System.*` 等共享程序集回落到默认上下文；
- 插件私有 DLL 从插件目录解析；
- 插件入口实现 `INatterPlugin`（或 legacy `IPluginModule`）；
- 插件本地 DI provider；
- 宿主统一调用 `chat.upstream` 的 `ConnectAsync` / `DisconnectAsync`；
- 插件失败标记 `Failed`，不阻塞无关插件；
- 插件未处理异常可能终止进程；v1 没有进程内沙箱。

### 6.3 进程与后台

- 动态插件依赖 app 进程存活；Discord bot 需要 foreground service / 通知；
- Android Doze、后台限制、进程回收会影响长连接；
- 应用重启后从 `FilesDir/plugins` 重新加载；
- 插件 store 不放在 `CacheDir`，避免被系统清理；
- 需要处理应用升级、Android 升级、存储空间不足、进程被杀。

### 6.4 不承诺热重载

- 安装/更新/卸载后重启；
- `AssemblyLoadContext` 的 collectible 行为在 Mono/Android 上必须实测，不作为设计保证；
- 不做运行中插件替换；
- 不把 HMR 作为 v1 验收项。

---

## 7. 安全模型

### 7.1 信任链

- 只安装来自可信发布者的签名插件；
- 应用内置一个或多个发布者公钥；
- 公钥轮换通过 app 更新完成；
- 禁止“首次使用即信任”；
- 调试构建可允许本地未签名插件，但必须在 UI 明确标识，且不能用于生产。

### 7.2 动态代码不是沙箱

- 动态插件拥有 app 进程权限；
- 可读取 app 私有数据、访问网络、调用 Android API；
- NPS `permissions` 是安装审批与可观测性，不是强制执行边界；
- 真正隔离需要独立进程、容器或 OS 级沙箱，属于后续版本；
- 不安装来源不明的 `.natter-plugin.zip`。

### 7.3 秘密

- Discord token、AI key 等通过 Android Keystore/Android SecretProvider 注入；
- 插件的 `config.json` 只允许 `${secret:NAME}` 引用；
- 插件目录、日志、`plugins doctor`、异常消息必须脱敏；
- 秘密不进入插件包、不进入 Git、不进入 AI prompt。

### 7.4 包安全

- 只允许 HTTPS 下载；
- 下载大小上限、超时、重定向限制；
- 解压路径检查、符号链接拒绝、zip bomb 限制；
- 安装前校验签名；
- 插件包可被解压查看，不依赖“加密包”；
- v1 禁止原生库，避免 ABI/执行权限/代码签名复杂度；
- 如果后续支持 `.so`，必须单独设计 ABI、签名、加载与 Play 政策。

### 7.5 Play 发行

如果通过 Google Play 分发，需要核对当期 **Dynamic Code Loading** 政策与目标 Android 版本限制。  
本文的动态插件形态默认面向 sideload / 企业 / 自托管；Play 形态建议只使用 embedded 插件。  
技术实验验证不等于上架合规验证。

---

## 8. 权限与配置

- NPS manifest 的 `permissions` 在安装时展示，用户确认后授予；
- `trust = signed` 的插件默认不自动授予高权限；
- 插件配置使用 JSON Schema；
- 配置存放在 `plugin-config/<id>.json`；
- 插件数据存放在 `plugin-data/<id>/`，需要配额与清理策略；
- 插件 `configSchema` 无效时拒绝安装/激活；
- 秘密通过 Android provider 解析；
- 不把动态插件的权限声明当作安全隔离。

Android 插件管理界面至少展示：

- 插件 id/name/version；
- 发布者与签名状态；
- 请求的权限；
- 状态（installed/enabled/disabled/failed/pending-restart）；
- 更新/回退/卸载；
- 需要重启的提示；
- 失败原因与日志导出。

---

## 9. 插件管理 UX

Android 没有桌面终端，不能只依赖 CLI：

- 应用内“插件”页面：安装、启用/禁用、更新、回退、卸载；
- “从文件安装”：只允许选择 `.natter-plugin.zip`，仍执行签名/路径校验；
- “开发者模式”：允许本地未签名插件，显示明显警告；
- 安装后提示“需要重启应用”；
- 失败时保留旧版本，提供回退按钮；
- 不允许静默安装/静默更新；
- 记录安装日志，支持导出诊断。

v1 可以先实现：

- 从 app 私有目录/调试入口安装；
- 插件列表与状态；
- 启用/禁用、卸载、回退；
- 安装日志。

商店/远程目录后期再做。

---

## 10. 测试矩阵

### 10.1 构建

- `DynamicPlugins`：AOT off、Trim off，arm64 真机 + x64 模拟器；
- `Embedded`：AOT/Trim 按目标配置，验证 embedded NPS 插件；
- 两个构建共享代码，插件行为一致；
- 启动时间、内存、电量基线对比；
- 应用升级后 plugin store 保持可用。

### 10.2 加载

- 从 `FilesDir/plugins` 加载签名插件；
- 插件本地 DI、contribution、配置、秘密、权限；
- `chat.upstream` Connect/Disconnect 由宿主驱动；
- 依赖图/可选依赖/Blocked；
- 失败插件不阻塞其他插件；
- 进程重启后重新加载。

### 10.3 安全/包

- 签名错误、hash 错误、未签名插件拒绝；
- zip slip、符号链接、重复路径、zip bomb 拒绝；
- 原生 `.so` 拒绝；
- hostApi 不兼容拒绝；
- 依赖缺失拒绝/Blocked；
- 配置 schema 错误、secret 缺失；
- 低磁盘空间；
- 安装中断/进程被杀后的清理；
- 更新失败回退；
- 卸载/重装。

### 10.4 Android 系统

- 应用升级、Android 升级；
- 前台服务/后台限制；
- 进程被系统回收；
- 网络断开/恢复；
- 多 ABI 设备；
- Play 构建不含动态插件安装入口。

---

## 11. 迁移步骤

1. **Embedded NPS 迁移**
   - Android 默认构建从 legacy `AddEmbeddedPlugin` 迁到 `AddEmbeddedNpsPlugin`；
   - 使用 `GreetingsNpsPlugin` / `MetricsNpsPlugin`；
   - 保持 AOT/Trim 策略。
2. **Dynamic 构建骨架**
   - 增加 `DynamicPlugins` Configuration；
   - 关闭 AOT/Trim；
   - 保持 APK 签名。
3. **Plugin Store**
   - 实现 `AndroidPluginStore`（安装/校验/回退/卸载）；
   - 实现 `AndroidSecretProvider`；
   - 配置 `FilesDir/plugins` 为 NPS 插件目录。
4. **宿主接入**
   - `LoadPlugins=true` + `PluginDirectories`；
   - 启动时扫描；
   - 插件管理 UI/调试入口。
5. **签名与发布**
   - 生成插件签名密钥；
   - 应用内置公钥；
   - 打包示例 NPS 插件为 `.natter-plugin.zip`；
   - 安装/更新/回退测试。
6. **默认发行保持 embedded**
   - Play/默认包不暴露动态安装；
   - 动态能力只在 extended 构建启用。

---

## 12. 验收标准

- [ ] Dynamic APK 从 `FilesDir/plugins` 加载签名 NPS 插件；
- [ ] 插件 manifest/hostApi/依赖/配置/秘密/权限生效；
- [ ] 未签名、签名错误、hash 错误、原生库、zip slip 被拒绝；
- [ ] 安装/更新/卸载后重启生效；
- [ ] 新版本激活失败可回退，旧版本保留；
- [ ] 插件失败不导致宿主启动失败（无关插件继续）；
- [ ] 插件不能访问宿主根 DI；
- [ ] AOT/Trim 关闭的构建可稳定运行；
- [ ] Embedded/Play 构建不受影响；
- [ ] 应用升级、进程重启后 store 可用；
- [ ] 文档、诊断日志、权限展示完整；
- [ ] 通过真机 arm64 + 模拟器 x64 测试。

---

## 13. 决策记录

### 13.1 已接受

- Android 动态插件作为 **extended** 能力；
- 动态构建关闭 AOT/Trim；
- v1 只支持托管 DLL；
- app 私有目录；
- 签名 + SHA-256；
- 安装/更新后重启；
- 默认/Play 使用 embedded NPS。

### 13.2 被否决的方案

| 方案 | 否决原因 |
| --- | --- |
| 动态插件 + AOT | 动态程序集无法预编译；JIT/解释器与裁剪行为复杂，不作为第一版 |
| 任意 URL/外部存储直接加载 | 无签名/路径控制，安全风险不可控 |
| v1 动态加载 `.so` | ABI、加载、签名、Play 政策复杂 |
| Play 默认包开放动态安装 | 平台政策与审核风险；默认包应稳定 |
| 依赖 ALC 回收做热更新 | Mono/Android 行为不可靠；重启更可预测 |
| 把 permission 当沙箱 | 进程内代码可绕过；必须明确信任边界 |

### 13.3 重新评估条件

- Google Play 政策明确允许/限制该形态；
- .NET Android 对 AOT + 动态程序集提供稳定支持；
- 需要真正隔离第三方插件时，启动 out-of-process 插件宿主；
- 插件市场/签名基础设施成熟；
- 原生库需求出现，且 ABI/签名方案确定。

---

## 14. 开放问题

- 插件签名公钥的分发与轮换；
- 插件市场/官方 registry；
- 更新失败跨进程崩溃的自动回滚；
- 插件数据配额与清理；
- 插件管理 UI 的具体交互；
- Android 前台服务与 bot 长连接；
- Play 政策最终口径；
- AOT + JIT 混合加载的可行性；
- 原生插件（`.so`）支持；
- out-of-process 插件宿主；
- 插件包是否支持增量更新。

---

## 15. 本仓库实现（基础）

### 15.1 代码映射

| 设计章节 | 实现 |
| --- | --- |
| §4 存储与包格式 | `src/Natter.Plugin.Store/PluginStore.cs`、`PluginPackageValidator.cs`、`PluginPackageWriter.cs` |
| §4.2 哈希/签名 | `package.sha256` 规范化 SHA-256；ECDSA P-256 + SHA-256 签名（`PluginPackageSignatureVerifier`） |
| §4.3 包校验 | manifest/hostApi/platform/依赖、zip slip/symlink/重复路径、大小与压缩比、原生 DLL 检测、配置 schema |
| §5 安装/更新/卸载 | `PluginStore.InstallAsync` / `RollbackAsync` / `UninstallAsync` / `SetEnabledAsync` / `CleanupAsync` |
| §5.2 更新与回退 | `installed.json` 保留 `previousVersion`；`ReconcileAsync` 在启动后根据 `IPluginRuntimeDiagnostics` 确认或回退 |
| §6 运行时加载 | `AndroidPluginHost`、`MainActivity`：dynamic 构建把 `FilesDir/plugins/<id>/<version>` 传给 NPS loader |
| §7.3 秘密 | `AndroidKeystoreSecretProvider`：AndroidKeyStore AES-GCM + app-private SharedPreferences |
| §8 配置 | NPS `CompositionOptions.PluginConfigDirectory` 读取 `FilesDir/plugin-config/<id>.json` |
| §9 管理入口 | `AndroidPluginHost.InstallInboxAsync`（`FilesDir/plugin-inbox/*.natter-plugin.zip` debug 安装）+ CLI `plugins store ...` |
| §3.3 构建配置 | `Natter.Hosting.Android.csproj` 的 `DynamicPlugins` Configuration（AOT off / Trim off） |
| 测试 | `tests/Natter.Framework.Tests/PluginStoreTests.cs`、`Natter.Framework.Tests` 现有 NPS 测试 |

### 15.2 使用方式

```bash
# 桌面/调试：创建并安装签名包（CLI 的 root 指向 app 私有目录或测试目录）
natter plugins store install ./natter.sample.greetings-0.1.0.natter-plugin.zip \
  --root /data/user/0/dev.natter.hosting.android/files/plugins \
  --trusted-key <base64-SubjectPublicKeyInfo>
natter plugins store list --root <root>
natter plugins store disable natter.sample.greetings --root <root>
natter plugins store rollback natter.sample.greetings --root <root>
```

Android dynamic 构建：

```bash
dotnet build src/Natter.Hosting.Android/Natter.Hosting.Android.csproj -c DynamicPlugins
```

- 启动时扫描 `FilesDir/plugins` 的 active 版本；
- `FilesDir/plugin-inbox/*.natter-plugin.zip` 是调试/侧载安装入口；
- 安装后重启应用生效；
- 宿主启动回调调用 `PluginStore.ReconcileAsync`，失败时回退到 `previousVersion`。

### 15.3 已覆盖与仍待补

已覆盖：

- 签名/哈希/zip 安全/原生库拒绝/依赖与配置校验；
- app 私有版本目录、installed/pending 状态、更新保留旧版本、回退、启用/禁用、卸载、清理；
- dynamic/embedded 双构建、embedded NPS 迁移、AndroidKeystore secret provider；
- NPS loader 外部配置目录、宿主启动 reconcile 回调；
- 平台无关 store 可由桌面测试直接验证。

仍待补（不阻塞默认 embedded 发行）：

- Android 文件选择器与完整插件管理 UI 页面（当前是 debug inbox + CLI/输出）；
- 真机 arm64 + 模拟器 x64 的启动时间/内存/后台/进程回收矩阵；
- 应用升级、Android 升级、低磁盘、安装中断的真实系统故障注入；
- 内置发布者公钥的生产配置与轮换流程（当前支持 `FilesDir/plugin-trusted-keys.txt` 或后续嵌入资源；DEBUG 允许 unsigned）；
- 跨进程崩溃自动回退（当前是下一次启动 reconcile，不保证崩溃前自动恢复）；
- 远程 HTTPS 下载器/插件市场（安装器只接收本地包）。

## 16. 一句话结论

**技术上可以动态加载，但把它做成可交付能力的关键不是 `Assembly.LoadFrom`，而是：独立 dynamic 构建、app 私有存储、签名包、严格安装校验、NPS 文件加载、重启生效、失败回退和明确的信任边界。**

默认继续 embedded，extended 才开 dynamic。  
这两条路共享 NPS 契约，不要在一个 APK 里混在一起。
