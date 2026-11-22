# 04 · WebConsole / PromptLibrary 安全与 .NET 代码审查

- **仓库**：`/home/ndm/Projects/csharp/Natter`（.NET 10 / Natter Framework）
- **审查范围**：`plugins/Natter.Plugin.WebConsole`、`plugins/Natter.Plugin.PromptLibrary`、`tests/Natter.Plugin.WebConsole.Tests`、`tests/Natter.Plugin.PromptLibrary.Tests`、以及 `docs/WEBCONSOLE_PLUGIN_DESIGN.md`、`docs/WEBCONSOLE_PLUGIN.md`、`docs/WEBCONSOLE_HOST_API_GAP.md`、`docs/PROMPT_COMPILER.md`、`docs/HARNESS_RESPONSIBILITY.md`（`bin/obj` 不参与）
- **方式**：只读静态审查 + 3 个可执行探针（`artifacts/review/probe/`，仅新增文件，未修改任何现有源码/配置/测试）
- 行号以审查时的文件内容为准。

---

## 0. 结论摘要

| 严重度 | 数量 | 说明 |
| --- | --- | --- |
| **P0** | 0 | 未发现无需凭据即可读数据/执行命令的入口 |
| **P1** | 1 | `/api/config/{id}/validate` 把宿主 diff 原样回传，viewer（只读角色）可读出未脱敏的配置文档内容（含明文 secret），并使 `allowConfigWrite=false` / `readOnly=true` 部署下的"配置脱敏"承诺失效 |
| **P2** | 5 | 登录全局失败上限不可恢复（登录锁死）；客户端键可由 XFF 伪造（限速/审计）；请求体读取无超时 + 每连接无并发上限（慢速 DoS）；脱敏规则缺 PEM 私钥块且宿主 `IAdminRedactor` 从未接入；未认证请求即可无限写审计并覆盖历史 |
| **P3** | 10 | 登录 CSRF、会话撤销粒度、Cookie Secure 依赖 XFF、undo/审计信息暴露、宿主异常路径回传、body 预算不匹配、有损 UTF-8 解码、external 模式信任任意本地服务、PromptLibrary 配额与 TOCTOU |

安全基线部分做得扎实（常量时间比较、HttpOnly+SameSite=Strict、CSP/安全头、静态资源白名单、配置 id 白名单、命令不落 shell 且宿主二次校验、日志导出转义、审计 0700/0600、prompt 路径策略、composer 保留 id 保护）。缺陷集中在**"校验类端点绕过了为读取路径设计的脱敏"**、**限速/审计等旁路的可伪造性**、以及**传输层缺少超时与并发上限**。

---

## 1. 认证与会话

### WC-01（P1）`/api/config/{id}/validate` 回传宿主 diff，绕过配置回显脱敏

**位置**

- `plugins/Natter.Plugin.WebConsole/WebConsoleRequestHandler.cs:814-836`（`HandleConfigValidateAsync`），关键行 `834`：

```csharp
// WebConsoleRequestHandler.cs:814
if (_admin.Surface.HasConfigStore)
{
    var hostResult = await _admin.ValidateAsync(documentId, content, cancellationToken)...;
    if (hostResult.Ok && hostResult.Value is { } host)
    {
        ...
        return WebConsoleHttpResponse.Json(200, new
        {
            id = documentId,
            validation = new { host.Valid, host.ETag, ..., host.Diff },  // :834 —— diff 原样返回
        });
    }
}
```

- `plugins/Natter.Plugin.WebConsole/WebConsoleAdminGateway.cs:579-582`：`MapDiff` 只做结构映射，没有 `RedactSecrets`：

```csharp
private static WebConsoleAdminDiffView? MapDiff(AdminConfigDiff? diff)
    => diff is null ? null
       : new WebConsoleAdminDiffView(diff.Added, diff.Removed, diff.Changed, diff.Truncated, diff.Lines);
```

- 对照 `WebConsoleAdminGateway.cs:176-177`（读取路径是脱敏的）：

```csharp
var safeContent = WebConsoleRedaction.RedactSecrets(document.Content);
var redacted = !string.Equals(safeContent, document.Content, StringComparison.Ordinal);
```

- diff 的 `-` 行就是磁盘上当前文档的原文：`src/Natter.Application/Admin/AdminConfigDiffBuilder.cs:82,96`（`Emit("-" + left[x])`）。
- 可达性：`WebConsoleAuthorization.Classify` 把 `POST .../validate` 归为 `Read`（`WebConsoleAuthorization.cs:25-45`），因此 **viewer 也能调用**，且不经过 `readOnly` / `allowConfigWrite` 闸门（`ReadOnlyCodesTests.cs:81-103` 与 `RoleEnforcementTests.cs:111-157` 明确"viewer 可以 validate"）。

**影响**

1. viewer 提交 `{"content":"{}"}` 即得到 `-` 前缀的当前文档全文（单次 ≤200 行/32 KiB，可分段推进），包括明文 secret、注释、路径等所有被读取路径刻意遮掉的内容 —— 直接推翻 `docs/WEBCONSOLE_HOST_API_GAP.md:258`「secret 不返回 / 配置回显脱敏」与设计文档 §"先脱敏"的承诺。
2. `allowConfigWrite=false`（默认）与 `readOnly=true`（默认）都拦不住：validate 属 `Read`，是唯一"能拿到宿主文档正文"的写类方法。
3. 由于 diff 基线来自宿主 store（`HostAdminConfigStore.ValidateAsync` 用 `Build(current.Content, content)`，见 `HostAdminConfigStore.cs:141`），与插件侧脱敏规则完全无关，属于结构性绕过而非单点漏判。

