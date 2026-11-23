# 发布指南

## 1. 前置条件

| 场景 | 要求 |
| --- | --- |
| 桌面 CLI | .NET SDK 10.0.1xx；目标 RID 的运行时包（首次发布需联网还原） |
| Android | .NET SDK 10 + `android` workload、Android SDK（API 36）、JDK 17+ |
| 单文件发布 | 无需额外工具；脚本使用自包含 + 单文件 + 压缩 |

检查环境：

```bash
dotnet --info
dotnet workload list
```

安装 Android workload：

```bash
dotnet workload install android
```

## 2. 桌面 CLI

### 2.1 一键发布

```bash
./scripts/publish.sh linux-x64
./scripts/publish.sh win-x64
./scripts/publish.sh osx-arm64
./scripts/publish.sh linux-x64 win-x64 osx-arm64   # 或多个 RID
```

默认发布全部 RID：

```bash
./scripts/publish.sh
# linux-x64 linux-arm64 osx-x64 osx-arm64 win-x64 win-arm64
```

输出：

```text
artifacts/cli/<rid>/
├── natter            (Windows 为 natter.exe)
└── plugins/
    ├── greetings/{plugin.json, Natter.Sample.Plugin.Greetings.dll}
    └── metrics/{plugin.json, Natter.Sample.Plugin.Metrics.dll}
```

### 2.2 等价的 MSBuild 命令

```bash
dotnet publish src/Natter.Cli/Natter.Cli.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=embedded \
  -o artifacts/cli/linux-x64
```

Windows PowerShell：

```powershell
./scripts/publish.ps1 -RuntimeIdentifiers win-x64,linux-x64,osx-arm64
```

### 2.3 框架依赖发布

如果目标机器已安装 .NET 10 Runtime，可去掉 `--self-contained`：

```bash
dotnet publish src/Natter.Cli/Natter.Cli.csproj \
  -c Release -r linux-x64 --self-contained false \
  -p:PublishSingleFile=true -o artifacts/cli/linux-x64-fd
```

### 2.4 已验证的 RID

| RID | 产物 | 说明 |
| --- | --- | --- |
| `linux-x64` | ELF 64-bit `natter`（约 40 MB） | 已在本机运行验证 |
| `win-x64` | PE32+ `natter.exe`（约 38 MB） | 交叉发布验证 |
| `osx-arm64` | Mach-O arm64 `natter` | 交叉发布验证 |
| `android-arm64` / `android-x64` | APK | 已构建并签名（调试证书） |

`linux-arm64`、`win-arm64`、`osx-x64` 使用相同脚本发布；未在对应硬件上运行验证。

### 2.5 插件随发布部署

`Natter.Cli.csproj` 中的 `CopySamplePluginsToPublish` 会把示例插件复制到发布目录的 `plugins/` 子目录。CLI 启动时自动扫描：

1. `--plugin-dir <path>`；
2. 环境变量 `NATTER_PLUGIN_DIR`；
3. 可执行文件同级的 `plugins/` 目录。

单文件发布只打包**框架程序集**，插件 DLL 与 `plugin.json` 保持外置，因此可以独立升级插件而无需重新发布宿主。

## 3. Android

### 3.1 构建

```bash
./scripts/publish-android.sh
# 或
dotnet publish src/Natter.Hosting.Android/Natter.Hosting.Android.csproj \
  -c Release -f net10.0-android \
  -p:AndroidPackageFormat=apk \
  -o artifacts/android
```

输出：

```text
artifacts/android/
├── dev.natter.hosting.android.apk
├── dev.natter.hosting.android-Signed.apk
└── ...
```

安装到设备/模拟器：

```bash
adb install -r artifacts/android/dev.natter.hosting.android-Signed.apk
adb shell am start -n dev.natter.hosting.android/crc642e548ea74e563731.MainActivity
```

> Activity 名由构建生成，实际以 `aapt dump badging` 的 `launchable-activity` 为准。

### 3.2 签名

脚手架默认使用 .NET Android 的调试证书（`CN=Android Debug`），仅适合内部验证。生产发布请配置：

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Release'">
  <AndroidKeyStore>true</AndroidKeyStore>
  <AndroidSigningKeyStore>natter.keystore</AndroidSigningKeyStore>
  <AndroidSigningKeyAlias>natter</AndroidSigningKeyAlias>
  <AndroidSigningKeyPass>env:ANDROID_KEY_PASS</AndroidSigningKeyPass>
  <AndroidSigningStorePass>env:ANDROID_STORE_PASS</AndroidSigningStorePass>
</PropertyGroup>
```

生成 keystore：

```bash
keytool -genkeypair -v \
  -keystore natter.keystore \
  -alias natter \
  -keyalg RSA -keysize 2048 -validity 10000
