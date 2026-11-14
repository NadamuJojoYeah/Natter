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

APK 是打包产物，不能扫描外置 DLL 目录。Android 宿主使用**嵌入插件**：

```csharp
builder => builder
    .AddEmbeddedPlugin<GreetingsPlugin>()
    .AddEmbeddedPlugin<MetricsPlugin>()
```

因此 Android 与桌面共享同一套 `IPluginModule` 契约，但加载方式不同。若需要 Android 动态下发插件，需要自行实现从应用私有目录加载并配合 ALC/裁剪策略。

## 4. 发布检查清单

- [ ] `dotnet test tests/Natter.Framework.Tests` 全绿；
- [ ] 目标 RID 的可执行文件能运行 `version` / `doctor`；
- [ ] `plugins/` 目录结构正确，`plugins list` 显示 `Loaded`；
- [ ] `ports` 显示插件覆盖（如 `plugin:greetings`）；
- [ ] `exec "demo --times 2" "metrics"` 能看到事件被多个订阅者消费；
- [ ] Android 使用正式签名并检查 `targetSdkVersion`；
- [ ] 生产环境替换内存适配器（仓储、UoW、事件日志）并评估事件可靠性。