**证据（探针，`artifacts/review/probe/Program.cs`）**

```text
read.content                : {\n  "schemaVersion": 1,\n  "apiKey": "[redacted]", ...}
read.contentRedacted        : True
validate.diff.lines         : -{ | -  "schemaVersion": 1, | -  "apiKey": "sk-plaintextsecret1234", | ...
validate.diff leaks apiKey  : True
```

**修复方向**

- 在 `MapDiff` 中对每一行 `diff.Lines` 跑 `WebConsoleRedaction.RedactSecrets`（保持前缀字符不变），或在 `RequestHandler` 返回前统一脱敏。
- 更彻底：把 `-`（被移除的旧行）在 `Read` 权限下替换为计数 + 只回显 `+`（用户自己提交的内容），需要正文时要求 `Manage` 并单独记录审计。
- 补测试：viewer + `/validate` 响应体断言 `DoesNotContain("sk-")`，并对 `contentRedacted` 与 diff 做一致性断言。

**状态**：已用探针复现（网关层），handler 层为逐行读取代码确认。

---

### WC-07（P3）登录端点不做 CSRF / Origin 强绑定（login CSRF）

**位置**：`WebConsoleRequestHandler.cs:141-142`（`/api/session` 在会话解析之前分流）、`305-306`（`POST` → `Login`）、`WebConsoleOriginPolicy.cs` 语义（`WebConsoleAuth.cs:308-320`：缺 `Origin` 头一律放行）。

```csharp
// WebConsoleRequestHandler.cs:141
if (string.Equals(request.Path, "/api/session", StringComparison.Ordinal))
    return HandleSession(request, startedAt);
```

**影响**：跨站表单/fetch 在**没有** `Origin`（或同源导航）时可以驱动登录；`SameSite=Strict` 只保护既有 cookie，不保护"新建会话"。攻击者用自己的 token 把受害者浏览器登进攻击者会话（需要攻击者本身持有一个有效凭据），随后受害者在该会话中的操作被攻击者观察/复用。对双凭据控制台属低危，但对"给同事发只读链接"这类运维习惯有现实意义。

**修复方向**：登录要求 `Origin`/`Sec-Fetch-Site: same-origin` 二者之一成立（缺失即 403）；或登录体加一次性 challenge（GET `/api/session/nonce`）。

**状态**：代码确认。

---

### WC-08（P3）登出只撤销当前会话；无全会话失效；会话仅进程内存

**位置**：`WebConsoleRequestHandler.cs:308-330`（`DELETE /api/session` → `_sessions.Revoke(session.Id)`）、`WebConsoleAuth.cs:31-70`（`Create`/`Revoke`/`Clear`）、`WebConsoleRuntime.cs:215-221`（停止时 `Clear`）。

**影响**：同 token 在另一浏览器/机器上的会话在登出后继续有效（最长 `sessionMinutes`，上限 1440 分钟）；凭据疑似泄漏时无法"一键下线"；进程重启即全部失效（文档已声明的限制，但不是安全特性）。重复登录也不吊销旧会话。

**修复方向**：会话按 `credentialId` 索引，登出可 `revoke-all`；`SessionMinutes` 之外加绝对上限与滑动续期策略；如需跨重启保留，落盘加密并绑定凭据版本。

**状态**：代码确认（属设计取舍，记录以免误判为"登出即安全"）。

---

### WC-09（P3）`RequiresSecureCookie` 未被使用，`Secure` 只依赖 `X-Forwarded-Proto`

**位置**：`WebConsoleAuth.cs:305-306`（唯一一处 `RequiresSecureCookie`，无调用方，仅测试引用）、`WebConsoleRequestHandler.cs:1051-1061`（`IsSecureRequest`）、`WebConsoleAuth.cs:346-352`（cookie 属性）。

```csharp
public bool RequiresSecureCookie
    => _allowedOrigins.Any(origin => origin.StartsWith("https://", ...));   // 从未被读取
```

**影响**：`publicBaseUrl=https://...`（反向代理终结 TLS）的部署里，若代理未被列入 `trustedProxies` 或未设置 `X-Forwarded-Proto`，会话 cookie 会**不带 `Secure`** 下发，可经明文回退泄漏；同时 cookie 无 `__Host-` 前缀，`Max-Age` 最长 24h。当前默认部署（loopback + http）下 `Secure` 无意义，所以定为 P3。

**修复方向**：`Secure` 判定改为 `request.IsHttps || RequiresSecureCookie || X-Forwarded-Proto==https(受信代理)`；cookie 名改 `__Host-natter_webconsole`（Path=/、无 Domain）。

**状态**：代码确认；触发条件依赖部署（标"待确认"部署侧事实）。

---

## 2. 登录限速、审计与可用性

### WC-02（P2）登录"全局失败上限"不可恢复 → 任何人可锁死控制台登录