```

### 3.3 AOT / 裁剪

当前 Android 工程为了保留反射与嵌入插件的类型完整性：

```xml
<PublishTrimmed>false</PublishTrimmed>
<RunAOTCompilation>false</RunAOTCompilation>
<AndroidLinkMode>None</AndroidLinkMode>
```

需要更小体积/更快启动时：

1. 设置 `PublishTrimmed=true`；
2. 设置 `RunAOTCompilation=true`（必须启用裁剪）；
3. 使用 `AndroidLinkMode=SdkOnly`；
4. 为反射使用的插件类型补充 trimmer 注解/描述文件，并在真机回归。

### 3.4 Android 插件模式

APK 是打包产物，默认不能扫描外部 DLL 目录。Android 有两种发行形态：

**embedded / play（默认）**

```csharp
builder => builder
    .AddEmbeddedNpsPlugin(GreetingsNpsPlugin.Manifest, new GreetingsNpsPlugin())
    .AddEmbeddedNpsPlugin(MetricsNpsPlugin.Manifest, new MetricsNpsPlugin())
```

- `Natter.Hosting.Android.csproj` 默认引用示例插件并编译进 APK；
- 插件通过 NPS manifest + `INatterPlugin` 注册；
- 可开启 AOT/Trim（需真机验证）。

**dynamic / extended**

```bash
dotnet build src/Natter.Hosting.Android/Natter.Hosting.Android.csproj -c DynamicPlugins
```

- `DynamicPlugins` Configuration 关闭 AOT/Trim，并排除 embedded 示例插件 ProjectReference；
- 宿主扫描 app 私有 `FilesDir/plugins`，通过 `Natter.Plugin.Store` 安装/回退签名包；
- 调试/侧载安装入口：`FilesDir/plugin-inbox/*.natter-plugin.zip`；
- 安装/更新后重启应用生效；v1 只支持托管 DLL。

签名安装示例：

```bash
natter plugins store install ./plugin.natter-plugin.zip \
  --root /data/user/0/dev.natter.hosting.android/files/plugins \
  --trusted-key <base64-SubjectPublicKeyInfo>
```

动态形态的 app 私有 store、哈希/签名、AOT off、回滚与管理入口见 [Android 插件宿主设计](ANDROID_PLUGIN_HOST.md)。默认/Play 发行继续使用 embedded。

### 3.5 Discord transport 插件

`natter.transport.messaging.discord` 是 NPS transport 插件，不包含 AI 逻辑。构建：

```bash
dotnet build samples/Natter.Sample.Plugin.Discord/Natter.Sample.Plugin.Discord.csproj -c Release
```

插件目录需要：

```text
plugin.json
config.schema.json
config.json                 # 不含 token 明文
lib/Natter.Sample.Plugin.Discord.dll
lib/Discord.Net.*.dll
lib/Natter.Chat.Discord.dll
```

发布前：

1. 将 bot token 放进宿主的 secret provider（桌面可用环境变量 `DISCORD_BOT_TOKEN`，Android 用 AndroidKeyStore provider）；
2. `config.json` 只写 `"token": "${secret:DISCORD_BOT_TOKEN}"`；
3. 设置 `selfId` 为 bot 的 numeric user id；
4. 只声明需要的 intents；`MessageContent` 是 privileged intent，需在 Discord Developer Portal 开启；
5. 不把真实 token、config.json 或 `.env` 打进插件包；
6. 使用 fake transport CI 测试；真实网关验证单独在测试 guild 进行。

详细设计见 [Discord Transport Plugin Design](DISCORD_PLUGIN_DESIGN.md)。

### 3.6 DeepSeek AI responder 插件

`natter.feature.ai.deepseek` 是 NPS feature 插件，使用 Microsoft.Extensions.AI 的 `IChatClient`，不依赖 Discord.Net。

```bash
dotnet build src/Natter.Feature.Ai.DeepSeek/Natter.Feature.Ai.DeepSeek.csproj -c Release
```

插件目录：

```text
plugin.json
config.schema.json
config.json                 # 不含 key 明文
lib/Natter.Feature.Ai.DeepSeek.dll
lib/Microsoft.Extensions.AI*.dll
lib/Natter.Chat.dll / Natter.Application.dll
```

发布前：

1. `DEEPSEEK_API_KEY` 只放在宿主的 secret provider；config 只写 `"apiKey": "${secret:DEEPSEEK_API_KEY}"`；
2. v1 保持 `"thinking": "disabled"`、`"streaming": false`、`"tools": []`；
3. 限制 `maxOutputTokens`、`historyMaxMessages`、`historyMaxCharacters` 和 `timeoutSeconds`；
4. 不把真实 key、config.json、prompt/response 日志打进包；
5. 先跑 fake `IChatClient`/fake HTTP 测试，再用测试 key 做真实 API + fake Discord；
6. 真实 Discord + 真实 DeepSeek 只在测试 guild/限额下执行。

CI 不调用真实 DeepSeek。详细设计见 [AI Provider Design](AI_PROVIDER_DESIGN.md)。

### 3.7 钉钉 / 星火验证插件

**钉钉 transport**（`natter.transport.messaging.dingtalk`，Stream 模式）

- 配置 `clientId`、`clientSecret` 为 `${secret:DINGTALK_CLIENT_SECRET}`、`botId`、`robotCode`；
- Stream 模式由客户端主动建立 WebSocket，不需要公网 callback URL；
- `DingTalkStreamTransport` 调 gateway open 接口获取 endpoint/ticket，订阅 `/v1.0/im/bot/messages/get`，收到消息后 ack，断线退避重连；
- 回复使用消息中的 `sessionWebhook`，有有效期；过期后等待下一条入站消息；
- 当前只支持 text 入站/出站，不支持 edit/delete/typing/slash/附件；
- HTTP callback transport 保留为 legacy，不作为默认插件 transport。

**星火 provider**（`natter.feature.ai.spark`）

- 配置 `apiKey` 为 `${secret:SPARK_API_KEY}`，默认 MaaS endpoint `https://maas-api.cn-huabei-1.xf-yun.com/v2/chat/completions`；
- 默认模型 `spark-x2.5-1.7b`；模型名按控制台/文档选择，不硬编码旧模型；
- 请求使用 `max_completion_tokens` 和 `thinking.type`，v1 默认 `thinking=disabled`；不发送 `temperature`；
- v1 非流式、无 tools；Spark 错误码与 HTTP 错误经 `SparkException` 分类，不泄漏 APIPassword；
- 与 DeepSeek 共用 `Natter.Feature.Ai` 的 `AiChatResponder`、context assembler 与 conversation store。

真实钉钉/星火凭据不进入仓库；当前契约测试使用 fake transport/IChatClient 和本机 HttpListener，真实平台 live 未执行。

### 3.8 Runtime profile / 状态目录

- `--config-dir` 指向 `profiles.json` 与插件 config；默认 `<app base>/config`；
- `--state-dir` 指向 SQLite（`natter.db`）；不提交 state 目录；
- `--profile` 覆盖默认 profile；`NATTER_CONFIG_DIR` / `NATTER_STATE_DIR` / `NATTER_PROFILE` 等价；
- 显式记忆使用 `natter remember` / `forget` / `memory list`；只有配置 `--state-dir` 才跨重启持久化；
- `natter serve` 长驻；`natter health --json` 输出 upstream/provider/plugin/会话快照；
- Tier 1 profile provider 缺失时启动失败；Tier 2 缺失只禁用该 profile。

## 4. 发布检查清单

- [ ] `dotnet test tests/Natter.Framework.Tests` 全绿；
- [ ] 目标 RID 的可执行文件能运行 `version` / `doctor`；
- [ ] `plugins/` 目录结构正确，NPS 插件 `plugins list --detail` 显示 `Activated`，legacy 插件显示 `Loaded`；
- [ ] `ports` 显示插件覆盖（如 `plugin:natter.sample.port.greetings`）；
- [ ] `exec "demo --times 2" "metrics"` 能看到事件被多个订阅者消费；
- [ ] Android 使用正式签名并检查 `targetSdkVersion`；
- [ ] Android dynamic 构建使用 `-c DynamicPlugins`，签名包校验通过，安装后重启能加载 active 版本；
- [ ] plugin store 的 `installed.json` / `pending.json` 不含秘密；
- [ ] Discord 插件 `config.json` 只含 secret reference，token 只在 secret provider；
- [ ] Discord `selfId` 与 token 对应的 bot 一致，intents 已在 Developer Portal 开启；
- [ ] DeepSeek `apiKey` 只含 secret reference，thinking 默认关闭，reasoning_content 不进入回复/日志；
- [ ] AI 插件使用 fake 测试通过，真实 API 验证不在 CI；
- [ ] 钉钉 Stream clientSecret 只从 secret provider 读取，WebSocket 重连/ack 已验证；
- [ ] 星火 APIPassword 只从 secret provider 读取，MaaS model/endpoint/thinking 已验证；
- [ ] `profiles.json` 的 Tier 1 provider 已加载，state 目录可写且不入库；
- [ ] 生产环境替换内存适配器（仓储、UoW、事件日志）并评估事件可靠性。