**位置**：`WebConsoleAuth.cs:196-201`、`219-223`、`235-251`、`254-258`。

```csharp
// WebConsoleAuth.cs:219
if (_global.Count >= MaxFailuresGlobal)        // 50 次/5 分钟，跨所有客户端
{
    retryAfter = RetryAfter(_global, now);
    return true;
}
...
// WebConsoleAuth.cs:254 —— 成功登录只清本客户端，_global 永不清空
public void Reset(string clientKey)
{
    lock (_gate) _failures.Remove(clientKey);
}
```

**影响**：攻击者（或在受害者机器上运行的网页，见 WC-06）每 5 分钟发 50 个错误 token，即可让**正确 token 的 admin 登录也返回 429**，控制台彻底不可登录（只能等窗口过期，且可持续维持）。这让"限速"从防爆破变成拒绝服务原语。

**证据（探针 3）**

```text
blocked after Reset          : True     // 50 次失败（来自 50 个不同 IP）后 Reset("127.0.0.1")
retryAfter                   : 00:04:59.9948608
```

**修复方向**：成功登录同时清理 `_global` 中该时间窗的条目（或改为"全局阈值只做告警/延迟，不做硬拒绝"）；对全局锁死加指数退避 + 告警，避免单点拒绝。

**状态**：已用探针复现。

---

### WC-03（P2）客户端键可伪造：限速按客户端维度失效，审计 IP 可被污染

**位置**：`WebConsoleRequestHandler.cs:1020-1032`（`ResolveClientAddress` 直接取 `X-Forwarded-For` 的**第一个**值）、`WebConsoleOptions.cs:40`（默认 `TrustedProxies = ["127.0.0.1"]`）、`WebConsoleAuth.cs:225-229`（以该键为限速维度）、`WebConsoleRequestHandler.cs:1034-1049`（审计也用它）。

```csharp
var remote = WebConsoleNetwork.NormalizeAddress(request.RemoteAddress) ?? request.RemoteAddress;
if (!WebConsoleNetwork.IsInAnyCidr(remote, _options.TrustedProxies)) return remote;
var forwarded = GetHeader(request, "X-Forwarded-For");
var first = forwarded.Split(',')[0].Trim();     // :1030 最左侧=客户端自填
```

**影响**：默认 `trustedProxies` 含 `127.0.0.1`，因此**同机任何进程/本地服务**转发（或直连 loopback 的脚本）都能自填 `X-Forwarded-For` 轮换"客户端"：5 次/客户端的限速被完全绕过（只剩 WC-02 的全局锁死这一副作用）；审计 `ip` 字段可被写成任意值，取证价值下降。反向代理场景同理（最左值永远不可信）。

**修复方向**：取 `X-Forwarded-For` 的**最右侧非受信**地址；`trustedProxies` 默认置空，或为"直连 loopback"与"经代理"区分两套信任配置；限速键用 `remote endpoint`（不受头影响）+ 次级指纹（如 token 前缀哈希）。

**状态**：代码确认。

---

### WC-04（P2）请求体读取阶段无超时、每连接无并发上限（慢速/连接占满 DoS）

**位置**：`WebConsoleServer.cs:265`（每请求一个新 `Task.Run`，无并发闸门）、`269-294`（15s 超时在 body 读完之后才建立）、`305-329`（`BuildRequestAsync` 用 accept-loop 的取消令牌读 body，仅关闭时才会取消）。

```csharp
// :265
_ = Task.Run(() => HandleAsync(context, cancellationToken), CancellationToken.None);
// :273-277
var request = await BuildRequestAsync(context, cancellationToken)...;   // 无超时
using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
timeout.CancelAfter(TimeSpan.FromSeconds(15));                          // 只覆盖 handler
```

**影响**：客户端声明 `Content-Length` 后缓慢滴流，`BuildRequestAsync` 长时间阻塞在 `InputStream.ReadAsync`，HttpListener 请求队列（默认上限 1000）被占满后控制台对所有请求无响应；同时每个连接一个 work item + `MemoryStream`。绑定 loopback 时仅同机可触发，但 `publicBaseUrl` 反代部署（设计文档支持的形态）可直接来自外网。

**修复方向**：给 body 读取加独立超时（如 5s）与每连接总预算；`SemaphoreSlim` 或 `Channel` 限制在途请求数（超限 503）；`HttpListener` 层开启 `TimeoutManager`/改用 Kestrel（文档 G7 已列为后续）。

**状态**：代码确认。

---

### WC-06（P2）未认证即可无限写审计，1 MiB 轮转会覆盖历史（取证可被第三方网页刷掉）

**位置**：`WebConsoleRequestHandler.cs:119-126`（Origin 校验失败在任何认证之前就写审计）、`1034-1049`、`WebConsoleAudit.cs:74-104`（每次请求同步 append）、`158-166`（轮转 `File.Move(..., ".1", overwrite: true)` 只保留一份历史）。

```csharp
// RequestHandler.cs:122
if (!_origins.IsAllowed(origin, fetchSite))
{
    Audit(request, "origin.rejected", "denied", startedAt);   // 无需凭据、无限速
    return Error(403, "request.origin_denied", "Origin is not allowed.");
}
...
// Audit.cs:164-165
var archived = _path + ".1";
File.Move(_path, archived, overwrite: true);
```

**影响**：任意网站让受害者浏览器向 `http://127.0.0.1:5090/api/...` 发跨站 `fetch`（简单请求无需预检，`Origin` 会带上），即可在数秒内写入约 1 MiB 审计、把 `webconsole-audit.jsonl` 轮转两次，**覆盖掉 `.1` 中的真实历史**；同时 `Write` 在全局锁内做 `AppendAllText + SetUnixFileMode`，未认证流量还能拖慢所有已认证请求的审计路径。

**修复方向**：`origin.rejected` 只做采样/计数（如每 IP 每分钟 1 条），或写独立 `security.jsonl` 且带限速；轮转保留 N 份并按时间命名；审计批量异步写（single-writer channel）。

**状态**：代码确认（未认证写审计路径明确，无需探针）。

---

## 3. 脱敏、机密与配置读写

### WC-05（P2）脱敏规则缺 PEM 私钥块，且宿主的 `IAdminRedactor` 从未接入

**位置**

- `plugins/Natter.Plugin.WebConsole/WebConsoleConfig.cs:58-67`：插件侧只有 `sk-*/ak-*/Bearer` 与键值对两类规则，**没有** `-----BEGIN … PRIVATE KEY-----` 整块规则。
- `plugins/Natter.Plugin.WebConsole/WebConsoleConfig.cs:92-103`（`RedactSecrets`）、`105-112`（`SanitizeText`，审计 target、日志行、错误消息都走这里）。
- 宿主侧有该规则：`src/Natter.Application/Admin/AdminRedactor.cs:24-27`。
- 但宿主脱敏器被解析后从未使用：`WebConsoleAdminSurface.cs:15,35` 注入 `IAdminRedactor? Redactor`，`grep -rn "Redactor" plugins/Natter.Plugin.WebConsole/*.cs` 只有这两处 —— 网关/处理器始终只用 `WebConsoleRedaction`。
- 文档承诺相反：`docs/WEBCONSOLE_HOST_API_GAP.md:22`「输出与配置回显双重脱敏」、`:243`「两者规则一致且有对拍测试」。

**探针证据（probe 0）**

```text
plugin RedactSecrets unchanged : True     // PEM 原样返回
plugin SanitizeText unchanged  : True
host AdminRedactor unchanged   : False    // 宿主会替换
```

**影响**：配置文档里以非敏感键名携带的 PEM（如 `"tls": "-----BEGIN RSA PRIVATE KEY-----…"`）在 `GET /api/config/{id}` 回显中被原样返回（该端点对 viewer 开放）；同样的文本出现在 `audit` target、`host.admin_config.failed` 的错误消息里也不会被遮。命令输出路径由宿主先脱敏（`CliAdminCommandRunner.cs:144-148`）所以受影响较小，但"规则一致"与"双重脱敏"的文档结论不成立。

**修复方向**：把 `AdminRedactor` 的 PEM/多行规则补进 `WebConsoleRedaction`（并把单测扩成规则集对拍，而不是只测 `sk-`）；在 `WebConsoleAdminGateway` 中优先用 `_surface.Redactor ?? local`，让宿主规则成为唯一真源。

**状态**：已用探针复现。

---

### WC-10（P3）`/undo` 与 `/audit` 向 viewer 泄漏宿主路径与他人 IP

**位置**：`WebConsoleRequestHandler.cs:624-635`（`HandleConfigUndoList`，GET → `Read`）、`WebConsoleAdminGateway.cs:361-380`（`WebConsoleUndoSummary.BackupPath` 为宿主绝对路径）、`WebConsoleRequestHandler.cs:209-215`（`/api/audit` 原样返回条目）、`WebConsoleAudit.cs:6-13`（条目含 `Ip`）。

**影响**：只读角色可获得 `/var/lib/natter/config/*.backup` 之类绝对路径（便于后续定向攻击/社工）以及其它管理客户端 IP。属信息暴露，非提权。

**修复方向**：`BackupPath` 只在 `Manage` 下返回（viewer 得到 `backup: available`），`/api/audit` 对 viewer 去掉 `ip`/截断 target，或整体要求 `Manage`。

**状态**：代码确认。

---

### WC-11（P3）宿主异常消息（含文件路径）回传给浏览器

**位置**：`WebConsoleAdminGateway.cs:584-590`（`Describe(exception)` 拼接 `exception.Message`，仅 300 字符截断）、`WebConsoleRequestHandler.cs:613-618`（`ReadAsync` 失败 → `Failure` 原样返回，viewer 可达）。

**影响**：`UnauthorizedAccessException`/`IOException` 的消息通常带绝对路径与系统错误码，等于把宿主文件布局暴露给只读角色。与 WC-01 叠加时价值不大，但属"错误信息泄漏"清单项。

**修复方向**：对外只回 `host.admin_config.failed` + 稳定子码；原始消息只进审计/日志。

**状态**：代码确认。

---

### WC-12（P3）传输层 body 预算与内容预算不匹配（转义密集文档无法保存）

**位置**：`WebConsoleServer.cs:300-303`（`/api/config/` 前缀放宽到 `MaxAdminBodyBytes` = 260 KiB）、`WebConsoleOptions.cs:57`、`WebConsoleConfigEdit.cs:30`（内容上限 256 KiB）。body 是 JSON 包装后的字符串（`{"content":"…"}`），`"`/`\`/换行会 2 倍膨胀。

**影响**：256 KiB 的合法文档若含较多 `"`、`\`、换行（如内嵌证书/多行脚本），JSON body 会超过 260 KiB，在传输层被 413，永远存不进去；插件侧校验却认为合法（`Phase1UnchangedTests.cs:173` 只测预算常量，未测包装膨胀）。

**修复方向**：传输预算按 `内容上限 × 2 + 头部余量` 计算（或直接用 base64/原始 `text/plain` 提交），并在 413 消息中区分"transport cap"与"document cap"。

**状态**：代码确认。

---

### WC-13（P3）请求体按 UTF-8 有损解码

**位置**：`WebConsoleServer.cs:328`：`body = Encoding.UTF8.GetString(buffer.ToArray());`（`Encoding.UTF8` 默认替换非法字节为 U+FFFD）。

**影响**：含非法字节的保存请求会被"安静地"改写为替换字符后写入配置文件（数据损坏，且 ETag 与用户提交内容不再对应）；同时无法通过 400 明确拒绝非法编码。

**修复方向**：用 `new UTF8Encoding(false, throwOnInvalidBytes: true)` 解码，`DecoderFallbackException` → 400 `config.encoding_invalid`。

**状态**：代码确认。

---

### WC-14（P3，待确认）external 模式仅凭响应形状信任端口上的任意本地服务

**位置**：`WebConsoleRuntime.cs:153-165`（端口被占用且探测"像控制台"→ 进入 external 模式，`_server = null`）、`379-415`（`IsExistingConsoleAsync` 只校验 200/401 + JSON 里有布尔 `authenticated`）。

```csharp
return document.RootElement.ValueKind == JsonValueKind.Object
       && document.RootElement.TryGetProperty("authenticated", out var authenticated)
       && authenticated.ValueKind is JsonValueKind.True or JsonValueKind.False;
```

**影响**：任何先占端口的本地进程都能通过该探测，插件在 `web status`/UI 上继续宣称"控制台在 <url>"；操作者若照此访问并输入 admin token，token 落到冒名服务。属可钓鱼的信任问题（不由插件建立 TLS 或无凭据身份绑定）。

**修复方向**：external 模式要求额外挑战（例如启动时生成一次性 `X-WebConsole-Instance` 值并写入状态目录，探测时校验），或在状态里明确标注"身份未验证"并拒绝当作可用控制台。

**状态**：待确认（需要运维是否依赖 external 模式的事实；代码路径已确认）。

---

## 4. 授权与命令终端

结论：**端点级角色闸门是完整的**，未发现可绕过 `Manage` 的写路径。

- `Classify` 默认把未知动词归为 `Manage`（`WebConsoleAuthorization.cs:31-45`），GET/HEAD/OPTIONS 与 `*/validate` 为 `Read`；`IsAllowed` 只在 `Admin` 放行 `Manage`（`:47-48`）；handler 在路由前统一执行（`WebConsoleRequestHandler.cs:157-167`），角色拒绝优先于只读码（测试 `RoleEnforcementTests.cs:173`）。
- 所有写端点（`config` POST、`config/{id}/rollback`、`commands` POST）都二次检查 `ReadOnly` 与功能开关（`WebConsoleRequestHandler.cs:648-658, 686-696, 885-895`），错误码稳定。
- 配置 id 白名单双层：`WebConsoleConfigEditPolicy.IsAllowedId`（`WebConsoleConfigEdit.cs:46-58`，只允许 `profiles` / `plugin:<[A-Za-z0-9._-]>`）+ 宿主 `AdminConfigDocumentPolicy.TryNormalize`，`..`/绝对路径/分隔符被拒（`AdminContractParityTests.cs:32-41` 对拍）。
- 命令终端：**不启子进程、不拼 shell**。插件侧 `WebConsoleCommandPolicy.Evaluate`（`WebConsoleCommandPolicy.cs:35-64`）做规范化+精确 allowlist+控制字符/长度/条数限制；宿主 `CliAdminCommandRunner.RunAsync` 再复核自己的 allowlist、CLI registry、`CliParser`（argv 语义），并用进程级 `SemaphoreSlim` 串行（`CliAdminCommandRunner.cs:51-99`）。因此"参数注入/命令拼接/exec 链"在本实现中不成立；风险降级为"运维把危险命令放进 allowlist"（部署侧）。
- `viewer` 能 `POST /api/commands/validate`（只返回判定，不执行），符合设计；但它同时暴露 allowlist（`/api/commands` GET），属预期。

> 说明：本仓库只实现 **viewer / admin 两角色**（`WebConsoleRole`），设计文档亦为两角色（`docs/WEBCONSOLE_PLUGIN_DESIGN.md:225-231`），不存在"operator"角色；如需三权分立，是功能缺口而非实现偏差。

---

## 5. Web 层（CSRF / XSS / 穿越 / SSRF / 响应头）

- **CSRF**：每个会话独立 `Csrf`（`WebConsoleAuth.cs:31-48`），写请求必须带 `X-WebConsole-CSRF`，比较走 `FixedTimeEquals`（`WebConsoleRequestHandler.cs:1015-1018`，`WebConsoleAuth.cs:331-341`）；缺失/错误返回 403 `csrf.invalid`，登出同样受保护（`:314-318`）。`SameSite=Strict` + `HttpOnly`（`:346-352`）。除 WC-07（登录）外未发现状态变更接口缺 token。
- **Origin**：`WebConsoleOriginPolicy` 白名单为 loopback 三形态 + `publicBaseUrl`，`cross-site` 一律拒（`WebConsoleAuth.cs:308-320`）；缺 `Origin` 放行（对非浏览器客户端必要，但也是 WC-07 的前提）。
- **XSS**：服务端不回显 HTML；前端全部 `textContent`（`wwwroot/app.js:101-110, 166-174`，`grep innerHTML|insertAdjacentHTML|document.write|eval` 无命中）；CSP `default-src 'none'; script-src 'self' …; base-uri 'none'; form-action 'none'; frame-ancestors 'none'`（`WebConsoleRequestHandler.cs:23-25`），并附 `nosniff`/`X-Frame-Options: DENY`/`no-referrer`/COOP/CORP/Permissions-Policy（`:14-26`），对所有响应统一施加（`:1104-1111`）。index.html 无内联脚本/样式（仅 `<script src="/app.js">`），与 `script-src 'self'` 相容。
- **路径穿越**：静态资源是 3 条硬编码路由的字典（`WebConsoleServer.cs:69-131`），不存在任意文件读取；日志导出无路径参数；配置 id 白名单见 §4；审计目录来自 `NATTER_STATE_DIR`（`WebConsoleAuditPaths.cs:33-55`），不接受请求输入。
- **SSRF**：唯一出站请求是 `HealthEndpointHostPort` → `hostHealthUrl`，被 `WebConsoleOptionsValidator.TryParseLoopbackUrl` 强制 loopback + 路径 `/health`（`WebConsoleOptions.cs:207-224`），`AllowAutoRedirect=false`，响应 ≤256 KiB，2s 超时（`WebConsoleHostPort.cs:199-338`）。未发现用户可控 URL。
- **开放重定向 / CORS**：无任何 `Location` 重定向、无 `Access-Control-Allow-*` 响应头。
- **Host 头**：未显式校验，但 `HttpListener` 只注册了 `http://{bind}:{port}/` 单一前缀，Host 不匹配由 http.sys 拒绝；`bind` 又被校验为 loopback（`WebConsoleOptions.cs:81-87`）。属"依赖传输层"，建议将来迁移 Kestrel 时补显式 Host 校验。
- **JSON 反序列化**：只读 `JsonDocument`（默认 MaxDepth 64），无多态/类型名解析；`PromptLibrary` 的 `JsonSerializer.Deserialize<PromptLibraryFile>` 同样无类型判别。未发现反序列化 gadget 面。
- **日志导出**：`text/plain` + `attachment` + `nosniff`，NDJSON 自实现转义覆盖 `"`/`\`/控制字符（`WebConsoleLogsPolicy.cs:355-390`）；字节预算按 UTF-8 计（`:228-238`）。

---

## 6. PromptLibrary

总体判断：**路径与信任策略实现得比同类插件更严**，未发现越权读取或提示词注入的直接通道。

已验证的防护：

- 路径：拒绝绝对路径、`..`、盘符、符号链接/重解析点，并做 `IsUnder` 复核（`PromptLibraryLoader.cs:383-406, 458-486`）；大小与严格 UTF-8 校验（`:409-455`）；只读，从不写。
- 来源：`mandatory: true` 被拒绝（`PromptLibraryLoader.cs:218-221`），与宿主 `PromptComposer` 的保留 id 校验形成闭环。**探针 1 验证**：库内声明 `host.safety.untrusted-data` 会被宿主以 `prompt.fragment_reserved` 拒绝（`PromptComposer.cs:208-215`），safety 块无法被遮蔽：

```text
compose rejected            : prompt.fragment_reserved
```

- 覆盖顺序：`extends` 链 base→child、child 按 `fragmentId` 覆盖 base section、变量 base→child（`PromptComposer.cs:330-368`）；`presetFile` 的 fragments/presets 追加在 config 之后，重复 id 直接启动失败（`PromptLibraryLoader.cs:117-136`，fail-closed 而非静默覆盖）——比"后者覆盖前者"更安全。
- 正文不外泄：诊断只输出 id/版本/hash/warning（`PromptLibraryRuntime.cs:78-115`），失败只记录 `code`（`PromptLibraryProvider.cs:75-85`），`PromptPlan.Explain` 不含正文。

### PL-01（P3）库级无片段数量/总量配额，且以"宿主信任级"传入 composer

**位置**：`PromptLibraryLoader.cs:102-159`（无 `fragments/presets` 数量与总字节上限）、`PromptLibraryProvider.cs:43-49`（`FromPromptRequest(request, library.Presets, library.Fragments, …)` → **`Fragments` 参数**）、`PromptComposer.cs:196-204`（`PluginMaxFragments=64` 只约束 `PluginFragments`）。

**影响**：配置里声明上万条片段可通过加载与每次编译（`ToDictionary` + 全量 hash），单请求成本随配置线性增长；与插件片段的 64 条配额口径不一致，配置写错时表现为"内存/CPU 抖动"而不是可读错误。config 属可信输入，故 P3。

**修复方向**：加载期加上限（如 ≤256 片段、总字节 ≤1 MiB）并给稳定错误码；或在 composer 对 `Fragments` 也应用条数上限。

**状态**：代码确认。

### PL-02（P3，待确认）片段文件"先查链接再读"存在 TOCTOU

**位置**：`PromptLibraryLoader.cs:383-406`（`ResolveUnder` → `EnsureNoLink`）、`409-438`（随后 `File.ReadAllBytes(path)`）。

**影响**：对插件目录有写权限的本地用户可在检查与读取之间把文件换成指向目录外的符号链接，读取任意可读文件并使其成为 prompt 片段（内容进入系统提示 = 提示词注入 + 文件读取）。前提是本地写权限，威胁模型较窄。

**修复方向**：打开时用 `FileStream` + `FileOptions` 并校验 `FileInfo.LinkTarget`（或 Linux 下 `O_NOFOLLOW`）；或一次性把 fragment 内容复制到受控目录再加载。

**状态**：待确认（依赖部署是否把插件目录设为可写；默认安装为只读挂载）。

---

## 7. 测试是否真的覆盖了风险

**覆盖得好的部分**

- 角色强制：`Phase23/RoleEnforcementTests.cs`（viewer 读全量 + 两个 `/validate`、viewer 写/命令 403、`auth.forbidden` 优先于 `read_only`）。
- 只读/开关错误码：`Phase23/ReadOnlyCodesTests.cs`。
- 配置编辑契约、并发单写者、符号链接文档、路径型 id：`Phase23Acceptance/ConfigPhase2AcceptanceTests.cs`、`Phase23/ConfigPhase2HttpTests.cs`。
- 命令 allowlist/控制字符/超时/输出截断与脱敏：`Phase23/CommandPhase3HttpTests.cs`、`Phase23Acceptance/CommandPhase3AcceptanceTests.cs`。
- 日志过滤/分页/导出边界与转义：`Phase23/LogsPolicyTests.cs`、`LogsFilterExportTests.cs`。
- 审计目录权限/状态根：`Phase23/AuditPathTests.cs`。
- 宿主-插件契约对拍（ETag、id 白名单、secret 字面量、命令规范化、输出预算）：`Phase23/AdminContractParityTests.cs`。
- PromptLibrary 路径/符号链接/大小/UTF-8/重复/引用/cycle/mandatory/日志无正文：`tests/Natter.Plugin.PromptLibrary.Tests/Loader|Provider|Plugin|NpsIntegration`。

**未被覆盖的风险（与本文发现一一对应）**

| 发现 | 测试缺口 |
| --- | --- |
| WC-01 | `ConfigPhase2HttpTests.cs:85-98` 只断言 **GET 回显**脱敏；`:355-380` 的 validate 测试只断言 `diff.removed == 1`，从不检查 `diff.lines` 内容，也没有"viewer 调 validate 不得看到 `sk-`"的断言 —— 而这正是 `RoleEnforcementTests.cs:130` 明确放行的路径 |
| WC-02 | `WebConsoleAuthTests.cs:47-78` 只测单客户端窗口；没有"50 次全局失败后 Reset 是否解锁""正确 token 是否被全局锁死"的用例（探针 3 直接暴露） |
| WC-03 | `WebConsoleRequestHandlerTests.cs:313-322` 只测"受信代理时 XFF 生效"，没有"轮换 XFF 绕过每客户端限速""审计 IP 不可伪造"的用例 |
| WC-04 | `WebConsoleServerTests.cs` 无慢速 body / 并发上限用例（也不易单测），设计文档也未把"读 body 超时"列为契约 |
| WC-05 | `AdminContractParityTests.cs:67-73` 只对 `sk-` 做一次对拍，没有规则集层面的对拍（PEM/多行/键值对大小写变体），于是"两者规则一致"的文档结论没有被测试守住 |
| WC-06 | 无"未认证请求不得写审计/不得触发轮转"的用例；`LogsFilterExportTests` 只覆盖读取侧 |
| WC-07 | 无"无 Origin 的 POST /api/session 应被拒"的用例（`AcceptanceHarness.cs:499` 恒发 Origin，掩盖了该路径） |
| WC-09 | `WebConsoleAuthTests.cs:91-99` 断言了 `RequiresSecureCookie` 的值，却没断言它影响 `Set-Cookie` —— 死代码被测试"保护"而非被使用 |
| PL-01/PL-02 | 无片段数量/总量上限用例；符号链接只测"加载前已存在"的静态情形 |

---

## 8. Top 8 发现（含 file:line）

| # | 级别 | 发现 | 关键位置 |
| --- | --- | --- | --- |
| 1 | P1 | `/validate` diff 原样回传，viewer 可读未脱敏配置正文（含明文 secret），绕过 `readOnly`/`allowConfigWrite` | `WebConsoleRequestHandler.cs:834`、`WebConsoleAdminGateway.cs:579-582` |
| 2 | P2 | 全局登录失败上限不可恢复，50 次失败即可锁死 admin 登录 5 分钟且可无限续期 | `WebConsoleAuth.cs:219-223`、`:254-258` |
| 3 | P2 | 客户端键取 `X-Forwarded-For` 最左值且默认信任 loopback 代理 → 限速按客户端失效、审计 IP 可伪造 | `WebConsoleRequestHandler.cs:1020-1032`、`WebConsoleOptions.cs:40` |
| 4 | P2 | 请求体读取无超时 + 每连接无并发上限（HttpListener 队列可被慢速请求占满） | `WebConsoleServer.cs:265`、`:305-329` |
| 5 | P2 | 脱敏缺 PEM 私钥块；宿主 `IAdminRedactor` 解析后从未使用（文档称"规则一致/双重脱敏"） | `WebConsoleConfig.cs:58-67`、`WebConsoleAdminSurface.cs:15,35` |
| 6 | P2 | 未认证的 `origin.rejected` 无限写审计 + 1 MiB 轮转覆盖 `.1` → 取证历史可被任意网页刷掉 | `WebConsoleRequestHandler.cs:122-126`、`WebConsoleAudit.cs:158-166` |
| 7 | P3 | 登录端点无 CSRF/Origin 强绑定（login CSRF） | `WebConsoleRequestHandler.cs:141-142`、`:305-306` |
| 8 | P3 | `/undo` 向 viewer 泄漏宿主备份绝对路径、`/audit` 泄漏他人 IP；宿主异常消息带路径回传 | `WebConsoleAdminGateway.cs:361-380`、`:584-590`、`WebConsoleRequestHandler.cs:624-635` |

---

## 9. 最关键的 5 个文件

1. `plugins/Natter.Plugin.WebConsole/WebConsoleRequestHandler.cs`（1180 行）—— 路由/认证/CSRF/角色/只读闸门/审计的唯一汇聚点；WC-01/03/06/07/10 都在这里落地。
2. `plugins/Natter.Plugin.WebConsole/WebConsoleAuth.cs`（372 行）—— 令牌常量时间比较、会话、限速、Origin、Cookie；WC-02/07/09。
3. `plugins/Natter.Plugin.WebConsole/WebConsoleAdminGateway.cs`（619 行）—— 宿主契约与浏览器之间的脱敏/裁剪边界；WC-01/10/11。
4. `plugins/Natter.Plugin.WebConsole/WebConsoleServer.cs`（424 行）—— 传输层预算、超时、并发、静态资源；WC-04/12/13。
5. `plugins/Natter.Plugin.WebConsole/WebConsoleConfig.cs`（173 行）—— 统一的脱敏实现（配置回显/日志/命令输出/审计 target 共用）；WC-05。

次要但相关：`plugins/Natter.Plugin.WebConsole/WebConsoleAudit.cs`（WC-06）、`plugins/Natter.Plugin.PromptLibrary/PromptLibraryLoader.cs` 与 `PromptLibraryProvider.cs`（PL-01/02）。

---

## 10. 修复优先级建议

1. **先堵 WC-01**（1 行级改动 + 一个测试）：`MapDiff` 对每行 `RedactSecrets`，或对 `Read` 权限隐藏 `-` 行。这是唯一的 P1。
2. **再改 WC-02/WC-06**：全局限速改成"退避 + 告警"并在成功登录时同步清理；未认证路径的审计采样限速、轮转保留多份。二者都是"可用性/取证"级问题，改动小、收益直接。
3. **WC-03/WC-04/WC-05**：XFF 取最右、`trustedProxies` 默认空、限制在途请求数与 body 读取超时、补 PEM 规则并接入 `IAdminRedactor`。这一批是"部署到 `publicBaseUrl` 形态前必须完成"的硬化项。
4. P3 批量收尾（Cookie 前缀/Secure 判定、undo/audit 字段分级、异常消息外置、body 预算、严格 UTF-8、external 模式身份、PromptLibrary 配额与 TOCTOU）。

---

## 附录 A · 探针

- 位置：`artifacts/review/probe/`（`probe.csproj` + `Program.cs`，仅引用 `Natter.Application` 与 `Natter.Plugin.WebConsole` 项目，不修改仓库源码）
- 运行：`cd artifacts/review/probe && dotnet run`
- 原始输出：

```text
== probe 0: PEM redaction ==
plugin RedactSecrets unchanged : True
plugin SanitizeText unchanged  : True
host AdminRedactor unchanged   : False

== probe 1: shadow host.safety.untrusted-data ==
compose rejected            : prompt.fragment_reserved

== probe 2: config validate diff vs read redaction ==
read.content                : {\n  "schemaVersion": 1,\n  "apiKey": "[redacted]",\n  "tls": "-----BEGIN RSA PRIVATE KEY-----"\n}\n
read.contentRedacted        : True
validate.diff.lines         : -{ | -  "schemaVersion": 1, | -  "apiKey": "sk-plaintextsecret1234", | -  "tls": "-----BEGIN RSA PRIVATE KEY-----" | -} | +{}
validate.diff leaks apiKey  : True

== probe 3: global throttle ==
blocked after Reset          : True
retryAfter                   : 00:04:59.9948608
```

probe 1 的"被拒绝"是**预期正确行为**（宿主 `prompt.fragment_reserved` 守住了 safety 块），记录在此以免后续误报为缺陷。
